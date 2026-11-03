// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;

namespace XboxDevCLI.Commands;

/// <summary>Builds the XBOX services configuration command namespace and resource families.</summary>
internal static class XboxLiveCommandFactory
{
    public static Command Create()
    {
        var xboxLive = new Command("xbl", "Manage XBOX services configuration");

        var configuration = new Command(
            "configuration",
            "Inspect working and published XBOX services configuration state");
        configuration.Subcommands.Add(ListConfigurationsCommandFactory.Create());
        configuration.Subcommands.Add(GetConfigurationCommandFactory.Create());
        xboxLive.Subcommands.Add(configuration);
        xboxLive.Subcommands.Add(AchievementImageCommandFactory.Create());

        var schema = new Command("schema", "Inspect XBOX services configuration schemas");
        schema.Subcommands.Add(GetSchemasCommandFactory.Create());
        xboxLive.Subcommands.Add(schema);

        var document = new Command("document", "Manage XBOX services configuration documents");
        document.Subcommands.Add(GetDocumentsCommandFactory.Create());
        document.Subcommands.Add(CommitDocumentsCommandFactory.Create());
        xboxLive.Subcommands.Add(document);

        var publish = new Command("publish", "Manage XBOX services configuration publishing");
        publish.Subcommands.Add(PublishCommandFactory.Create());
        publish.Subcommands.Add(GetPublishStatusCommandFactory.Create());
        xboxLive.Subcommands.Add(publish);

        return xboxLive;
    }
}
