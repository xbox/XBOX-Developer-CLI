// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using Microsoft.Extensions.Configuration;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

/// <summary>
/// Runs registered PlayFab commands and checks how a failure is reported. A failure the CLI
/// expects is reported by the command's own action, after the prefix that says what failed: one
/// line on stderr, nothing on stdout, and exit code 1. A bug escapes the action, and is reported
/// with its stack trace.
/// </summary>
/// <remarks>
/// A failure that escaped the action would reach the error boundary instead, which writes the
/// message with no prefix, so the expected line tells the two apart.
/// </remarks>
[Collection("ConsoleRedirect")]
public class PlayFabCommandActionTests
{
    private const string BaseUrlTemplateVariable = "XBOX_PLAYFAB__BASEURLTEMPLATE";

    [Theory]
    [InlineData("Request failed: boom", "playfab", "api", "/Admin/GetTitleData")]
    [InlineData("Request failed: boom", "playfab", "mps", "build", "list")]
    [InlineData("Request failed: boom", "playfab", "title-data", "get")]
    [InlineData("Request failed: boom", "playfab", "title-data", "set", "--key", "level", "--value", "5")]
    [InlineData("Request failed: boom", "playfab", "title-data", "delete", "--keys", "level")]
    [InlineData("Request failed: boom", "playfab", "cloudscript", "classic", "revision", "get")]
    [InlineData("Request failed: boom", "playfab", "cloudscript", "classic", "revision", "create", "--file", "main.js")]
    [InlineData("Request failed: boom", "playfab", "cloudscript", "classic", "revision", "publish", "--version", "1", "--revision", "2")]
    [InlineData("Request failed: boom", "playfab", "cloudscript", "classic", "version", "list")]
    [InlineData("Request failed: boom", "playfab", "cloudscript", "function", "list")]
    [InlineData(
        "Request failed: boom",
        "playfab", "cloudscript", "function", "register", "--type", "http", "--name", "MatchWebhook",
        "--url", "https://nodpublishers.azurewebsites.net/api/MatchWebhook")]
    [InlineData("Request failed: boom", "playfab", "cloudscript", "function", "unregister", "--name", "MatchWebhook")]
    [InlineData(
        "Request failed: boom",
        "playfab", "file", "list", "--entity-id", "TITLEPLAYER123", "--entity-type", "title_player_account")]
    [InlineData(
        "Download failed: boom",
        "playfab", "file", "download", "--entity-id", "TITLEPLAYER123", "--entity-type", "title_player_account",
        "--name", "save.json")]
    [InlineData(
        "Upload failed: boom",
        "playfab", "file", "upload", "--entity-id", "TITLEPLAYER123", "--entity-type", "title_player_account",
        "--file", "save.json")]
    [InlineData(
        "Request failed: boom",
        "playfab", "file", "delete", "--entity-id", "TITLEPLAYER123", "--entity-type", "title_player_account",
        "save.json", "--confirm")]
    [InlineData("Request failed: boom", "playfab", "group", "get", "--group-id", "GROUP123")]
    [InlineData("Request failed: boom", "playfab", "group", "delete", "--group-id", "GROUP123", "--confirm")]
    public async Task AClientThatCantBeBuiltIsReportedByTheAction(string expectedError, params string[] args)
    {
        var (exitCode, stdout, stderr) = await InvokeAsync(_ => throw new InvalidOperationException("boom"), args);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(expectedError + Environment.NewLine, stderr);
    }

    [Fact]
    public async Task AnAssetUploadWhoseClientCantBeBuiltIsReportedWithItsPrefix()
    {
        var file = Path.GetTempFileName();
        try
        {
            var (exitCode, stdout, stderr) = await InvokeAsync(
                _ => throw new InvalidOperationException("boom"),
                ["playfab", "mps", "asset", "upload", "--file", file, "--asset-name", "gameserver.zip"]);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Equal("Asset upload failed: boom" + Environment.NewLine, stderr);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>
    /// An action writes a failure's message the way the error boundary does: line breaks are
    /// kept, and any other control character is removed, so the message can't rewrite the
    /// terminal. <c>{file}</c> stands for a file that exists.
    /// </summary>
    [Theory]
    [InlineData("Request failed: ", "playfab", "api", "/Admin/GetTitleData")]
    [InlineData(
        "Request failed: ",
        "playfab", "file", "list", "--entity-id", "TITLEPLAYER123", "--entity-type", "title_player_account")]
    [InlineData(
        "Download failed: ",
        "playfab", "file", "download", "--entity-id", "TITLEPLAYER123", "--entity-type", "title_player_account",
        "--name", "save.json")]
    [InlineData(
        "Upload failed: ",
        "playfab", "file", "upload", "--entity-id", "TITLEPLAYER123", "--entity-type", "title_player_account",
        "--file", "save.json")]
    [InlineData(
        "Asset upload failed: ", "playfab", "mps", "asset", "upload", "--file", "{file}", "--asset-name", "gameserver.zip")]
    public async Task AFailuresMessageIsWrittenSafelyAfterItsPrefix(string prefix, params string[] args)
    {
        var file = Path.GetTempFileName();
        try
        {
            var (exitCode, stdout, stderr) = await InvokeAsync(
                _ => throw new InvalidOperationException("First line\u001b[2J\r\nSecond\u0007 line"),
                [.. args.Select(arg => arg == "{file}" ? file : arg)]);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Equal(
                $"{prefix}First line[2J{Environment.NewLine}Second line{Environment.NewLine}", stderr);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>A failure from inside the action gets the command's prefix too.</summary>
    [Fact]
    public async Task ADownloadToStdoutIsRefusedWithTheDownloadPrefix()
    {
        var handler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));

        var (exitCode, stdout, stderr) = await InvokeAsync(
            _ => PlayFabTestClient.Create(handler),
            ["playfab", "file", "download", "--entity-id", "TITLEPLAYER123", "--entity-type", "title_player_account",
                "--name", "save.json", "--dest", "-"]);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(
            "Download failed: Downloading to stdout with '--dest -' isn't implemented yet. Pass a file path to --dest." +
                Environment.NewLine,
            stderr);
        Assert.Null(handler.LastRequest);
    }

    /// <summary>
    /// The actions catch only failures the CLI expects, so System.CommandLine prints a bug with its
    /// stack trace, rather than as a failed request.
    /// </summary>
    [Theory]
    [InlineData("playfab", "api", "/Admin/GetTitleData")]
    [InlineData("playfab", "title-data", "get")]
    [InlineData(
        "playfab", "file", "download", "--entity-id", "TITLEPLAYER123", "--entity-type", "title_player_account",
        "--name", "save.json")]
    [InlineData(
        "playfab", "file", "upload", "--entity-id", "TITLEPLAYER123", "--entity-type", "title_player_account",
        "--file", "save.json")]
    public async Task ABugIsReportedWithItsStackTrace(params string[] args)
    {
        var (exitCode, stdout, stderr) = await InvokeAsync(_ => throw new NullReferenceException(), args);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.StartsWith("Unhandled exception: System.NullReferenceException", stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>playfab api</c> takes the path and body from the user, so a path the client refuses, and a
    /// body on stdin that isn't UTF-8, are reported as the errors they are, not as bugs, and nothing
    /// is sent.
    /// </summary>
    [Theory]
    [InlineData("Request failed: API path must not contain a backslash.", "playfab", "api", "/Admin\\GetTitleData")]
    [InlineData("Request failed: Unable to translate bytes [FF]", "playfab", "api", "/Admin/GetTitleData", "--body-file", "-")]
    public async Task ApiReportsAPathOrBodyItCantSend(string reason, params string[] args)
    {
        var handler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var stdin = new MemoryStream([0xFF]);

        var (exitCode, stdout, stderr) = await InvokeAsync(_ => PlayFabTestClient.Create(handler), args, stdin);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.StartsWith(reason, stderr, StringComparison.Ordinal);
        Assert.Null(handler.LastRequest);
    }

    /// <summary>
    /// A base URL template that won't format or parse is a settings error that names the setting,
    /// not a bug.
    /// </summary>
    [Theory]
    [InlineData("xboxdevcli.playfabapi.com")]
    [InlineData("https://{titleId}.playfabapi.com")]
    public async Task AnInvalidBaseUrlTemplateIsReportedAsASettingsError(string template)
    {
        var original = Environment.GetEnvironmentVariable(BaseUrlTemplateVariable);
        Environment.SetEnvironmentVariable(BaseUrlTemplateVariable, template);
        try
        {
            var (exitCode, stdout, stderr) = await InvokeAsync(
                clientFactory: null,
                ["--playfab-title-id", "ABC123", "--disable-telemetry", "playfab", "title-data", "get"]);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.StartsWith(
                "Request failed: PlayFab:BaseUrlTemplate isn't a valid URL template: ", stderr, StringComparison.Ordinal);
            Assert.EndsWith(
                "Check XBOX_PLAYFAB__BASEURLTEMPLATE." + Environment.NewLine, stderr, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(BaseUrlTemplateVariable, original);
        }
    }

    /// <summary>
    /// Runs <paramref name="args"/> through the root command, with its PlayFab client from
    /// <paramref name="clientFactory"/>, or the real one when it's <c>null</c>, and stdin from
    /// <paramref name="stdin"/>. Endpoint warnings are left out of the stderr it returns.
    /// </summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr)> InvokeAsync(
        Func<IConfiguration, PlayFabApiClient>? clientFactory,
        string[] args,
        Stream? stdin = null)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Program.ApiClientFactory = clientFactory;
        Program.StandardInputFactory = stdin is null ? null : () => stdin;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var parseResult = Program.BuildRootCommand().Parse(args, Program.NoResponseFileConfig);
            Assert.Empty(parseResult.Errors);
            var exitCode = await parseResult.InvokeAsync();
            return (exitCode, stdout.ToString(), TestEnvironment.WithoutEndpointWarnings(stderr.ToString()));
        }
        finally
        {
            Program.ApiClientFactory = null;
            Program.StandardInputFactory = null;
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}
