// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Net;
using System.Text;
using System.Text.Json;
using NuGet.Versioning;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Commands;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public class CliUpdateCheckerTests
{
    [Theory]
    [InlineData("0.9.0", "v0.10.0", true)]
    [InlineData("1.0.0", "v1.0.0", false)]
    [InlineData("2.0.0", "v1.0.0", false)]
    [InlineData("1.0.0-preview.2", "v1.0.0", true)]
    [InlineData("2.0.0-preview.1", "v1.9.0", false)]
    [InlineData("1.0.0+abc", "v1.0.0+xyz", false)]
    [InlineData("1.0.0", "1.0.1", true)]
    public async Task ComparesSemanticVersionsWithoutInstallationOrAssetRequirements(
        string current, string tag, bool expected)
    {
        using var handler = new ReleaseHandler(Release(tag));
        using var http = new HttpClient(handler);
        var result = await new CliUpdateChecker(http).CheckAsync(current, CancellationToken.None);

        Assert.Equal(1, result.SchemaVersion);
        Assert.Equal(current, result.CurrentVersion);
        Assert.Equal(tag.TrimStart('v'), result.LatestVersion);
        Assert.Equal(expected, result.UpdateAvailable);
        Assert.Equal($"{ProjectLinks.ReleaseTags}/{Uri.EscapeDataString(tag)}", result.ReleaseNotesUrl);
        Assert.Single(handler.Requests);
        Assert.Equal(ProjectLinks.LatestReleaseApi, handler.Requests[0].ToString());
        if (expected)
        {
            Assert.Contains(result.Instructions, step => step.Contains("ZIP", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Instructions, step => step.Contains("appsettings", StringComparison.Ordinal));
            Assert.Contains(result.Instructions, step => step.Contains("Windows", StringComparison.Ordinal));
            Assert.Contains(result.Instructions, step => step.Contains("package-managed", StringComparison.Ordinal));
            Assert.Contains(result.Instructions, step => step.Contains("xbox --version", StringComparison.Ordinal));
            Assert.Contains(result.Instructions, step => step.Contains(ProjectLinks.InstallationGuide, StringComparison.Ordinal));
            Assert.Contains(result.Instructions, step => step.Contains("PATH needs no change", StringComparison.Ordinal));
            Assert.Contains(result.Instructions, step => step.Contains("not the executable filename", StringComparison.Ordinal));
            Assert.Contains(result.Instructions, step => step.Contains("Get-Command xbox -All", StringComparison.Ordinal)
                && step.Contains("type -a xbox", StringComparison.Ordinal));
            Assert.Contains(result.Instructions, step => step.Contains("%LOCALAPPDATA%\\XboxDevCLI", StringComparison.Ordinal)
                && step.Contains("automatically reused", StringComparison.Ordinal)
                && step.Contains("No import or seller reset", StringComparison.Ordinal));
            Assert.Contains(result.Instructions, step => step.Contains("Keep any XBOX_ environment variables", StringComparison.Ordinal));
        }
        else
        {
            Assert.Empty(result.Instructions);
            Assert.Contains("equal to or newer", result.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RunningAssemblyHasParseableInformationalVersion()
    {
        Assert.True(NuGetVersion.TryParse(CliUpdateChecker.CurrentVersion, out _));
        Assert.Equal(TimeSpan.FromSeconds(30), CliUpdateChecker.CheckTimeout);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("1.0.0\n")]
    [InlineData("1.2.3.4.5")]
    public async Task InvalidCurrentVersionFailsBeforeNetwork(string version)
    {
        using var handler = new ReleaseHandler(Release());
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new CliUpdateChecker(http).CheckAsync(version, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{"draft":true,"prerelease":false,"tag_name":"v1.0.1"}""")]
    [InlineData("""{"draft":false,"prerelease":true,"tag_name":"v1.0.1"}""")]
    [InlineData("""{"draft":false,"tag_name":"v1.0.1"}""")]
    [InlineData("""{"draft":false,"prerelease":false,"tag_name":5}""")]
    [InlineData("""{"draft":false,"prerelease":false,"tag_name":null}""")]
    [InlineData("""{"draft":false,"prerelease":false,"tag_name":"v1.0.1-preview.1"}""")]
    [InlineData("""{"draft":false,"prerelease":false,"tag_name":"garbage"}""")]
    public async Task InvalidOrUnstableReleaseIsNotReportedAsUpToDate(string body)
    {
        using var handler = new ReleaseHandler(body);
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new CliUpdateChecker(http).CheckAsync("1.0.0", CancellationToken.None));
    }

    [Theory]
    [InlineData(404, "accessible anonymously")]
    [InlineData(403, "rate-limited")]
    [InlineData(429, "rate-limited")]
    [InlineData(500, "unavailable")]
    [InlineData(302, "unavailable")]
    public async Task HttpFailuresAreActionableAndNeverFollowedOrRetried(int status, string expected)
    {
        using var handler = new ReleaseHandler("{}", (HttpStatusCode)status);
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            new CliUpdateChecker(http).CheckAsync("1.0.0", CancellationToken.None));
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
        Assert.Contains(ProjectLinks.Releases, error.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedMetadataIsRejectedWithOrWithoutContentLength(bool declaredLength)
    {
        using var handler = new ReleaseHandler(_ =>
        {
            var bytes = Encoding.UTF8.GetBytes(Release().PadRight(CliUpdateChecker.MaximumMetadataBytes + 1));
            HttpContent content = declaredLength
                ? new ByteArrayContent(bytes)
                : new StreamContent(new UnseekableStream(bytes));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new CliUpdateChecker(http).CheckAsync("1.0.0", CancellationToken.None));
    }

    [Fact]
    public async Task MetadataAtLimitIsAccepted()
    {
        using var handler = new ReleaseHandler(Release().PadRight(CliUpdateChecker.MaximumMetadataBytes));
        using var http = new HttpClient(handler);
        Assert.True((await new CliUpdateChecker(http).CheckAsync("1.0.0", CancellationToken.None)).UpdateAvailable);
    }

    [Theory]
    [InlineData("upgrade")]
    [InlineData("upgrade --check")]
    [InlineData("upgrade --check --output json")]
    [InlineData("--output json-compact upgrade")]
    [InlineData("upgrade --check -o json-compact")]
    [InlineData("upgrade --check --data-only")]
    public async Task CommandReturnsJsonAndManualInstructionsWithoutAuthentication(string invocation)
    {
        using var handler = new ReleaseHandler(Release());
        var root = CreateRoot(handler);
        var result = await CaptureAsync(() => root.Parse(invocation, Program.NoResponseFileConfig).InvokeAsync());
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal(7, json.RootElement.EnumerateObject().Count());
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(json.RootElement.GetProperty("updateAvailable").GetBoolean());
        Assert.NotEmpty(json.RootElement.GetProperty("instructions").EnumerateArray());
        Assert.DoesNotContain("canUpgrade", result.Output, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
        if (invocation.Contains("json-compact", StringComparison.Ordinal))
            Assert.DoesNotContain('\n', result.Output.TrimEnd());
        else
            Assert.Contains('\n', result.Output.TrimEnd());
    }

    [Theory]
    [InlineData("upgrade --confirm")]
    [InlineData("upgrade --yes")]
    [InlineData("upgrade -y")]
    [InlineData("upgrade --output table")]
    public async Task UnsupportedApplyOrOutputModesFailBeforeNetwork(string invocation)
    {
        using var handler = new ReleaseHandler(Release());
        var result = await CaptureAsync(() =>
            CreateRoot(handler).Parse(invocation, Program.NoResponseFileConfig).InvokeAsync());
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.NotEmpty(result.Error);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("upgrade --status")]
    [InlineData("upgrade --install")]
    [InlineData("upgrade --output xml")]
    [InlineData("upgrade unexpected")]
    public void UnknownInputsAreParseErrors(string invocation)
    {
        using var handler = new ReleaseHandler(Release());
        Assert.NotEmpty(CreateRoot(handler).Parse(invocation, Program.NoResponseFileConfig).Errors);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("{", 200)]
    [InlineData("{}", 200)]
    [InlineData("{}", 404)]
    public async Task CommandErrorsLeaveStdoutEmpty(string body, int status)
    {
        using var handler = new ReleaseHandler(body, (HttpStatusCode)status);
        var result = await CaptureAsync(() =>
            CreateRoot(handler).Parse("upgrade", Program.NoResponseFileConfig).InvokeAsync());
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains("Update check failed:", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidUtf8TagNameFailsCleanlyInsteadOfThrowingUnhandled()
    {
        // {"draft":false,"prerelease":false,"tag_name":"v1.0.<invalid UTF-8 continuation bytes>"}
        // JsonDocument.Parse(byte[]) accepts this document; GetString() on tag_name is what
        // throws InvalidOperationException, so this exercises that path end-to-end rather than
        // CliUpdateChecker directly, to prove the command never lets it escape as an unhandled
        // exception.
        var prefix = "{\"draft\":false,\"prerelease\":false,\"tag_name\":\"v1.0."u8.ToArray();
        var suffix = "\"}"u8.ToArray();
        var bytes = new byte[prefix.Length + 2 + suffix.Length];
        prefix.CopyTo(bytes, 0);
        bytes[prefix.Length] = 0xC0;
        bytes[prefix.Length + 1] = 0xAF;
        suffix.CopyTo(bytes, prefix.Length + 2);

        using var handler = new ReleaseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        });
        var result = await CaptureAsync(() =>
            CreateRoot(handler).Parse("upgrade", Program.NoResponseFileConfig).InvokeAsync());
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains("Update check failed:", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LongVersionIsRejected()
    {
        using var handler = new ReleaseHandler(Release("v1.0.0+" + new string('a', 257)));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new CliUpdateChecker(http).CheckAsync("1.0.0", CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndTimeoutAreReportedWithoutSuccess(bool cancelled)
    {
        using var tokenSource = new CancellationTokenSource();
        using var handler = new ReleaseHandler(_ =>
        {
            if (cancelled) tokenSource.Cancel();
            throw new OperationCanceledException();
        });
        var root = CreateRoot(handler);
        var result = await CaptureAsync(() =>
            root.Parse("upgrade", Program.NoResponseFileConfig).InvokeAsync(cancellationToken: tokenSource.Token));
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains(cancelled ? "cancelled" : "timed out", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationReachesMetadataStream()
    {
        using var tokenSource = new CancellationTokenSource();
        using var handler = new ReleaseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new CancellingStream(tokenSource)),
        });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new CliUpdateChecker(http).CheckAsync("1.0.0", tokenSource.Token));
    }

    [Fact]
    public async Task HelpAndMetadataDescribeOnlyAnAdvisoryCommand()
    {
        var root = Program.BuildRootCommand();
        var upgrade = Assert.Single(root.Subcommands, command => command.Name == "upgrade");
        Assert.Equal(CommandSafety.ReadOnly, CommandMetadataRegistry.GetMetadata(upgrade).Safety);
        Assert.False(Program.SupportsTableOutput(upgrade));
        var direct = await CaptureAsync(() => root.Parse("upgrade --help", Program.NoResponseFileConfig).InvokeAsync());
        var path = await CaptureAsync(() => root.Parse("help upgrade", Program.NoResponseFileConfig).InvokeAsync());
        Assert.Equal(direct.Output, path.Output);
        Assert.Equal(0, direct.ExitCode);
        Assert.Equal(0, path.ExitCode);
        Assert.Empty(direct.Error);
        Assert.Contains("manual", direct.Output, StringComparison.Ordinal);
        Assert.Contains("--check", direct.Output, StringComparison.Ordinal);
        Assert.Contains(ProjectLinks.InstallationGuide, direct.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("--confirm", direct.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("--status", direct.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TableGateRejectsCommandButNotHelp()
    {
        var refused = await CaptureAsync(() => Program.Main(["upgrade", "--output", "table"]));
        Assert.Equal(1, refused.ExitCode);
        Assert.Empty(refused.Output);
        Assert.Contains("does not declare a table view", refused.Error, StringComparison.Ordinal);
        var help = await CaptureAsync(() => Program.Main(["upgrade", "--help", "--output", "table"]));
        Assert.Equal(0, help.ExitCode);
        Assert.Contains("manual", help.Output, StringComparison.Ordinal);
    }

    private static RootCommand CreateRoot(ReleaseHandler handler)
    {
        var root = new RootCommand();
        root.Options.Add(Program.OutputOption);
        root.Options.Add(Program.ConfirmOption);
        root.Options.Add(Program.DataOnlyOption);
        root.Subcommands.Add(UpgradeCommandFactory.Create(() => new HttpClient(handler), () => "1.0.0"));
        return root;
    }

    private static async Task<(int ExitCode, string Output, string Error)> CaptureAsync(Func<Task<int>> action)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var exit = await action();
            return (exit, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static string Release(string tag = "v1.0.1") =>
        JsonSerializer.Serialize(new { draft = false, prerelease = false, tag_name = tag });

    private sealed class ReleaseHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal ReleaseHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
            : this(_ => new HttpResponseMessage(status) { Content = new StringContent(body) }) { }

        internal List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Headers.Authorization);
            Assert.NotEmpty(request.Headers.UserAgent);
            Assert.Equal("application/vnd.github+json", Assert.Single(request.Headers.Accept).MediaType);
            Requests.Add(request.RequestUri!);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }

    private sealed class UnseekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class CancellingStream(CancellationTokenSource tokenSource) : MemoryStream
    {
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            tokenSource.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The check did not forward cancellation to the metadata stream.");
        }
    }
}
