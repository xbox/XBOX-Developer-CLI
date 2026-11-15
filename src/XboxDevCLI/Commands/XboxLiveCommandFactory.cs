// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using XboxDevCLI.CommandLine;

namespace XboxDevCLI.Commands;

/// <summary>Builds the XBOX services configuration command namespace and resource families.</summary>
internal static class XboxLiveCommandFactory
{
    public static Command Create()
    {
        var xboxLive = new Command("xbl", "Manage XBOX services configuration");

        var config = new Command(
            "config",
            "Inspect, download, save, and publish XBOX services configuration");
        config.Subcommands.Add(ListConfigurationsCommandFactory.Create());
        config.Subcommands.Add(ConfigStatusCommandFactory.Create());
        config.Subcommands.Add(GetDocumentsCommandFactory.Create());
        config.Subcommands.Add(CommitDocumentsCommandFactory.Create());
        config.Subcommands.Add(PublishCommandFactory.Create());
        xboxLive.Subcommands.Add(config);

        var schema = new Command("schema", "List and download XBOX services configuration schemas");
        schema.Subcommands.Add(GetSchemasCommandFactory.CreateList());
        schema.Subcommands.Add(GetSchemasCommandFactory.CreateGet());
        schema.Subcommands.Add(GetSchemasCommandFactory.CreateDownload());
        xboxLive.Subcommands.Add(schema);

        xboxLive.Subcommands.Add(AchievementImageCommandFactory.Create());

        return xboxLive.Requires(CommandRequirement.Authentication).Calls(ServiceEndpoints.XboxPartner);
    }
}
