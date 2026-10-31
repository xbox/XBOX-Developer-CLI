// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.CommandLine;
using XboxDevCLI.PlayFab.Generated.Experimentation;
using XboxDevCLI.Services;
using static XboxDevCLI.PlayFab.Commands.PlayFabCommandHelper;

namespace XboxDevCLI.PlayFab.Commands;

/// <summary>
/// Creates the <c>playfab experiment</c> command group: read-only inspection of a title's A/B
/// experimentation state via the PlayFab Experimentation API. Every subcommand maps to a
/// <c>Get</c>/<c>List</c> endpoint, so the whole family is safe by construction:
/// <list type="bullet">
///   <item><c>list</c> — all experiments for the title (<c>/Experimentation/GetExperiments</c>).</item>
///   <item><c>scorecard get</c> — latest scorecard for one experiment (<c>/Experimentation/GetLatestScorecard</c>).</item>
///   <item><c>treatment get</c> — treatment assignment for an entity (<c>/Experimentation/GetTreatmentAssignment</c>).</item>
///   <item><c>exclusion-group list</c> — exclusion groups for the title (<c>/Experimentation/GetExclusionGroups</c>).</item>
/// </list>
/// Experiment mutations (create/update/delete/start/stop) are intentionally out of scope; only the
/// read path ships here.
/// </summary>
internal static class ExperimentCommandFactory
{
    internal const string GetExperimentsPath = "/Experimentation/GetExperiments";
    internal const string GetLatestScorecardPath = "/Experimentation/GetLatestScorecard";
    internal const string GetTreatmentAssignmentPath = "/Experimentation/GetTreatmentAssignment";
    internal const string GetExclusionGroupsPath = "/Experimentation/GetExclusionGroups";

    public static Command Create()
    {
        var experiment = new Command("experiment", "Inspect a title's A/B experiments, scorecards, treatment assignments, and exclusion groups (read-only).");
        experiment.Subcommands.Add(CreateListCommand());
        experiment.Subcommands.Add(CreateScorecardCommand());
        experiment.Subcommands.Add(CreateTreatmentCommand());
        experiment.Subcommands.Add(CreateExclusionGroupCommand());
        return experiment;
    }

    #region list

    private static Command CreateListCommand()
    {
        var command = new Command("list", "List all experiments for the title (scheduled, started, stopped, or completed).");

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            WithClientAsync(parseResult, cancellationToken, (client, output, stdout, stderr, cancellationToken) =>
                RunListAsync(client, output, stdout, stderr, cancellationToken)));

        return command.WithMetadata(new CommandMetadata(
            ApiPaths: [GetExperimentsPath],
            Scope: "Reads all experiments configured for the resolved title.",
            Output: "The title's scheduled, started, stopped, and completed experiments.",
            Examples: [
                "xbox playfab experiment list",
                "xbox playfab experiment list --output table",
            ],
            Table: new TableView(
                nameof(GetExperimentsResult.Experiments),
                [
                    new TableColumn("ID", nameof(Experiment.Id), 38),
                    new TableColumn("NAME", nameof(Experiment.Name), 36),
                    new TableColumn("STATE", nameof(Experiment.State), 12),
                    new TableColumn("TYPE", nameof(Experiment.ExperimentType), 12),
                ])));
    }

    /// <summary>
    /// Action for <c>experiment list</c>. Sends an empty <see cref="GetExperimentsRequest"/> and
    /// returns the full list of experiments for the resolved title.
    /// </summary>
    internal static async Task<int> RunListAsync(
        PlayFabApiClient client, OutputOptions output, TextWriter stdout, TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var request = new GetExperimentsRequest();
        return await RunAsync(client, GetExperimentsPath, request, output, stdout, stderr, cancellationToken: cancellationToken);
    }

    #endregion

    #region scorecard

    private static Command CreateScorecardCommand()
    {
        var scorecard = new Command("scorecard", "Inspect an experiment's latest available scorecard.");
        var experimentIdOption = RequiredOption("--experiment-id", "The ID of the experiment whose latest scorecard to fetch.");

        var command = new Command("get", "Get the latest available scorecard for an experiment.")
        {
            experimentIdOption,
        };

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            WithClientAsync(parseResult, cancellationToken, (client, output, stdout, stderr, cancellationToken) =>
                RunScorecardAsync(
                    client,
                    parseResult.GetValue(experimentIdOption)!,
                    output, stdout, stderr,
                    cancellationToken)));

        var leaf = command.WithMetadata(new CommandMetadata(
            ApiPaths: [GetLatestScorecardPath],
            Scope: "Reads the latest scorecard for one experiment.",
            Output: "The experiment scorecard, including metric results when available.",
            Examples: [
                "xbox playfab experiment scorecard get --experiment-id 11111111-2222-3333-4444-555555555555",
            ]));

        scorecard.Subcommands.Add(leaf);
        return scorecard;
    }

    /// <summary>
    /// Action for <c>experiment scorecard get</c>. Sends a <see cref="GetLatestScorecardRequest"/> for
    /// <paramref name="experimentId"/> and returns the latest scorecard.
    /// </summary>
    internal static async Task<int> RunScorecardAsync(
        PlayFabApiClient client, string experimentId,
        OutputOptions output, TextWriter stdout, TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var request = new GetLatestScorecardRequest { ExperimentId = experimentId };
        return await RunAsync(client, GetLatestScorecardPath, request, output, stdout, stderr, cancellationToken: cancellationToken);
    }

    #endregion

    #region treatment

    private static Command CreateTreatmentCommand()
    {
        var treatment = new Command("treatment", "Inspect treatment variants and variables assigned to an entity.");
        var entityIdOption = RequiredOption("--entity-id", "Unique ID of the entity to look up treatment assignments for.");
        var entityTypeOption = RequiredOption("--entity-type", "Entity type (e.g. 'title_player_account', 'title'). See the PlayFab built-in entity types.");

        var command = new Command("get", "Get the treatment variants and variables assigned to an entity across all running experiments.")
        {
            entityIdOption,
            entityTypeOption,
        };

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            WithClientAsync(parseResult, cancellationToken, (client, output, stdout, stderr, cancellationToken) =>
                RunTreatmentAsync(
                    client,
                    parseResult.GetValue(entityIdOption)!,
                    parseResult.GetValue(entityTypeOption)!,
                    output, stdout, stderr,
                    cancellationToken)));

        var leaf = command.WithMetadata(new CommandMetadata(
            ApiPaths: [GetTreatmentAssignmentPath],
            Scope: "Reads treatment assignments for one entity across running experiments.",
            Output: "The entity's assigned variants and variables.",
            Examples: [
                "xbox playfab experiment treatment get --entity-id TITLEPLAYER123 --entity-type title_player_account",
            ]));

        treatment.Subcommands.Add(leaf);
        return treatment;
    }

    /// <summary>
    /// Action for <c>experiment treatment get</c>. Sends a <see cref="GetTreatmentAssignmentRequest"/>
    /// carrying the target <see cref="EntityKey"/> (<paramref name="entityId"/>/
    /// <paramref name="entityType"/>) and returns that entity's treatment assignment.
    /// </summary>
    internal static async Task<int> RunTreatmentAsync(
        PlayFabApiClient client, string entityId, string entityType,
        OutputOptions output, TextWriter stdout, TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var request = new GetTreatmentAssignmentRequest
        {
            Entity = new EntityKey { Id = entityId, Type = entityType },
        };
        return await RunAsync(client, GetTreatmentAssignmentPath, request, output, stdout, stderr, cancellationToken: cancellationToken);
    }

    #endregion

    #region exclusion-groups

    private static Command CreateExclusionGroupCommand()
    {
        var exclusionGroup = new Command("exclusion-group", "Inspect the exclusion groups defined for the title.");
        var command = new Command("list", "List the exclusion groups defined for the title.");

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            WithClientAsync(parseResult, cancellationToken, (client, output, stdout, stderr, cancellationToken) =>
                RunExclusionGroupsAsync(client, output, stdout, stderr, cancellationToken)));

        var leaf = command.WithMetadata(new CommandMetadata(
            ApiPaths: [GetExclusionGroupsPath],
            Scope: "Reads all experiment exclusion groups configured for the resolved title.",
            Output: "The title's experiment exclusion groups.",
            Examples: [
                "xbox playfab experiment exclusion-group list",
                "xbox playfab experiment exclusion-group list --output table",
            ],
            Table: new TableView(
                nameof(GetExclusionGroupsResult.ExclusionGroups),
                [
                    new TableColumn("GROUP ID", nameof(ExperimentExclusionGroup.ExclusionGroupId), 38),
                    new TableColumn("NAME", nameof(ExperimentExclusionGroup.Name), 36),
                    new TableColumn("DESCRIPTION", nameof(ExperimentExclusionGroup.Description), 50),
                ])));

        exclusionGroup.Subcommands.Add(leaf);
        return exclusionGroup;
    }

    /// <summary>
    /// Action for <c>experiment exclusion-group list</c>. Sends an empty
    /// <see cref="GetExclusionGroupsRequest"/> and returns the title's exclusion groups.
    /// </summary>
    internal static async Task<int> RunExclusionGroupsAsync(
        PlayFabApiClient client, OutputOptions output, TextWriter stdout, TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var request = new GetExclusionGroupsRequest();
        return await RunAsync(client, GetExclusionGroupsPath, request, output, stdout, stderr, cancellationToken: cancellationToken);
    }

    #endregion
}
