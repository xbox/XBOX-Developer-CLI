// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;

namespace XboxDevCLI.Commands;

internal static class AuthCommandFactory
{
    internal static Command Create()
    {
        var command = new Command("auth", "Authenticate with Microsoft Entra ID");
        command.Subcommands.Add(LoginCommandFactory.Create());
        command.Subcommands.Add(LogoutCommandFactory.Create());
        command.Subcommands.Add(StatusCommandFactory.Create());
        return command;
    }
}
