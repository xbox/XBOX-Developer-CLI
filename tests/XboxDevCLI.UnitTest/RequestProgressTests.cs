// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using XboxDevCLI.Commands;
using XboxDevCLI.Output;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

public sealed class RequestProgressTests
{
    private const string ConfigurationResponse =
        """{"sandbox":"STAGE.0","hasWorkingConfiguration":true,"workingVersion":9}""";

    private const string PublishStatusResponse = """{"Status":"Success"}""";

    private const string StatusOutput =
        """{"sandbox":"STAGE.0","hasWorkingConfiguration":true,"workingVersion":9,"lastPublish":{"status":"Success","jobId":null,"message":null}}""";

    private static readonly Guid Scid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    [Fact]
    public void DescribesTheCurrentActivityAndElapsedSeconds()
    {
        using var progress = new RequestProgress("Getting schema types", TextWriter.Null, enabled: false);
        Assert.Matches(@"^Getting schema types\.\.\. \d+s$", progress.Describe());

        progress.Activity = "Downloading achievements2017 schema version 2";
        Assert.Matches(@"^Downloading achievements2017 schema version 2\.\.\. \d+s$", progress.Describe());
    }

    [Fact]
    public void RemovesControlCharactersSoTheLineStaysOneErasableRow()
    {
        using var progress = new RequestProgress(
            "Getting achieve\nments\u001b[31m2017 schema types", TextWriter.Null, enabled: false);
        Assert.Matches(@"^Getting achievements\[31m2017 schema types\.\.\. \d+s$", progress.Describe());

        progress.Activity = "Getting a\r\u0007\u2028b schema versions";
        Assert.Matches(@"^Getting ab schema versions\.\.\. \d+s$", progress.Describe());
    }

    [Fact]
    public async Task ATestTerminalFixesTheWidthTheLineIsCutTo()
    {
        using var output = new StringWriter();
        using var stderrBuffer = new StringWriter();
        TextWriter stderr = TextWriter.Synchronized(stderrBuffer);
        using var client = CreateClient(new SlowHandler(
            HttpStatusCode.OK,
            ConfigurationResponse,
            () => stderrBuffer.ToString().Contains("Getting config", StringComparison.Ordinal)));

        ProgressLine.TerminalOverride.Value = new(IsTerminal: true, Columns: 20);
        int exitCode = await ConfigStatusCommandFactory.RunAsync(
            client, 12345, Scid, "STAGE.0", OutputOptions.JsonCompact, output, stderr, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Matches(@"^(\rGetting configur\.\.\.)+\r {19}\r$", stderrBuffer.ToString());
    }

    [Theory]
    [InlineData(false, false, "XDKS.1", "XDKS.1", "Publishing to XDKS.1")]
    [InlineData(false, false, "STAGE.0", "RETAIL", "Publishing from STAGE.0 to RETAIL")]
    [InlineData(true, false, "STAGE.0", "RETAIL", "Validating publish from STAGE.0 to RETAIL")]
    [InlineData(false, true, "XDKS.1", "XDKS.1", "Republishing to XDKS.1")]
    public void DescribesThePublish(bool validateOnly, bool republish, string source, string sandbox, string expected) =>
        Assert.Equal(
            expected,
            PublishCommandFactory.DescribePublish(
                new XboxLivePublishRequest(Scid, sandbox, source, validateOnly, null, republish)));

    [Theory]
    [InlineData(false, "XDKS.1", 2, "Saving 2 documents to XDKS.1")]
    [InlineData(true, "XDKS.1", 1, "Validating 1 document for XDKS.1")]
    [InlineData(false, null, 3, "Saving 3 account documents")]
    [InlineData(true, null, 1, "Validating 1 account document")]
    public void DescribesTheCommit(bool validateOnly, string? sandbox, int count, string expected)
    {
        var documents = Enumerable.Range(0, count)
            .Select(i => new XboxDocumentUpload($"doc{i}.xml", []))
            .ToArray();
        Assert.Equal(
            expected,
            CommitDocumentsCommandFactory.DescribeCommit(new XboxLiveCommitDocumentsRequest(
                sandbox is null ? "account" : "sandbox",
                sandbox is null ? null : Scid,
                sandbox,
                validateOnly,
                Force: false,
                ETag: null,
                Message: null,
                documents)));
    }

    [Theory]
    [InlineData("XDKS.1", "Downloading draft documents from XDKS.1")]
    [InlineData(null, "Downloading draft account documents")]
    public void DescribesTheDownload(string? sandbox, string expected) =>
        Assert.Equal(
            expected,
            GetDocumentsCommandFactory.DescribeDownload(new XboxLiveGetDocumentsRequest(
                sandbox is null ? "account" : "sandbox",
                sandbox is null ? null : Scid,
                sandbox,
                "working",
                null,
                [])));

    [Fact]
    public async Task ASlowRequestShowsProgressOnStderrAndErasesItBeforeTheResult()
    {
        using var output = new StringWriter();
        using var stderrBuffer = new StringWriter();
        TextWriter stderr = TextWriter.Synchronized(stderrBuffer);
        using var client = CreateClient(new SlowHandler(
            HttpStatusCode.OK,
            ConfigurationResponse,
            () => stderrBuffer.ToString().Contains("Getting configuration status", StringComparison.Ordinal)));

        ProgressLine.TerminalOverride.Value = new(IsTerminal: true, Columns: 120);
        int exitCode = await ConfigStatusCommandFactory.RunAsync(
            client, 12345, Scid, "STAGE.0", OutputOptions.JsonCompact, output, stderr, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(StatusOutput + Environment.NewLine, output.ToString());
        Assert.Matches(@"^(\rGetting configuration status for STAGE\.0\.\.\. \d+s *)+\r +\r$", stderrBuffer.ToString());
    }

    [Fact]
    public async Task AFailedRequestErasesProgressBeforeTheError()
    {
        using var output = new StringWriter();
        using var stderrBuffer = new StringWriter();
        TextWriter stderr = TextWriter.Synchronized(stderrBuffer);
        using var client = CreateClient(new SlowHandler(
            HttpStatusCode.NotFound,
            """{"message":"Not found"}""",
            () => stderrBuffer.ToString().Contains("Getting configuration status", StringComparison.Ordinal)));

        ProgressLine.TerminalOverride.Value = new(IsTerminal: true, Columns: 120);
        int exitCode = await ConfigStatusCommandFactory.RunAsync(
            client, 12345, Scid, "RETAIL", OutputOptions.Json, output, stderr, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Empty(output.ToString());
        string drawn = stderrBuffer.ToString();
        Match progress = Regex.Match(drawn, @"^(\rGetting configuration status for RETAIL\.\.\. \d+s *)+\r +\r(?!\n)");
        Assert.True(progress.Success, drawn);
        string error = drawn[progress.Length..];
        Assert.NotEmpty(error);
        Assert.DoesNotContain("Getting", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("list", "Reading configurations", """{"configurations":[]}""")]
    [InlineData("status", "Getting configuration status for STAGE.0", ConfigurationResponse)]
    public async Task ResolvedTargetIsWrittenAfterProgressIsErased(string action, string activity, string body)
    {
        using var output = new StringWriter();
        using var stderrBuffer = new StringWriter();
        TextWriter stderr = TextWriter.Synchronized(stderrBuffer);
        using var client = CreateClient(new SlowHandler(
            HttpStatusCode.OK,
            body,
            () => stderrBuffer.ToString().Contains(activity, StringComparison.Ordinal),
            configureResponse: response =>
            {
                response.Headers.Add("X-XboxPartner-Scid", Scid.ToString("D"));
                response.Headers.Add("X-XboxPartner-Seller-Id", "12345");
                response.Headers.Add("X-XboxPartner-Resolved-Title-Id", "00000042");
            }));

        ProgressLine.TerminalOverride.Value = new(IsTerminal: true, Columns: 120);
        int exitCode = await (action switch
        {
            "list" => ListConfigurationsCommandFactory.RunAsync(
                client, 12345, null, OutputOptions.JsonCompact, output, stderr, CancellationToken.None,
                titleId: "00000042", verbose: true),
            _ => ConfigStatusCommandFactory.RunAsync(
                client, 12345, null, "STAGE.0", OutputOptions.JsonCompact, output, stderr, CancellationToken.None,
                titleId: "00000042", verbose: true),
        });

        Assert.Equal(0, exitCode);
        string drawn = stderrBuffer.ToString();
        Match progress = Regex.Match(drawn, $@"^(\r{Regex.Escape(activity)}\.\.\. \d+s *)+\r +\r(?!\n)");
        Assert.True(progress.Success, drawn);
        Assert.Equal(
            $"Effective configuration target: seller 12345, title 00000042, SCID {Scid:D}{Environment.NewLine}",
            drawn[progress.Length..]);
    }

    [Fact]
    public async Task NothingIsDrawnWhenStderrIsRedirected()
    {
        using var output = new StringWriter();
        using var stderr = new StringWriter();
        using var client = CreateClient(new SlowHandler(
            HttpStatusCode.OK,
            ConfigurationResponse,
            ready: null,
            delay: TimeSpan.FromMilliseconds(500)));

        ProgressLine.TerminalOverride.Value = new(IsTerminal: false, Columns: 120);
        int exitCode = await ConfigStatusCommandFactory.RunAsync(
            client, 12345, Scid, "STAGE.0", OutputOptions.JsonCompact, output, stderr, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(StatusOutput + Environment.NewLine, output.ToString());
        Assert.Empty(stderr.ToString());
    }

    private static XboxPartnerClient CreateClient(HttpMessageHandler handler) =>
        new(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["XboxPartner:BaseUrl"] = "http://localhost:8080",
                    ["XboxPartner:TimeoutSeconds"] = "60",
                })
                .Build(),
            new HttpClient(handler),
            () => Task.FromResult<string?>("caller-token"));

    // Holds the response until the progress line has drawn, or for a fixed delay.
    private sealed class SlowHandler(
        HttpStatusCode statusCode,
        string body,
        Func<bool>? ready,
        TimeSpan? delay = null,
        Action<HttpResponseMessage>? configureResponse = null) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (ready is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (!ready())
                {
                    await Task.Delay(20, timeout.Token);
                }
            }
            else if (delay is { } wait)
            {
                await Task.Delay(wait, cancellationToken);
            }

            string content = statusCode == HttpStatusCode.OK &&
                request.RequestUri!.AbsolutePath.EndsWith("/publish/status", StringComparison.Ordinal)
                ? PublishStatusResponse
                : body;
            var response = new HttpResponseMessage(statusCode) { Content = new StringContent(content) };
            configureResponse?.Invoke(response);
            return response;
        }
    }
}
