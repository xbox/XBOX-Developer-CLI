// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using XboxDevCLI.CommandLine;

namespace XboxDevCLI.PlayFab.Commands;

internal static class PlayFabCommandFactory
{
    public static Command Create()
    {
        var playFabCommand = new Command("playfab", "Inspect and manage PlayFab services");

        // Families that map commands one-to-one onto PlayFab API operations are Experimental
        // until they're redesigned around developer tasks. Help labels their descendants too.
        playFabCommand.Subcommands.Add(ApiCommandFactory.Create());
        playFabCommand.Subcommands.Add(Experimental(MpsCommandFactory.CreateMpsCommand()));
        playFabCommand.Subcommands.Add(FileCommandFactory.Create());
        playFabCommand.Subcommands.Add(TitleDataCommandFactory.Create());
        playFabCommand.Subcommands.Add(ApiPolicyCommandFactory.Create());
        playFabCommand.Subcommands.Add(Experimental(CloudScriptCommandFactory.Create()));
        playFabCommand.Subcommands.Add(Experimental(GroupCommandFactory.Create()));
        playFabCommand.Subcommands.Add(PlayerDataCommandFactory.Create());
        playFabCommand.Subcommands.Add(EconomyCommandFactory.Create());
        playFabCommand.Subcommands.Add(Experimental(ExperimentCommandFactory.Create()));
        playFabCommand.Subcommands.Add(Experimental(PlayerCommandFactory.Create()));
        playFabCommand.Subcommands.Add(Experimental(PrivacyCommandFactory.Create()));

        return playFabCommand;
    }

    private static Command Experimental(Command command) =>
        command.WithMetadata(CommandMetadataRegistry.GetMetadata(command) with { Stage = CommandStage.Experimental });
}
