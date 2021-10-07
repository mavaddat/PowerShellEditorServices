// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.PowerShell.EditorServices.Logging;
using Microsoft.PowerShell.EditorServices.Services.DebugAdapter;
using Microsoft.PowerShell.EditorServices.Services.PowerShell;
using Microsoft.PowerShell.EditorServices.Services.PowerShell.Execution;
using Microsoft.PowerShell.EditorServices.Services.PowerShell.Host;

namespace Microsoft.PowerShell.EditorServices.Services
{
    internal class BreakpointService
    {
        private static readonly Version s_minimumBreakpointApiVersion = new Version(7, 0, 0, 0);

        private readonly ILogger<BreakpointService> _logger;
        private readonly PowerShellExecutionService _executionService;
        private readonly PsesInternalHost _editorServicesHost;
        private readonly DebugStateService _debugStateService;

        // TODO: This needs to be managed per nested session
        internal readonly Dictionary<string, HashSet<Breakpoint>> BreakpointsPerFile =
            new Dictionary<string, HashSet<Breakpoint>>();

        internal readonly HashSet<Breakpoint> CommandBreakpoints =
            new HashSet<Breakpoint>();

        public BreakpointService(
            ILoggerFactory factory,
            PowerShellExecutionService executionService,
            PsesInternalHost editorServicesHost,
            DebugStateService debugStateService)
        {
            _logger = factory.CreateLogger<BreakpointService>();
            _executionService = executionService;
            _editorServicesHost = editorServicesHost;
            _debugStateService = debugStateService;
        }

        public async Task<List<Breakpoint>> GetBreakpointsAsync()
        {
            if (_editorServicesHost.CurrentRunspace.PowerShellVersionDetails.Version >= s_minimumBreakpointApiVersion)
            {
                return BreakpointApiUtils.GetBreakpoints(
                    _editorServicesHost.Runspace.Debugger,
                    _debugStateService.RunspaceId);
            }

            // Legacy behavior
            PSCommand psCommand = new PSCommand()
                .AddCommand(@"Microsoft.PowerShell.Utility\Get-PSBreakpoint");
            IEnumerable<Breakpoint> breakpoints = await _executionService.ExecutePSCommandAsync<Breakpoint>(psCommand, CancellationToken.None).ConfigureAwait(false);
            return breakpoints.ToList();
        }

        public async Task<IEnumerable<BreakpointDetails>> SetBreakpointsAsync(string escapedScriptPath, IEnumerable<BreakpointDetails> breakpoints)
        {
            if (_editorServicesHost.CurrentRunspace.PowerShellVersionDetails.Version >= s_minimumBreakpointApiVersion)
            {
                foreach (BreakpointDetails breakpointDetails in breakpoints)
                {
                    try
                    {
                        BreakpointApiUtils.SetBreakpoint(_editorServicesHost.Runspace.Debugger, breakpointDetails, _debugStateService.RunspaceId);
                    }
                    catch(InvalidOperationException e)
                    {
                        breakpointDetails.Message = e.Message;
                        breakpointDetails.Verified = false;
                    }
                }

                return breakpoints;
            }

            // Legacy behavior
            PSCommand psCommand = null;
            List<BreakpointDetails> configuredBreakpoints = new List<BreakpointDetails>();
            foreach (BreakpointDetails breakpoint in breakpoints)
            {
                ScriptBlock actionScriptBlock = null;

                // Check if this is a "conditional" line breakpoint.
                if (!string.IsNullOrWhiteSpace(breakpoint.Condition) ||
                    !string.IsNullOrWhiteSpace(breakpoint.HitCondition) ||
                    !string.IsNullOrWhiteSpace(breakpoint.LogMessage))
                {
                    actionScriptBlock = BreakpointApiUtils.GetBreakpointActionScriptBlock(
                        breakpoint.Condition,
                        breakpoint.HitCondition,
                        breakpoint.LogMessage,
                        out string errorMessage);

                    if (!string.IsNullOrEmpty(errorMessage))
                    {
                        breakpoint.Verified = false;
                        breakpoint.Message = errorMessage;
                        configuredBreakpoints.Add(breakpoint);
                        continue;
                    }
                }

                // On first iteration psCommand will be null, every subsequent
                // iteration will need to start a new statement.
                if (psCommand == null)
                {
                    psCommand = new PSCommand();
                }
                else
                {
                    psCommand.AddStatement();
                }

                psCommand
                    .AddCommand(@"Microsoft.PowerShell.Utility\Set-PSBreakpoint")
                    .AddParameter("Script", escapedScriptPath)
                    .AddParameter("Line", breakpoint.LineNumber);

                // Check if the user has specified the column number for the breakpoint.
                if (breakpoint.ColumnNumber.HasValue && breakpoint.ColumnNumber.Value > 0)
                {
                    // It bums me out that PowerShell will silently ignore a breakpoint
                    // where either the line or the column is invalid.  I'd rather have an
                    // error or warning message I could relay back to the client.
                    psCommand.AddParameter("Column", breakpoint.ColumnNumber.Value);
                }

                if (actionScriptBlock != null)
                {
                    psCommand.AddParameter("Action", actionScriptBlock);
                }
            }

            // If no PSCommand was created then there are no breakpoints to set.
            if (psCommand != null)
            {
                IEnumerable<Breakpoint> setBreakpoints =
                    await _executionService.ExecutePSCommandAsync<Breakpoint>(psCommand, CancellationToken.None).ConfigureAwait(false);
                configuredBreakpoints.AddRange(
                    setBreakpoints.Select((breakpoint) => BreakpointDetails.Create(breakpoint))
                );
            }

            return configuredBreakpoints;
        }

        public async Task<IEnumerable<CommandBreakpointDetails>> SetCommandBreakpoints(IEnumerable<CommandBreakpointDetails> breakpoints)
        {
            if (_editorServicesHost.CurrentRunspace.PowerShellVersionDetails.Version >= s_minimumBreakpointApiVersion)
            {
                foreach (CommandBreakpointDetails commandBreakpointDetails in breakpoints)
                {
                    try
                    {
                        BreakpointApiUtils.SetBreakpoint(_editorServicesHost.Runspace.Debugger, commandBreakpointDetails, _debugStateService.RunspaceId);
                    }
                    catch(InvalidOperationException e)
                    {
                        commandBreakpointDetails.Message = e.Message;
                        commandBreakpointDetails.Verified = false;
                    }
                }

                return breakpoints;
            }

            // Legacy behavior
            PSCommand psCommand = null;
            List<CommandBreakpointDetails> configuredBreakpoints = new List<CommandBreakpointDetails>();
            foreach (CommandBreakpointDetails breakpoint in breakpoints)
            {
                // On first iteration psCommand will be null, every subsequent
                // iteration will need to start a new statement.
                if (psCommand == null)
                {
                    psCommand = new PSCommand();
                }
                else
                {
                    psCommand.AddStatement();
                }

                psCommand
                    .AddCommand(@"Microsoft.PowerShell.Utility\Set-PSBreakpoint")
                    .AddParameter("Command", breakpoint.Name);

                // Check if this is a "conditional" line breakpoint.
                if (!string.IsNullOrWhiteSpace(breakpoint.Condition) ||
                    !string.IsNullOrWhiteSpace(breakpoint.HitCondition))
                {
                    ScriptBlock actionScriptBlock =
                        BreakpointApiUtils.GetBreakpointActionScriptBlock(
                            breakpoint.Condition,
                            breakpoint.HitCondition,
                            logMessage: null,
                            out string errorMessage);

                    // If there was a problem with the condition string,
                    // move onto the next breakpoint.
                    if (!string.IsNullOrEmpty(errorMessage))
                    {
                        breakpoint.Verified = false;
                        breakpoint.Message = errorMessage;
                        configuredBreakpoints.Add(breakpoint);
                        continue;
                    }

                    psCommand.AddParameter("Action", actionScriptBlock);
                }
            }

            // If no PSCommand was created then there are no breakpoints to set.
            if (psCommand != null)
            {
                IEnumerable<Breakpoint> setBreakpoints =
                    await _executionService.ExecutePSCommandAsync<Breakpoint>(psCommand, CancellationToken.None).ConfigureAwait(false);
                configuredBreakpoints.AddRange(
                    setBreakpoints.Select(CommandBreakpointDetails.Create));
            }

            return configuredBreakpoints;
        }

        /// <summary>
        /// Clears all breakpoints in the current session.
        /// </summary>
        public async Task RemoveAllBreakpointsAsync(string scriptPath = null)
        {
            try
            {
                if (_editorServicesHost.CurrentRunspace.PowerShellVersionDetails.Version >= s_minimumBreakpointApiVersion)
                {
                    foreach (Breakpoint breakpoint in BreakpointApiUtils.GetBreakpoints(
                            _editorServicesHost.Runspace.Debugger,
                            _debugStateService.RunspaceId))
                    {
                        if (scriptPath == null || scriptPath == breakpoint.Script)
                        {
                            BreakpointApiUtils.RemoveBreakpoint(
                                _editorServicesHost.Runspace.Debugger,
                                breakpoint,
                                _debugStateService.RunspaceId);
                        }
                    }

                    return;
                }

                // Legacy behavior

                PSCommand psCommand = new PSCommand();
                psCommand.AddCommand(@"Microsoft.PowerShell.Utility\Get-PSBreakpoint");

                if (!string.IsNullOrEmpty(scriptPath))
                {
                    psCommand.AddParameter("Script", scriptPath);
                }

                psCommand.AddCommand(@"Microsoft.PowerShell.Utility\Remove-PSBreakpoint");

                await _executionService.ExecutePSCommandAsync<object>(psCommand, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogException("Caught exception while clearing breakpoints from session", e);
            }
        }

        public async Task RemoveBreakpointsAsync(IEnumerable<Breakpoint> breakpoints)
        {
            if (_editorServicesHost.CurrentRunspace.PowerShellVersionDetails.Version >= s_minimumBreakpointApiVersion)
            {
                foreach (Breakpoint breakpoint in breakpoints)
                {
                    BreakpointApiUtils.RemoveBreakpoint(
                        _editorServicesHost.Runspace.Debugger,
                        breakpoint,
                        _debugStateService.RunspaceId);

                    switch (breakpoint)
                    {
                        case CommandBreakpoint commandBreakpoint:
                            CommandBreakpoints.Remove(commandBreakpoint);
                            break;
                        case LineBreakpoint lineBreakpoint:
                            if (BreakpointsPerFile.TryGetValue(lineBreakpoint.Script, out HashSet<Breakpoint> bps))
                            {
                                bps.Remove(lineBreakpoint);
                            }
                            break;
                        default:
                            throw new ArgumentException("Unsupported breakpoint type.");
                    }
                }

                return;
            }

            // Legacy behavior
            var breakpointIds = breakpoints.Select(b => b.Id).ToArray();
            if(breakpointIds.Length > 0)
            {
                PSCommand psCommand = new PSCommand();
                psCommand.AddCommand(@"Microsoft.PowerShell.Utility\Remove-PSBreakpoint");
                psCommand.AddParameter("Id", breakpoints.Select(b => b.Id).ToArray());

                await _executionService.ExecutePSCommandAsync<object>(psCommand, CancellationToken.None).ConfigureAwait(false);
            }
        }


    }
}
