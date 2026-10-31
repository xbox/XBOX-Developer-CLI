// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;

namespace XboxDevCLI.CommandLine;

/// <summary>
/// Renders Help for whichever command <c>--help</c> was requested on.
/// </summary>
/// <remarks>
/// Every depth goes through <see cref="CommandHelpWriter"/>. The stock <c>System.CommandLine</c>
/// renderer is no longer used as a fallback for deeper commands, so a leaf and the root now share
/// one layout.
/// </remarks>
internal sealed class CommandHelpAction(RootCommand rootCommand, string cliName)
    : SynchronousCommandLineAction
{
    /// <summary>
    /// Keeps Help short-circuiting validation, so <c>--help</c> works on a command whose required
    /// options are absent and never triggers configuration or network work.
    /// </summary>
    public override bool ClearsParseErrors => true;

    public override int Invoke(ParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(parseResult);

        if (CommandLineErrorWriter.TryWriteUnknownCommand(
                parseResult,
                rootCommand,
                cliName,
                parseResult.InvocationConfiguration.Error,
                requireParseError: false))
        {
            return 1;
        }

        CommandHelpWriter.Write(
            parseResult.InvocationConfiguration.Output,
            rootCommand,
            parseResult.CommandResult.Command,
            cliName,
            useAnsi: CommandHelpWriter.ShouldUseAnsi());
        return 0;
    }
}
