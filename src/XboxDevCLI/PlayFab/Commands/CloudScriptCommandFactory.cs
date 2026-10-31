// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;

namespace XboxDevCLI.PlayFab.Commands;

/// <summary>
/// Creates the "playfab cloudscript" command group. Cloud Script has two distinct models, each
/// exposed as its own sub-group:
/// <list type="bullet">
///   <item><description><c>classic</c> — legacy PlayFab-hosted JavaScript revisions (the
///     <c>/Admin/*CloudScript*</c> APIs).</description></item>
///   <item><description><c>function</c> — Azure Functions registrations
///     (the <c>/CloudScript/*Function*</c> APIs).</description></item>
/// </list>
/// </summary>
internal static class CloudScriptCommandFactory
{
    public static Command Create()
    {
        var command = new Command(
            "cloudscript",
            "Manage scripts and function registrations");

        command.Subcommands.Add(CloudScriptClassicCommandFactory.Create());
        command.Subcommands.Add(CloudScriptFunctionCommandFactory.Create());

        return command;
    }
}
