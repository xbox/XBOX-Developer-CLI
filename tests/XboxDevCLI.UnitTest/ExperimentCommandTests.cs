// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json;
using XboxDevCLI.PlayFab.Commands;
using XboxDevCLI.Output;
using Xunit;

namespace XboxDevCLI.UnitTest;

/// <summary>
/// Coverage for the read-only <c>playfab experiment</c> command group: command/option structure,
/// the request bodies actually serialized (asserting request URI path and JSON body), success
/// output (pretty / compact / data-only), the API-error path (non-2xx → stderr + exit 1), and each
/// subcommand reaching its correct Experimentation endpoint. Uses injected writers and a fake
/// transport, so no <c>Console</c> redirection (and thus no <c>ConsoleRedirect</c> collection) is
/// needed.
/// </summary>
public class ExperimentCommandTests
{
    #region Command structure

    [Fact]
    public void Experiment_HasAllSubcommands()
    {
        var experiment = ExperimentCommandFactory.Create();

        Assert.Equal("experiment", experiment.Name);
        foreach (var name in new[] { "list", "scorecard", "treatment", "exclusion-group" })
        {
            Assert.Contains(experiment.Subcommands, c => c.Name == name);
        }
    }

    [Fact]
    public void Experiment_Options()
    {
        var experiment = ExperimentCommandFactory.Create();
        var list = experiment.Subcommands.First(c => c.Name == "list");
        var scorecard = experiment.Subcommands.First(c => c.Name == "scorecard").Subcommands.Single();
        var treatment = experiment.Subcommands.First(c => c.Name == "treatment").Subcommands.Single();
        var exclusion = experiment.Subcommands.First(c => c.Name == "exclusion-group").Subcommands.Single();

        // Read-all commands take no options.
        Assert.Empty(list.Options);
        Assert.Empty(exclusion.Options);

        var experimentId = scorecard.Options.First(o => o.Name == "--experiment-id");
        Assert.True(experimentId.Required);

        foreach (var name in new[] { "--entity-id", "--entity-type" })
        {
            Assert.Contains(treatment.Options, o => o.Name == name);
            Assert.True(treatment.Options.First(o => o.Name == name).Required);
        }
    }

    #endregion

    #region list

    [Fact]
    public async Task RunListAsync_PostsEmptyBodyToGetExperiments()
    {
        var handler = new MockHttpMessageHandler(Ok("""{"code":200,"status":"OK","data":{"Experiments":[]}}"""));
        using var client = PlayFabTestClient.Create(handler);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await ExperimentCommandFactory.RunListAsync(client, OutputOptions.Json, stdout, stderr, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(HttpMethod.Post, handler.LastRequest?.Method);
        Assert.Contains("/Experimentation/GetExperiments", handler.LastRequestUri);
        // GetExperimentsRequest only has an optional CustomTags field; nulls are omitted → "{}".
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Empty(doc.RootElement.EnumerateObject());
        Assert.Contains("Experiments", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public async Task RunListAsync_JsonCompact_EmitsSingleLine()
    {
        var handler = new MockHttpMessageHandler(Ok("""{"code":200,"status":"OK","data":{"Experiments":[{"Name":"exp"}]}}"""));
        using var client = PlayFabTestClient.Create(handler);
        var stdout = new StringWriter();

        var exit = await ExperimentCommandFactory.RunListAsync(client, OutputOptions.JsonCompact, stdout, new StringWriter(), CancellationToken.None);

        Assert.Equal(0, exit);
        var output = stdout.ToString().TrimEnd();
        Assert.DoesNotContain("\n", output);
        Assert.Contains("Experiments", output);
    }

    [Fact]
    public async Task RunListAsync_DataOnly_StripsEnvelope()
    {
        var handler = new MockHttpMessageHandler(Ok("""{"code":200,"status":"OK","data":{"Experiments":[]}}"""));
        using var client = PlayFabTestClient.Create(handler);
        var stdout = new StringWriter();

        var exit = await ExperimentCommandFactory.RunListAsync(client, new OutputOptions("json", DataOnly: true), stdout, new StringWriter(), CancellationToken.None);

        Assert.Equal(0, exit);
        var output = stdout.ToString();
        Assert.Contains("Experiments", output); // the data payload is present
        Assert.DoesNotContain("status", output); // the {code,status,data} envelope is stripped
    }

    [Fact]
    public async Task RunListAsync_ApiError_ReturnsOneAndWritesError()
    {
        var errorJson = """{"code":401,"status":"Unauthorized","error":"NotAuthorized","errorCode":1074,"errorMessage":"nope"}""";
        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent(errorJson) });
        using var client = PlayFabTestClient.Create(handler);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await ExperimentCommandFactory.RunListAsync(client, OutputOptions.Json, stdout, stderr, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Contains("NotAuthorized", stderr.ToString());
    }

    #endregion

    #region scorecard

    [Fact]
    public async Task RunScorecardAsync_SendsExperimentIdToGetLatestScorecard()
    {
        var handler = new MockHttpMessageHandler(Ok("""{"code":200,"status":"OK","data":{"Scorecard":{"ExperimentId":"exp-1"}}}"""));
        using var client = PlayFabTestClient.Create(handler);
        var stdout = new StringWriter();

        var exit = await ExperimentCommandFactory.RunScorecardAsync(client, "exp-1", OutputOptions.Json, stdout, new StringWriter(), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("/Experimentation/GetLatestScorecard", handler.LastRequestUri);
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("exp-1", doc.RootElement.GetProperty("ExperimentId").GetString());
        Assert.Contains("Scorecard", stdout.ToString());
    }

    [Fact]
    public async Task RunScorecardAsync_ApiError_ReturnsOneAndWritesError()
    {
        var errorJson = """{"code":400,"status":"BadRequest","error":"InvalidParams","errorCode":1000,"errorMessage":"bad id"}""";
        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(errorJson) });
        using var client = PlayFabTestClient.Create(handler);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await ExperimentCommandFactory.RunScorecardAsync(client, "missing", OutputOptions.Json, stdout, stderr, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Contains("InvalidParams", stderr.ToString());
    }

    #endregion

    #region treatment

    [Fact]
    public async Task RunTreatmentAsync_SendsEntityKeyToGetTreatmentAssignment()
    {
        var handler = new MockHttpMessageHandler(Ok("""{"code":200,"status":"OK","data":{"TreatmentAssignment":{"Variables":[]}}}"""));
        using var client = PlayFabTestClient.Create(handler);
        var stdout = new StringWriter();

        var exit = await ExperimentCommandFactory.RunTreatmentAsync(
            client, "E1", "title_player_account", OutputOptions.Json, stdout, new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("/Experimentation/GetTreatmentAssignment", handler.LastRequestUri);
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var entity = doc.RootElement.GetProperty("Entity");
        Assert.Equal("E1", entity.GetProperty("Id").GetString());
        Assert.Equal("title_player_account", entity.GetProperty("Type").GetString());
        Assert.Contains("TreatmentAssignment", stdout.ToString());
    }

    [Fact]
    public async Task RunTreatmentAsync_ApiError_ReturnsOneAndWritesError()
    {
        var errorJson = """{"code":400,"status":"BadRequest","error":"InvalidParams","errorCode":1000,"errorMessage":"bad entity"}""";
        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(errorJson) });
        using var client = PlayFabTestClient.Create(handler);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await ExperimentCommandFactory.RunTreatmentAsync(
            client, "E1", "title", OutputOptions.Json, stdout, stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Contains("InvalidParams", stderr.ToString());
    }

    #endregion

    #region exclusion-groups

    [Fact]
    public async Task RunExclusionGroupsAsync_PostsEmptyBodyToGetExclusionGroups()
    {
        var handler = new MockHttpMessageHandler(Ok("""{"code":200,"status":"OK","data":{"ExclusionGroups":[]}}"""));
        using var client = PlayFabTestClient.Create(handler);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await ExperimentCommandFactory.RunExclusionGroupsAsync(client, OutputOptions.Json, stdout, stderr, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("/Experimentation/GetExclusionGroups", handler.LastRequestUri);
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Empty(doc.RootElement.EnumerateObject());
        Assert.Contains("ExclusionGroups", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public async Task RunExclusionGroupsAsync_ApiError_ReturnsOneAndWritesError()
    {
        var errorJson = """{"code":403,"status":"Forbidden","error":"NotAuthorized","errorCode":1074,"errorMessage":"denied"}""";
        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent(errorJson) });
        using var client = PlayFabTestClient.Create(handler);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await ExperimentCommandFactory.RunExclusionGroupsAsync(client, OutputOptions.Json, stdout, stderr, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Contains("NotAuthorized", stderr.ToString());
    }

    #endregion

    #region Test helpers

    private static HttpResponseMessage Ok(string json = """{"code":200,"status":"OK","data":{"ok":true}}""") =>
        new(HttpStatusCode.OK) { Content = new StringContent(json) };

    #endregion
}
