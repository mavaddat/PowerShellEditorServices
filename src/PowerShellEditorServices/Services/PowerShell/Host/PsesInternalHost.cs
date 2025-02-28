﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Management.Automation.Host;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.PowerShell.EditorServices.Hosting;
using Microsoft.PowerShell.EditorServices.Services.PowerShell.Console;
using Microsoft.PowerShell.EditorServices.Services.PowerShell.Context;
using Microsoft.PowerShell.EditorServices.Services.PowerShell.Debugging;
using Microsoft.PowerShell.EditorServices.Services.PowerShell.Execution;
using Microsoft.PowerShell.EditorServices.Services.PowerShell.Runspace;
using Microsoft.PowerShell.EditorServices.Services.PowerShell.Utility;
using Microsoft.PowerShell.EditorServices.Utility;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;

namespace Microsoft.PowerShell.EditorServices.Services.PowerShell.Host
{
    using System.Management.Automation;
    using System.Management.Automation.Runspaces;

    internal class PsesInternalHost : PSHost, IHostSupportsInteractiveSession, IRunspaceContext, IInternalPowerShellExecutionService
    {
        private const string DefaultPrompt = "PSIC> ";
        // This is a default that can be overriden at runtime by the user or tests.
        private static string s_bundledModulePath = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(typeof(PsesInternalHost).Assembly.Location), "..", "..", ".."));

        private static string CommandsModulePath => Path.GetFullPath(Path.Combine(
            s_bundledModulePath, "PowerShellEditorServices", "Commands", "PowerShellEditorServices.Commands.psd1"));

        private readonly ILoggerFactory _loggerFactory;

        private readonly ILogger _logger;

        private readonly ILanguageServerFacade _languageServer;

        private readonly HostStartupInfo _hostInfo;

        private readonly BlockingConcurrentDeque<ISynchronousTask> _taskQueue;

        private readonly Stack<PowerShellContextFrame> _psFrameStack;

        private readonly Stack<RunspaceFrame> _runspaceStack;

        private readonly CancellationContext _cancellationContext;

        private readonly ReadLineProvider _readLineProvider;

        private readonly Thread _pipelineThread;

        private readonly IdempotentLatch _isRunningLatch = new();

        private readonly TaskCompletionSource<bool> _started = new();

        private readonly TaskCompletionSource<bool> _stopped = new();

        private EngineIntrinsics _mainRunspaceEngineIntrinsics;

        private bool _shouldExit = false;

        private int _shuttingDown = 0;

        private string _localComputerName;

        private ConsoleKeyInfo? _lastKey;

        private bool _skipNextPrompt = false;

        private bool _resettingRunspace = false;

        public PsesInternalHost(
            ILoggerFactory loggerFactory,
            ILanguageServerFacade languageServer,
            HostStartupInfo hostInfo)
        {
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<PsesInternalHost>();
            _languageServer = languageServer;
            _hostInfo = hostInfo;

            // Respect a user provided bundled module path.
            if (Directory.Exists(hostInfo.BundledModulePath))
            {
                _logger.LogTrace("Using new bundled module path: {}", hostInfo.BundledModulePath);
                s_bundledModulePath = hostInfo.BundledModulePath;
            }

            _readLineProvider = new ReadLineProvider(loggerFactory);
            _taskQueue = new BlockingConcurrentDeque<ISynchronousTask>();
            _psFrameStack = new Stack<PowerShellContextFrame>();
            _runspaceStack = new Stack<RunspaceFrame>();
            _cancellationContext = new CancellationContext();

            _pipelineThread = new Thread(Run)
            {
                Name = "PSES Pipeline Execution Thread",
            };

            if (VersionUtils.IsWindows)
            {
                _pipelineThread.SetApartmentState(ApartmentState.STA);
            }

            PublicHost = new EditorServicesConsolePSHost(this);
            Name = hostInfo.Name;
            Version = hostInfo.Version;

            DebugContext = new PowerShellDebugContext(loggerFactory, this);
            UI = hostInfo.ConsoleReplEnabled
                ? new EditorServicesConsolePSHostUserInterface(loggerFactory, _readLineProvider, hostInfo.PSHost.UI)
                : new NullPSHostUI();
        }

        public override CultureInfo CurrentCulture => _hostInfo.PSHost.CurrentCulture;

        public override CultureInfo CurrentUICulture => _hostInfo.PSHost.CurrentUICulture;

        public override Guid InstanceId { get; } = Guid.NewGuid();

        public override string Name { get; }

        public override PSHostUserInterface UI { get; }

        public override Version Version { get; }

        public bool IsRunspacePushed { get; private set; }

        public Runspace Runspace => _runspaceStack.Peek().Runspace;

        public RunspaceInfo CurrentRunspace => CurrentFrame.RunspaceInfo;

        public PowerShell CurrentPowerShell => CurrentFrame.PowerShell;

        public EditorServicesConsolePSHost PublicHost { get; }

        public PowerShellDebugContext DebugContext { get; }

        public bool IsRunning => _isRunningLatch.IsSignaled;

        public string InitialWorkingDirectory { get; private set; }

        public Task Shutdown => _stopped.Task;

        IRunspaceInfo IRunspaceContext.CurrentRunspace => CurrentRunspace;

        private PowerShellContextFrame CurrentFrame => _psFrameStack.Peek();

        public event Action<object, RunspaceChangedEventArgs> RunspaceChanged;

        private bool ShouldExitExecutionLoop => _shouldExit || _shuttingDown != 0;

        public override void EnterNestedPrompt()
        {
            PushPowerShellAndRunLoop(CreateNestedPowerShell(CurrentRunspace), PowerShellFrameType.Nested);
        }

        public override void ExitNestedPrompt()
        {
            SetExit();
        }

        public override void NotifyBeginApplication()
        {
            // TODO: Work out what to do here
        }

        public override void NotifyEndApplication()
        {
            // TODO: Work out what to do here
        }

        public void PopRunspace()
        {
            IsRunspacePushed = false;
            SetExit();
        }

        public void PushRunspace(Runspace runspace)
        {
            IsRunspacePushed = true;
            PushPowerShellAndRunLoop(CreatePowerShellForRunspace(runspace), PowerShellFrameType.Remote);
        }

        public override void SetShouldExit(int exitCode)
        {
            // TODO: Handle exit code if needed
            SetExit();
        }

        /// <summary>
        /// Try to start the PowerShell loop in the host.
        /// If the host is already started, this is idempotent.
        /// Returns when the host is in a valid initialized state.
        /// </summary>
        /// <param name="startOptions">Options to configure host startup.</param>
        /// <param name="cancellationToken">A token to cancel startup.</param>
        /// <returns>A task that resolves when the host has finished startup, with the value true if the caller started the host, and false otherwise.</returns>
        public async Task<bool> TryStartAsync(HostStartOptions startOptions, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Host starting");
            if (!_isRunningLatch.TryEnter())
            {
                _logger.LogDebug("Host start requested after already started.");
                await _started.Task.ConfigureAwait(false);
                return false;
            }

            _pipelineThread.Start();

            if (startOptions.LoadProfiles)
            {
                await LoadHostProfilesAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Profiles loaded");
            }

            if (startOptions.InitialWorkingDirectory is not null)
            {
                await SetInitialWorkingDirectoryAsync(startOptions.InitialWorkingDirectory, CancellationToken.None).ConfigureAwait(false);
            }

            await _started.Task.ConfigureAwait(false);
            return true;
        }

        public Task StopAsync()
        {
            TriggerShutdown();
            return Shutdown;
        }

        public void TriggerShutdown()
        {
            if (Interlocked.Exchange(ref _shuttingDown, 1) == 0)
            {
                _cancellationContext.CancelCurrentTaskStack();
                // NOTE: This is mostly for sanity's sake, as during debugging of tests I became
                // concerned that the repeated creation and disposal of the host was not also
                // joining and disposing this thread, leaving the tests in a weird state. Because
                // the tasks have been canceled, we should be able to join this thread.
                _pipelineThread.Join();
            }
        }

        public void SetExit()
        {
            // Can't exit from the top level of PSES
            // since if you do, you lose all LSP services
            if (_psFrameStack.Count <= 1)
            {
                return;
            }

            _shouldExit = true;
        }

        public Task<T> InvokeTaskOnPipelineThreadAsync<T>(
            SynchronousTask<T> task)
        {
            if (task.ExecutionOptions.InterruptCurrentForeground)
            {
                // When a task must displace the current foreground command,
                // we must:
                //  - block the consumer thread from mutating the queue
                //  - cancel any running task on the consumer thread
                //  - place our task on the front of the queue
                //  - skip the next prompt so the task runs instead
                //  - unblock the consumer thread
                using (_taskQueue.BlockConsumers())
                {
                    CancelCurrentTask();
                    _taskQueue.Prepend(task);
                    _skipNextPrompt = true;
                }

                return task.Task;
            }

            switch (task.ExecutionOptions.Priority)
            {
                case ExecutionPriority.Next:
                    _taskQueue.Prepend(task);
                    break;

                case ExecutionPriority.Normal:
                    _taskQueue.Append(task);
                    break;
            }

            return task.Task;
        }

        public void CancelCurrentTask()
        {
            _cancellationContext.CancelCurrentTask();
        }

        public Task<TResult> ExecuteDelegateAsync<TResult>(
            string representation,
            ExecutionOptions executionOptions,
            Func<PowerShell, CancellationToken, TResult> func,
            CancellationToken cancellationToken)
        {
            return InvokeTaskOnPipelineThreadAsync(
                new SynchronousPSDelegateTask<TResult>(_logger, this, representation, executionOptions, func, cancellationToken));
        }

        public Task ExecuteDelegateAsync(
            string representation,
            ExecutionOptions executionOptions,
            Action<PowerShell, CancellationToken> action,
            CancellationToken cancellationToken)
        {
            return InvokeTaskOnPipelineThreadAsync(
                new SynchronousPSDelegateTask(_logger, this, representation, executionOptions, action, cancellationToken));
        }

        public Task<TResult> ExecuteDelegateAsync<TResult>(
            string representation,
            ExecutionOptions executionOptions,
            Func<CancellationToken, TResult> func,
            CancellationToken cancellationToken)
        {
            return InvokeTaskOnPipelineThreadAsync(
                new SynchronousDelegateTask<TResult>(_logger, representation, executionOptions, func, cancellationToken));
        }

        public Task ExecuteDelegateAsync(
            string representation,
            ExecutionOptions executionOptions,
            Action<CancellationToken> action,
            CancellationToken cancellationToken)
        {
            return InvokeTaskOnPipelineThreadAsync(
                new SynchronousDelegateTask(_logger, representation, executionOptions, action, cancellationToken));
        }

        public Task<IReadOnlyList<TResult>> ExecutePSCommandAsync<TResult>(
            PSCommand psCommand,
            CancellationToken cancellationToken,
            PowerShellExecutionOptions executionOptions = null)
        {
            return InvokeTaskOnPipelineThreadAsync(
                new SynchronousPowerShellTask<TResult>(_logger, this, psCommand, executionOptions, cancellationToken));
        }

        public Task ExecutePSCommandAsync(
            PSCommand psCommand,
            CancellationToken cancellationToken,
            PowerShellExecutionOptions executionOptions = null)
        {
            return ExecutePSCommandAsync<PSObject>(psCommand, cancellationToken, executionOptions);
        }

        public TResult InvokeDelegate<TResult>(string representation, ExecutionOptions executionOptions, Func<CancellationToken, TResult> func, CancellationToken cancellationToken)
        {
            var task = new SynchronousDelegateTask<TResult>(_logger, representation, executionOptions, func, cancellationToken);
            return task.ExecuteAndGetResult(cancellationToken);
        }

        public void InvokeDelegate(string representation, ExecutionOptions executionOptions, Action<CancellationToken> action, CancellationToken cancellationToken)
        {
            var task = new SynchronousDelegateTask(_logger, representation, executionOptions, action, cancellationToken);
            task.ExecuteAndGetResult(cancellationToken);
        }

        public IReadOnlyList<TResult> InvokePSCommand<TResult>(PSCommand psCommand, PowerShellExecutionOptions executionOptions, CancellationToken cancellationToken)
        {
            var task = new SynchronousPowerShellTask<TResult>(_logger, this, psCommand, executionOptions, cancellationToken);
            return task.ExecuteAndGetResult(cancellationToken);
        }

        public void InvokePSCommand(PSCommand psCommand, PowerShellExecutionOptions executionOptions, CancellationToken cancellationToken)
        {
            InvokePSCommand<PSObject>(psCommand, executionOptions, cancellationToken);
        }

        public TResult InvokePSDelegate<TResult>(string representation, ExecutionOptions executionOptions, Func<PowerShell, CancellationToken, TResult> func, CancellationToken cancellationToken)
        {
            var task = new SynchronousPSDelegateTask<TResult>(_logger, this, representation, executionOptions, func, cancellationToken);
            return task.ExecuteAndGetResult(cancellationToken);
        }

        public void InvokePSDelegate(string representation, ExecutionOptions executionOptions, Action<PowerShell, CancellationToken> action, CancellationToken cancellationToken)
        {
            var task = new SynchronousPSDelegateTask(_logger, this, representation, executionOptions, action, cancellationToken);
            task.ExecuteAndGetResult(cancellationToken);
        }

        internal Task LoadHostProfilesAsync(CancellationToken cancellationToken)
        {
            return ExecuteDelegateAsync(
                "LoadProfiles",
                new PowerShellExecutionOptions { MustRunInForeground = true, ThrowOnError = false },
                (pwsh, _) => pwsh.LoadProfiles(_hostInfo.ProfilePaths),
                cancellationToken);
        }

        public Task SetInitialWorkingDirectoryAsync(string path, CancellationToken cancellationToken)
        {
            InitialWorkingDirectory = path;

            return ExecutePSCommandAsync(
                new PSCommand().AddCommand("Set-Location").AddParameter("LiteralPath", path),
                cancellationToken);
        }

        private void Run()
        {
            try
            {
                (PowerShell pwsh, RunspaceInfo localRunspaceInfo, EngineIntrinsics engineIntrinsics) = CreateInitialPowerShellSession();
                _mainRunspaceEngineIntrinsics = engineIntrinsics;
                _localComputerName = localRunspaceInfo.SessionDetails.ComputerName;
                _runspaceStack.Push(new RunspaceFrame(pwsh.Runspace, localRunspaceInfo));
                PushPowerShellAndRunLoop(pwsh, PowerShellFrameType.Normal, localRunspaceInfo);
            }
            catch (Exception e)
            {
                _started.TrySetException(e);
                _stopped.TrySetException(e);
            }
        }

        private (PowerShell, RunspaceInfo, EngineIntrinsics) CreateInitialPowerShellSession()
        {
            (PowerShell pwsh, EngineIntrinsics engineIntrinsics) = CreateInitialPowerShell(_hostInfo, _readLineProvider);
            RunspaceInfo localRunspaceInfo = RunspaceInfo.CreateFromLocalPowerShell(_logger, pwsh);
            return (pwsh, localRunspaceInfo, engineIntrinsics);
        }

        private void PushPowerShellAndRunLoop(PowerShell pwsh, PowerShellFrameType frameType, RunspaceInfo newRunspaceInfo = null)
        {
            // TODO: Improve runspace origin detection here
            if (newRunspaceInfo is null)
            {
                newRunspaceInfo = GetRunspaceInfoForPowerShell(pwsh, out bool isNewRunspace, out RunspaceFrame oldRunspaceFrame);

                if (isNewRunspace)
                {
                    Runspace newRunspace = pwsh.Runspace;
                    _runspaceStack.Push(new RunspaceFrame(newRunspace, newRunspaceInfo));
                    RunspaceChanged.Invoke(this, new RunspaceChangedEventArgs(RunspaceChangeAction.Enter, oldRunspaceFrame.RunspaceInfo, newRunspaceInfo));
                }
            }

            PushPowerShellAndRunLoop(new PowerShellContextFrame(pwsh, newRunspaceInfo, frameType));
        }

        private RunspaceInfo GetRunspaceInfoForPowerShell(PowerShell pwsh, out bool isNewRunspace, out RunspaceFrame oldRunspaceFrame)
        {
            oldRunspaceFrame = null;

            if (_runspaceStack.Count > 0)
            {
                // This is more than just an optimization.
                // When debugging, we cannot execute PowerShell directly to get this information;
                // trying to do so will block on the command that called us, deadlocking execution.
                // Instead, since we are reusing the runspace, we reuse that runspace's info as well.
                oldRunspaceFrame = _runspaceStack.Peek();
                if (oldRunspaceFrame.Runspace == pwsh.Runspace)
                {
                    isNewRunspace = false;
                    return oldRunspaceFrame.RunspaceInfo;
                }
            }

            isNewRunspace = true;
            return RunspaceInfo.CreateFromPowerShell(_logger, pwsh, _localComputerName);
        }

        private void PushPowerShellAndRunLoop(PowerShellContextFrame frame)
        {
            PushPowerShell(frame);

            try
            {
                if (_psFrameStack.Count == 1)
                {
                    RunTopLevelExecutionLoop();
                }
                else if ((frame.FrameType & PowerShellFrameType.Debug) != 0)
                {
                    RunDebugExecutionLoop();
                }
                else
                {
                    RunExecutionLoop();
                }
            }
            finally
            {
                PopPowerShell();
            }
        }

        private void PushPowerShell(PowerShellContextFrame frame)
        {
            if (_psFrameStack.Count > 0)
            {
                RemoveRunspaceEventHandlers(CurrentFrame.PowerShell.Runspace);
            }

            AddRunspaceEventHandlers(frame.PowerShell.Runspace);

            _psFrameStack.Push(frame);
        }

        private void PopPowerShell(RunspaceChangeAction runspaceChangeAction = RunspaceChangeAction.Exit)
        {
            _shouldExit = false;
            PowerShellContextFrame frame = _psFrameStack.Pop();
            try
            {
                // If we're changing runspace, make sure we move the handlers over. If we just
                // popped the last frame, then we're exiting and should pop the runspace too.
                if (_psFrameStack.Count == 0 || CurrentRunspace.Runspace != CurrentPowerShell.Runspace)
                {
                    RunspaceFrame previousRunspaceFrame = _runspaceStack.Pop();
                    RemoveRunspaceEventHandlers(previousRunspaceFrame.Runspace);

                    // If there is still a runspace on the stack, then we need to re-register the
                    // handlers. Otherwise we're exiting and so don't need to run 'RunspaceChanged'.
                    if (_runspaceStack.Count > 0)
                    {
                        RunspaceFrame newRunspaceFrame = _runspaceStack.Peek();
                        AddRunspaceEventHandlers(newRunspaceFrame.Runspace);
                        RunspaceChanged?.Invoke(
                            this,
                            new RunspaceChangedEventArgs(
                                runspaceChangeAction,
                                previousRunspaceFrame.RunspaceInfo,
                                newRunspaceFrame.RunspaceInfo));
                    }
                }
            }
            finally
            {
                frame.Dispose();
            }
        }

        private void RunTopLevelExecutionLoop()
        {
            try
            {
                // Make sure we execute any startup tasks first
                while (_taskQueue.TryTake(out ISynchronousTask task))
                {
                    task.ExecuteSynchronously(CancellationToken.None);
                }

                // Signal that we are ready for outside services to use
                _started.TrySetResult(true);

                RunExecutionLoop();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "PSES pipeline thread loop experienced an unexpected top-level exception");
                _stopped.TrySetException(e);
                return;
            }

            _logger.LogInformation("PSES pipeline thread loop shutting down");
            _stopped.SetResult(true);
        }

        private void RunDebugExecutionLoop()
        {
            try
            {
                DebugContext.EnterDebugLoop();
                RunExecutionLoop();
            }
            finally
            {
                DebugContext.ExitDebugLoop();
            }
        }

        private void RunExecutionLoop()
        {
            while (!ShouldExitExecutionLoop)
            {
                using CancellationScope cancellationScope = _cancellationContext.EnterScope(isIdleScope: false);
                DoOneRepl(cancellationScope.CancellationToken);

                while (!ShouldExitExecutionLoop
                    && !cancellationScope.CancellationToken.IsCancellationRequested
                    && _taskQueue.TryTake(out ISynchronousTask task))
                {
                    task.ExecuteSynchronously(cancellationScope.CancellationToken);
                }
            }
        }

        private void DoOneRepl(CancellationToken cancellationToken)
        {
            if (!_hostInfo.ConsoleReplEnabled)
            {
                // Throttle the REPL loop with a sleep because we're not interactively reading input from the user.
                Thread.Sleep(100);
                return;
            }

            // We use the REPL as a poll to check if the debug context is active but PowerShell
            // indicates we're no longer debugging. This happens when PowerShell was used to start
            // the debugger (instead of using a Code launch configuration) via Wait-Debugger or
            // simply hitting a PSBreakpoint. We need to synchronize the state and stop the debug
            // context (and likely the debug server).
            if (DebugContext.IsActive && !CurrentRunspace.Runspace.Debugger.InBreakpoint)
            {
                StopDebugContext();
            }

            // When a task must run in the foreground, we cancel out of the idle loop and return to the top level.
            // At that point, we would normally run a REPL, but we need to immediately execute the task.
            // So we set _skipNextPrompt to do that.
            if (_skipNextPrompt)
            {
                _skipNextPrompt = false;
                return;
            }

            try
            {
                string prompt = GetPrompt(cancellationToken);
                UI.Write(prompt);
                string userInput = InvokeReadLine(cancellationToken);

                // If the user input was empty it's because:
                //  - the user provided no input
                //  - the readline task was canceled
                //  - CtrlC was sent to readline (which does not propagate a cancellation)
                //
                // In any event there's nothing to run in PowerShell, so we just loop back to the prompt again.
                // However, we must distinguish the last two scenarios, since PSRL will not print a new line in those cases.
                if (string.IsNullOrEmpty(userInput))
                {
                    if (cancellationToken.IsCancellationRequested || LastKeyWasCtrlC())
                    {
                        UI.WriteLine();
                    }
                    return;
                }

                InvokeInput(userInput, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Do nothing, since we were just cancelled
            }
            catch (Exception e)
            {
                UI.WriteErrorLine($"An error occurred while running the REPL loop:{Environment.NewLine}{e}");
                _logger.LogError(e, "An error occurred while running the REPL loop");
            }
        }

        private string GetPrompt(CancellationToken cancellationToken)
        {
            string prompt = DefaultPrompt;
            try
            {
                // TODO: Should we cache PSCommands like this as static members?
                var command = new PSCommand().AddCommand("prompt");
                IReadOnlyList<string> results = InvokePSCommand<string>(command, executionOptions: null, cancellationToken);
                if (results.Count > 0)
                {
                    prompt = results[0];
                }
            }
            catch (CommandNotFoundException) { } // Use default prompt

            if (CurrentRunspace.RunspaceOrigin != RunspaceOrigin.Local)
            {
                // This is a PowerShell-internal method that we reuse to decorate the prompt string
                // with the remote details when remoting,
                // so the prompt changes to indicate when you're in a remote session
                prompt = Runspace.GetRemotePrompt(prompt);
            }

            return prompt;
        }

        /// <summary>
        /// This is used to write the invocation text of a command with the user's prompt so that,
        /// for example, F8 (evaluate selection) appears as if the user typed it. Used when
        /// 'WriteInputToHost' is true.
        /// </summary>
        /// <param name="command">The PSCommand we'll print after the prompt.</param>
        /// <param name="cancellationToken"></param>
        public void WriteWithPrompt(PSCommand command, CancellationToken cancellationToken)
        {
            UI.Write(GetPrompt(cancellationToken));
            UI.WriteLine(command.GetInvocationText());
        }

        private string InvokeReadLine(CancellationToken cancellationToken)
        {
            return _readLineProvider.ReadLine.ReadLine(cancellationToken);
        }

        private void InvokeInput(string input, CancellationToken cancellationToken)
        {
            var command = new PSCommand().AddScript(input, useLocalScope: false);
            InvokePSCommand(command, new PowerShellExecutionOptions { AddToHistory = true, ThrowOnError = false, WriteOutputToHost = true }, cancellationToken);
        }

        private void AddRunspaceEventHandlers(Runspace runspace)
        {
            runspace.Debugger.DebuggerStop += OnDebuggerStopped;
            runspace.Debugger.BreakpointUpdated += OnBreakpointUpdated;
            runspace.StateChanged += OnRunspaceStateChanged;
        }

        private void RemoveRunspaceEventHandlers(Runspace runspace)
        {
            runspace.Debugger.DebuggerStop -= OnDebuggerStopped;
            runspace.Debugger.BreakpointUpdated -= OnBreakpointUpdated;
            runspace.StateChanged -= OnRunspaceStateChanged;
        }

        private static PowerShell CreateNestedPowerShell(RunspaceInfo currentRunspace)
        {
            if (currentRunspace.RunspaceOrigin != RunspaceOrigin.Local)
            {
                return CreatePowerShellForRunspace(currentRunspace.Runspace);
            }

            // PowerShell.CreateNestedPowerShell() sets IsNested but not IsChild
            // This means it throws due to the parent pipeline not running...
            // So we must use the RunspaceMode.CurrentRunspace option on PowerShell.Create() instead
            var pwsh = PowerShell.Create(RunspaceMode.CurrentRunspace);
            pwsh.Runspace.ThreadOptions = PSThreadOptions.UseCurrentThread;
            return pwsh;
        }

        private static PowerShell CreatePowerShellForRunspace(Runspace runspace)
        {
            var pwsh = PowerShell.Create();
            pwsh.Runspace = runspace;
            return pwsh;
        }

        private (PowerShell, EngineIntrinsics) CreateInitialPowerShell(
            HostStartupInfo hostStartupInfo,
            ReadLineProvider readLineProvider)
        {
            Runspace runspace = CreateInitialRunspace(hostStartupInfo.InitialSessionState);
            PowerShell pwsh = CreatePowerShellForRunspace(runspace);

            var engineIntrinsics = (EngineIntrinsics)runspace.SessionStateProxy.GetVariable("ExecutionContext");

            if (hostStartupInfo.ConsoleReplEnabled)
            {
                // If we've been configured to use it, or if we can't load PSReadLine, use the legacy readline
                if (hostStartupInfo.UsesLegacyReadLine || !TryLoadPSReadLine(pwsh, engineIntrinsics, out IReadLine readLine))
                {
                    readLine = new LegacyReadLine(this, ReadKey, OnPowerShellIdle);
                }

                readLineProvider.OverrideReadLine(readLine);
                System.Console.CancelKeyPress += OnCancelKeyPress;
                System.Console.InputEncoding = Encoding.UTF8;
                System.Console.OutputEncoding = Encoding.UTF8;
            }

            if (VersionUtils.IsWindows)
            {
                pwsh.SetCorrectExecutionPolicy(_logger);
            }

            pwsh.ImportModule(CommandsModulePath);

            if (hostStartupInfo.AdditionalModules?.Count > 0)
            {
                foreach (string module in hostStartupInfo.AdditionalModules)
                {
                    pwsh.ImportModule(module);
                }
            }

            return (pwsh, engineIntrinsics);
        }

        private Runspace CreateInitialRunspace(InitialSessionState initialSessionState)
        {
            Runspace runspace = RunspaceFactory.CreateRunspace(PublicHost, initialSessionState);

            runspace.SetApartmentStateToSta();
            runspace.ThreadOptions = PSThreadOptions.UseCurrentThread;

            runspace.Open();

            Runspace.DefaultRunspace = runspace;

            return runspace;
        }

        private void OnPowerShellIdle(CancellationToken idleCancellationToken)
        {
            IReadOnlyList<PSEventSubscriber> eventSubscribers = _mainRunspaceEngineIntrinsics.Events.Subscribers;

            // Go through pending event subscribers and:
            // - if we have any subscribers, ensure we process any events
            // - if we have any idle events, generate an idle event and process that
            bool runPipelineForEventProcessing = false;
            foreach (PSEventSubscriber subscriber in eventSubscribers)
            {
                runPipelineForEventProcessing = true;

                if (string.Equals(subscriber.SourceIdentifier, PSEngineEvent.OnIdle, StringComparison.OrdinalIgnoreCase))
                {
                    // We control the pipeline thread, so it's not possible for PowerShell to generate events while we're here.
                    // But we know we're sitting waiting for the prompt, so we generate the idle event ourselves
                    // and that will flush idle event subscribers in PowerShell so we can service them
                    _mainRunspaceEngineIntrinsics.Events.GenerateEvent(PSEngineEvent.OnIdle, sender: null, args: null, extraData: null);
                    break;
                }
            }

            if (!runPipelineForEventProcessing && _taskQueue.IsEmpty)
            {
                return;
            }

            using (CancellationScope cancellationScope = _cancellationContext.EnterScope(isIdleScope: true, idleCancellationToken))
            {
                while (!cancellationScope.CancellationToken.IsCancellationRequested
                    && _taskQueue.TryTake(out ISynchronousTask task))
                {
                    if (task.ExecutionOptions.MustRunInForeground)
                    {
                        // If we have a task that is queued, but cannot be run under readline
                        // we place it back at the front of the queue, and cancel the readline task
                        _taskQueue.Prepend(task);
                        _skipNextPrompt = true;
                        _cancellationContext.CancelIdleParentTask();
                        return;
                    }

                    // If we're executing a task, we don't need to run an extra pipeline later for events
                    // TODO: This may not be a PowerShell task, so ideally we can differentiate that here.
                    //       For now it's mostly true and an easy assumption to make.
                    runPipelineForEventProcessing = false;
                    task.ExecuteSynchronously(cancellationScope.CancellationToken);
                }
            }

            // We didn't end up executing anything in the background,
            // so we need to run a small artificial pipeline instead
            // to force event processing
            if (runPipelineForEventProcessing)
            {
                InvokePSCommand(new PSCommand().AddScript("0", useLocalScope: true), executionOptions: null, CancellationToken.None);
            }
        }

        private void OnCancelKeyPress(object sender, ConsoleCancelEventArgs args)
        {
            // We need to cancel the current task.
            _cancellationContext.CancelCurrentTask();

            // If the current task was running under the debugger, we need to synchronize the
            // cancelation with our debug context (and likely the debug server). Note that if we're
            // currently stopped in a breakpoint, that means the task is _not_ under the debugger.
            if (!CurrentRunspace.Runspace.Debugger.InBreakpoint)
            {
                StopDebugContext();
            }
        }

        private ConsoleKeyInfo ReadKey(bool intercept)
        {
            // PSRL doesn't tell us when CtrlC was sent.
            // So instead we keep track of the last key here.
            // This isn't functionally required,
            // but helps us determine when the prompt needs a newline added

            _lastKey = ConsoleProxy.SafeReadKey(intercept, CancellationToken.None);
            return _lastKey.Value;
        }

        private bool LastKeyWasCtrlC()
        {
            return _lastKey.HasValue
                && _lastKey.Value.IsCtrlC();
        }

        private void StopDebugContext()
        {
            // We are officially stopping the debugger.
            DebugContext.IsActive = false;

            // If the debug server is active, we need to synchronize state and stop it.
            if (DebugContext.IsDebugServerActive)
            {
                _languageServer?.SendNotification("powerShell/stopDebugger");
            }
        }

        private void OnDebuggerStopped(object sender, DebuggerStopEventArgs debuggerStopEventArgs)
        {
            // The debugger has officially started. We use this to later check if we should stop it.
            DebugContext.IsActive = true;

            // If the debug server is NOT active, we need to synchronize state and start it.
            if (!DebugContext.IsDebugServerActive)
            {
                _languageServer?.SendNotification("powerShell/startDebugger");
            }

            DebugContext.SetDebuggerStopped(debuggerStopEventArgs);

            try
            {
                CurrentPowerShell.WaitForRemoteOutputIfNeeded();
                PushPowerShellAndRunLoop(CreateNestedPowerShell(CurrentRunspace), PowerShellFrameType.Debug | PowerShellFrameType.Nested);
                CurrentPowerShell.ResumeRemoteOutputIfNeeded();
            }
            finally
            {
                DebugContext.SetDebuggerResumed();
            }
        }

        private void OnBreakpointUpdated(object sender, BreakpointUpdatedEventArgs breakpointUpdatedEventArgs)
        {
            DebugContext.HandleBreakpointUpdated(breakpointUpdatedEventArgs);
        }

        private void OnRunspaceStateChanged(object sender, RunspaceStateEventArgs runspaceStateEventArgs)
        {
            if (!ShouldExitExecutionLoop && !_resettingRunspace && !runspaceStateEventArgs.RunspaceStateInfo.IsUsable())
            {
                _resettingRunspace = true;
                Task _ = PopOrReinitializeRunspaceAsync().HandleErrorsAsync(_logger);
            }
        }

        private Task PopOrReinitializeRunspaceAsync()
        {
            _cancellationContext.CancelCurrentTaskStack();
            RunspaceStateInfo oldRunspaceState = CurrentPowerShell.Runspace.RunspaceStateInfo;

            // Rather than try to lock the PowerShell executor while we alter its state,
            // we simply run this on its thread, guaranteeing that no other action can occur
            return ExecuteDelegateAsync(
                nameof(PopOrReinitializeRunspaceAsync),
                new ExecutionOptions { InterruptCurrentForeground = true },
                (_) =>
                {
                    while (_psFrameStack.Count > 0
                        && !_psFrameStack.Peek().PowerShell.Runspace.RunspaceStateInfo.IsUsable())
                    {
                        PopPowerShell(RunspaceChangeAction.Shutdown);
                    }

                    _resettingRunspace = false;

                    if (_psFrameStack.Count == 0)
                    {
                        // If our main runspace was corrupted,
                        // we must re-initialize our state.
                        // TODO: Use runspace.ResetRunspaceState() here instead
                        (PowerShell pwsh, RunspaceInfo runspaceInfo, EngineIntrinsics engineIntrinsics) = CreateInitialPowerShellSession();
                        _mainRunspaceEngineIntrinsics = engineIntrinsics;
                        PushPowerShell(new PowerShellContextFrame(pwsh, runspaceInfo, PowerShellFrameType.Normal));

                        _logger.LogError($"Top level runspace entered state '{oldRunspaceState.State}' for reason '{oldRunspaceState.Reason}' and was reinitialized."
                            + " Please report this issue in the PowerShell/vscode-PowerShell GitHub repository with these logs.");
                        UI.WriteErrorLine("The main runspace encountered an error and has been reinitialized. See the PowerShell extension logs for more details.");
                    }
                    else
                    {
                        _logger.LogError($"Current runspace entered state '{oldRunspaceState.State}' for reason '{oldRunspaceState.Reason}' and was popped.");
                        UI.WriteErrorLine($"The current runspace entered state '{oldRunspaceState.State}' for reason '{oldRunspaceState.Reason}'."
                            + " If this occurred when using Ctrl+C in a Windows PowerShell remoting session, this is expected behavior."
                            + " The session is now returning to the previous runspace.");
                    }
                },
                CancellationToken.None);
        }

        internal bool TryLoadPSReadLine(PowerShell pwsh, EngineIntrinsics engineIntrinsics, out IReadLine psrlReadLine)
        {
            psrlReadLine = null;
            try
            {
                var psrlProxy = PSReadLineProxy.LoadAndCreate(_loggerFactory, s_bundledModulePath, pwsh);
                psrlReadLine = new PsrlReadLine(psrlProxy, this, engineIntrinsics, ReadKey, OnPowerShellIdle);
                return true;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Unable to load PSReadLine. Will fall back to legacy readline implementation.");
                return false;
            }
        }

        private record RunspaceFrame(
            Runspace Runspace,
            RunspaceInfo RunspaceInfo);
    }
}
