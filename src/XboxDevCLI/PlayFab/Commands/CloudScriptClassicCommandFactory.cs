// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.CommandLine;
using XboxDevCLI.PlayFab.Generated.Admin;
using XboxDevCLI.Services;
using static XboxDevCLI.PlayFab.Commands.PlayFabCommandHelper;

namespace XboxDevCLI.PlayFab.Commands;

/// <summary>
/// Creates the "playfab cloudscript classic" command group for managing legacy, PlayFab-hosted
/// JavaScript Cloud Script revisions and versions (the <c>/Admin/*CloudScript*</c> APIs).
/// </summary>
internal static class CloudScriptClassicCommandFactory
{
    internal const string GetCloudScriptRevisionPath = "/Admin/GetCloudScriptRevision";
    internal const string GetCloudScriptVersionsPath = "/Admin/GetCloudScriptVersions";
    internal const string SetPublishedRevisionPath = "/Admin/SetPublishedRevision";
    internal const string UpdateCloudScriptPath = "/Admin/UpdateCloudScript";

    public static Command Create()
    {
        var command = new Command(
            "classic",
            "Manage legacy PlayFab-hosted JavaScript Cloud Script revisions and versions");

        // revision: a revision is the unit of Cloud Script content, so get/create/publish hang
        // off it rather than sitting bare on `classic`.
        var revision = new Command("revision", "Manage Cloud Script revisions.");
        revision.Subcommands.Add(CreateGetRevisionCommand());
        revision.Subcommands.Add(CreateUpdateCommand());
        revision.Subcommands.Add(CreatePublishCommand());
        command.Subcommands.Add(revision);

        var version = new Command("version", "Inspect Cloud Script versions.");
        version.Subcommands.Add(CreateListVersionsCommand());
        command.Subcommands.Add(version);

        return command;
    }

    internal static (string apiPath, GetCloudScriptRevisionRequest request) BuildGetRevisionCall(
        int? version, int? revision)
    {
        var request = new GetCloudScriptRevisionRequest
        {
            Version = version,
            Revision = revision,
        };

        return (GetCloudScriptRevisionPath, request);
    }

    internal static (string apiPath, SetPublishedRevisionRequest request) BuildPublishCall(
        int version, int revision)
    {
        var request = new SetPublishedRevisionRequest
        {
            Version = version,
            Revision = revision,
        };

        return (SetPublishedRevisionPath, request);
    }

    internal static (string apiPath, UpdateCloudScriptRequest request) BuildUpdateCall(
        string fileName, string fileContents, bool publish)
    {
        var request = new UpdateCloudScriptRequest
        {
            Files = new List<CloudScriptFile>
            {
                new() { Filename = fileName, FileContents = fileContents },
            },
            Publish = publish,
        };

        return (UpdateCloudScriptPath, request);
    }

    private static Command CreateGetRevisionCommand()
    {
        var versionOption = new Option<int?>("--version")
        {
            Description = "Version number (defaults to latest)",
        };

        var revisionOption = new Option<int?>("--revision")
        {
            Description = "Revision number (defaults to latest)",
        };

        var command = new Command("get", "Get the contents of a specific Cloud Script revision")
        {
            versionOption,
            revisionOption,
        };

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            WithClientAsync(parseResult, cancellationToken, (client, output, stdout, stderr, cancellationToken) =>
                RunGetRevisionAsync(
                    client,
                    parseResult.GetValue(versionOption),
                    parseResult.GetValue(revisionOption),
                    output, stdout, stderr,
                    cancellationToken)));

        return command.WithMetadata(new CommandMetadata(
            ApiPaths: [GetCloudScriptRevisionPath]));
    }

    /// <summary>
    /// Executes the "cloudscript classic revision get" logic against an already-constructed
    /// client. Extracted from the command action so the request-building and output logic can
    /// be unit-tested with a fake transport and captured output streams.
    /// </summary>
    internal static Task<int> RunGetRevisionAsync(
        PlayFabApiClient client,
        int? version,
        int? revision,
        OutputOptions output,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var (apiPath, request) = BuildGetRevisionCall(version, revision);
        return RunAsync(client, apiPath, request, output, stdout, stderr, cancellationToken: cancellationToken);
    }

    private static Command CreateListVersionsCommand()
    {
        var command = new Command("list", "List all Cloud Script versions and their revision info");

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            WithClientAsync(parseResult, cancellationToken, (client, output, stdout, stderr, cancellationToken) =>
                RunListVersionsAsync(client, output, stdout, stderr, cancellationToken)));

        return command.WithMetadata(new CommandMetadata(
            ApiPaths: [GetCloudScriptVersionsPath],
            Table: new TableView(
                nameof(GetCloudScriptVersionsResult.Versions),
                [
                    new TableColumn("VERSION", nameof(CloudScriptVersionStatus.Version), 10),
                    new TableColumn("PUBLISHED REVISION", nameof(CloudScriptVersionStatus.PublishedRevision), 20),
                    new TableColumn("LATEST REVISION", nameof(CloudScriptVersionStatus.LatestRevision), 18),
                ])));
    }

    /// <summary>
    /// Executes the "cloudscript classic version list" logic against an already-constructed
    /// client. Extracted from the command action so the output logic can be unit-tested with
    /// a fake transport and captured output streams.
    /// </summary>
    internal static Task<int> RunListVersionsAsync(
        PlayFabApiClient client,
        OutputOptions output,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        return InvokeAsync(
            client, GetCloudScriptVersionsPath, "{}", output, stdout, stderr,
            cancellationToken: cancellationToken);
    }

    private static Command CreateUpdateCommand()
    {
        var fileOption = new Option<string>("--file")
        {
            Description = "Path to the JavaScript file to upload as a new Cloud Script revision",
            Required = true,
        };

        var publishOption = new Option<bool>("--publish")
        {
            Description = "Immediately publish the new revision after uploading",
        };

        var command = new Command("create", "Upload a new Cloud Script revision from a local file")
        {
            fileOption,
            publishOption,
        };

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            WithClientAsync(parseResult, cancellationToken, (client, output, stdout, stderr, cancellationToken) =>
                RunUpdateAsync(
                    client,
                    parseResult.GetValue(fileOption)!,
                    parseResult.GetValue(publishOption),
                    output, stdout, stderr,
                    cancellationToken)));

        return command.WithMetadata(new CommandMetadata(
            ApiPaths: [UpdateCloudScriptPath],
            SafetyDetails:
                "The new revision is created unpublished unless --publish is passed, which makes " +
                "it live for players immediately."));
    }

    /// <summary>
    /// Executes the "cloudscript classic revision create" logic against an already-constructed client.
    /// Extracted from the command action so the local-file validation, request-building, and
    /// output logic can be unit-tested with a fake transport and captured output streams.
    /// </summary>
    internal static async Task<int> RunUpdateAsync(
        PlayFabApiClient client,
        string filePath,
        bool publish,
        OutputOptions output,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            stderr.WriteLine($"Error: File not found: {filePath}");
            return 1;
        }

        var fileContents = await File.ReadAllTextAsync(filePath, cancellationToken);
        var fileName = Path.GetFileName(filePath);

        var (apiPath, request) = BuildUpdateCall(fileName, fileContents, publish);
        return await RunAsync(client, apiPath, request, output, stdout, stderr, cancellationToken: cancellationToken);
    }

    private static Command CreatePublishCommand()
    {
        var versionOption = new Option<int>("--version")
        {
            Description = "Version number to publish",
            Required = true,
        };

        var revisionOption = new Option<int>("--revision")
        {
            Description = "Revision number to make the published revision",
            Required = true,
        };

        var command = new Command("publish", "Set the published revision for a Cloud Script version")
        {
            versionOption,
            revisionOption,
        };

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            WithClientAsync(parseResult, cancellationToken, (client, output, stdout, stderr, cancellationToken) =>
                RunPublishAsync(
                    client,
                    parseResult.GetValue(versionOption),
                    parseResult.GetValue(revisionOption),
                    output, stdout, stderr,
                    cancellationToken)));

        return command.WithMetadata(new CommandMetadata(
            ApiPaths: [SetPublishedRevisionPath],
            SafetyDetails:
                "The named revision becomes live for players as soon as the call succeeds."));
    }

    /// <summary>
    /// Executes the "cloudscript classic revision publish" logic against an already-constructed client.
    /// Extracted from the command action so the request-building and output logic can be
    /// unit-tested with a fake transport and captured output streams.
    /// </summary>
    internal static Task<int> RunPublishAsync(
        PlayFabApiClient client,
        int version,
        int revision,
        OutputOptions output,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var (apiPath, request) = BuildPublishCall(version, revision);
        return RunAsync(client, apiPath, request, output, stdout, stderr, cancellationToken: cancellationToken);
    }
}
