﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Management.Automation;
using Microsoft.PowerShell.EditorServices.Utility;

namespace Microsoft.PowerShell.EditorServices.Services.DebugAdapter
{
    /// <summary>
    /// Provides details about a breakpoint that is set in the
    /// PowerShell debugger.
    /// </summary>
    internal class BreakpointDetails : BreakpointDetailsBase
    {
        /// <summary>
        /// Gets the unique ID of the breakpoint.
        /// </summary>
        /// <returns></returns>
        public int Id { get; private set; }

        /// <summary>
        /// Gets the source where the breakpoint is located.  Used only for debug purposes.
        /// </summary>
        public string Source { get; private set; }

        /// <summary>
        /// Gets the line number at which the breakpoint is set.
        /// </summary>
        public int LineNumber { get; private set; }

        /// <summary>
        /// Gets the column number at which the breakpoint is set.
        /// </summary>
        public int? ColumnNumber { get; private set; }

        public string LogMessage { get; private set; }

        private BreakpointDetails()
        {
        }

        /// <summary>
        /// Creates an instance of the BreakpointDetails class from the individual
        /// pieces of breakpoint information provided by the client.
        /// </summary>
        /// <param name="source"></param>
        /// <param name="line"></param>
        /// <param name="column"></param>
        /// <param name="condition"></param>
        /// <param name="hitCondition"></param>
        /// <returns></returns>
        internal static BreakpointDetails Create(
            string source,
            int line,
            int? column = null,
            string condition = null,
            string hitCondition = null,
            string logMessage = null)
        {
            Validate.IsNotNullOrEmptyString(nameof(source), source);

            return new BreakpointDetails
            {
                Verified = true,
                Source = source,
                LineNumber = line,
                ColumnNumber = column,
                Condition = condition,
                HitCondition = hitCondition,
                LogMessage = logMessage
            };
        }

        /// <summary>
        /// Creates an instance of the BreakpointDetails class from a
        /// PowerShell Breakpoint object.
        /// </summary>
        /// <param name="breakpoint">The Breakpoint instance from which details will be taken.</param>
        /// <param name="updateType">The BreakpointUpdateType to determine if the breakpoint is verified.</param>
        /// <returns>A new instance of the BreakpointDetails class.</returns>
        internal static BreakpointDetails Create(
            Breakpoint breakpoint,
            BreakpointUpdateType updateType = BreakpointUpdateType.Set)
        {
            Validate.IsNotNull(nameof(breakpoint), breakpoint);

            if (breakpoint is not LineBreakpoint lineBreakpoint)
            {
                throw new ArgumentException(
                    "Unexpected breakpoint type: " + breakpoint.GetType().Name);
            }

            var breakpointDetails = new BreakpointDetails
            {
                Id = breakpoint.Id,
                Verified = updateType != BreakpointUpdateType.Disabled,
                Source = lineBreakpoint.Script,
                LineNumber = lineBreakpoint.Line,
                ColumnNumber = lineBreakpoint.Column,
                Condition = lineBreakpoint.Action?.ToString()
            };

            if (lineBreakpoint.Column > 0)
            {
                breakpointDetails.ColumnNumber = lineBreakpoint.Column;
            }

            return breakpointDetails;
        }
    }
}
