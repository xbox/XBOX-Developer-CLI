// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client.Extensions.Msal;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Commands;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

// status writes to Console.Out; tests that redirect it must not run in parallel with
// other tests that read/write the console.
[Collection("ConsoleRedirect")]
public class StatusCommandTests
{
    /// <summary>
    /// A client ID nothing is signed in to, so tests that run the real <c>auth status</c> never
    /// find the developer's own account.
    /// </summary>
    private const string UnusedClientId = "00000000-0000-0000-0000-000000000000";

    [Fact]
    public async Task Status_WithOutput_EmitsJsonWithSignedInFlag()
    {
        var (exitCode, stdout, _) = await RunStatusAsync("--output", "json-compact");

        Assert.Equal(0, exitCode);
        using var doc = JsonDocument.Parse(stdout);
        Assert.True(doc.RootElement.TryGetProperty("signedIn", out var signedIn));
        Assert.True(signedIn.ValueKind is JsonValueKind.True or JsonValueKind.False);
    }

    [Fact]
    public async Task Status_WithoutOutput_DefaultsToNonJsonText()
    {
        // Default (no --output) must be human-readable text, never a JSON document.
        var (_, stdout, _) = await RunStatusAsync();

        Assert.False(stdout.TrimStart().StartsWith('{'),
            "status without --output should print human-readable text, not JSON.");
    }

    /// <summary>
    /// Status reports the sign-in only. The PlayFab title is a context value, which
    /// <c>context get</c> reports with every other one.
    /// </summary>
    [Fact]
    public async Task Status_ReportsTheAccountAndNotTheTitle()
    {
        var auth = new FakeAuthenticationService(FakeAuthenticationService.CreateToken());

        var json = await RunStatusAsync(auth, "--playfab-title-id", "ABC12", "--output", "json-compact");
        var text = await RunStatusAsync(auth, "--playfab-title-id", "ABC12");

        Assert.Equal(0, json.ExitCode);
        using var doc = JsonDocument.Parse(json.Stdout);
        Assert.True(doc.RootElement.GetProperty("signedIn").GetBoolean());
        Assert.Equal("dev@nodpublishers.com", doc.RootElement.GetProperty("username").GetString());
        Assert.False(doc.RootElement.TryGetProperty("titleId", out _));
        Assert.DoesNotContain("ABC12", json.Stdout, StringComparison.Ordinal);
        Assert.Contains("Signed in as: dev@nodpublishers.com", text.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("PlayFab Title ID", text.Stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// An expired session is still signed in as far as the token cache goes; only the token
    /// fields are missing.
    /// </summary>
    [Fact]
    public async Task Status_ExpiredSessionOmitsTheTokenFields()
    {
        var auth = new FakeAuthenticationService(token: null)
        {
            CachedAccount = FakeAuthenticationService.CreateAccount(),
        };

        var json = await RunStatusAsync(auth, "--output", "json-compact");
        var text = await RunStatusAsync(auth);

        Assert.Equal(0, json.ExitCode);
        using var doc = JsonDocument.Parse(json.Stdout);
        Assert.True(doc.RootElement.GetProperty("signedIn").GetBoolean());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("tokenExpiresAt").ValueKind);
        Assert.Equal(0, text.ExitCode);
        Assert.Contains("Signed in as:", text.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Token expires at", text.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_NotSignedIn()
    {
        var auth = new FakeAuthenticationService(token: null);

        var json = await RunStatusAsync(auth, "--output", "json-compact");
        var text = await RunStatusAsync(auth);

        Assert.Equal(0, json.ExitCode);
        Assert.Equal("""{"signedIn":false}""", json.Stdout.TrimEnd());
        Assert.Equal(0, text.ExitCode);
        Assert.Equal(
            "Not signed in. Run 'xbox auth login' to authenticate.",
            text.Stdout.TrimEnd());
    }

    /// <summary>
    /// A configuration or sign-in state that can't be read isn't reported as signed out: status
    /// fails with the reason on stderr and nothing on stdout, as JSON when <c>--output</c> asks for it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Status_WhenConfigurationOrSignInCannotBeRead(bool configurationFails)
    {
        using var unreadable = configurationFails ? TestSettings.UseUnreadable() : null;
        string[] args = [];
        Func<ParseResult, AuthenticationService> authFactory = parseResult =>
        {
            _ = Program.LoadConfiguration(parseResult);
            throw new InvalidOperationException("The token cache can't be read.");
        };

        var json = await RunStatusAsync(authFactory, [.. args, "--output", "json-compact"]);
        var text = await RunStatusAsync(authFactory, args);

        string expected = configurationFails ? TestSettings.UnreadableMessage : "The token cache can't be read.";
        Assert.Equal(1, json.ExitCode);
        Assert.Empty(json.Stdout);
        using var error = JsonDocument.Parse(json.Stderr);
        Assert.False(error.RootElement.GetProperty("signedIn").GetBoolean());
        Assert.False(error.RootElement.TryGetProperty("authenticationMode", out _));
        Assert.Contains(expected, error.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(1, text.ExitCode);
        Assert.Empty(text.Stdout);
        Assert.StartsWith("Failed to retrieve status: ", text.Stderr, StringComparison.Ordinal);
        Assert.Contains(expected, text.Stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// A settings file an earlier release read, still beside the executable, doesn't break a
    /// failure's JSON on stderr; text output still notes it before the error.
    /// </summary>
    [Fact]
    public async Task Status_WithARetiredSettingsFile_FailureIsStillJson()
    {
        var directory = Directory.CreateTempSubdirectory("xbox-install-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "appsettings.json"), "{}");
            using var install = LoadedConfiguration.UseInstallDirectory(directory.FullName);
            Func<ParseResult, AuthenticationService> authFactory =
                _ => throw new InvalidOperationException("The token cache can't be read.");

            var json = await RunStatusAsync(authFactory, "--output", "json-compact");
            var text = await RunStatusAsync(authFactory);

            Assert.Equal(1, json.ExitCode);
            Assert.Empty(json.Stdout);
            using var error = JsonDocument.Parse(json.Stderr);
            Assert.False(error.RootElement.GetProperty("signedIn").GetBoolean());
            Assert.Equal(1, text.ExitCode);
            Assert.StartsWith("Note: ", text.Stderr, StringComparison.Ordinal);
            Assert.Contains("Failed to retrieve status: The token cache can't be read.", text.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Built-in settings that aren't valid JSON fail the whole invocation with exit code 1 and the
    /// command's JSON error, before the token cache is read.
    /// </summary>
    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    public async Task Status_WithInvalidBuiltInJson_ReportsAJsonError(string settings)
    {
        using var invalid = TestSettings.Use(settings);

        var result = await RunStatusAsync("--output", "json-compact");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        using var error = JsonDocument.Parse(result.Stderr);
        Assert.False(error.RootElement.GetProperty("signedIn").GetBoolean());
        Assert.Equal(LoadedConfiguration.InvalidBuiltInSettingsMessage, error.RootElement.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("json")]
    [InlineData("json-compact")]
    public async Task Status_WhenCachePersistenceFails_ReportsError(string? outputFormat)
    {
        const string message = "The token cache can't be persisted.";
        var auth = new FakeAuthenticationService(token: null)
        {
            CachedAccountException = new MsalCachePersistenceException(message),
        };

        var result = await RunStatusAsync(auth, outputFormat is null ? [] : ["--output", outputFormat]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        if (outputFormat is null)
        {
            Assert.Equal($"Failed to retrieve status: {message}{Environment.NewLine}", result.Stderr);
        }
        else
        {
            using var error = JsonDocument.Parse(result.Stderr);
            Assert.False(error.RootElement.GetProperty("signedIn").GetBoolean());
            Assert.Equal(message, error.RootElement.GetProperty("error").GetString());
        }
    }

    /// <summary>
    /// Ctrl-C while reading the account or refreshing the token must not look like a successful
    /// status: nothing is printed and the command fails.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Status_WhenCanceled_FailsWithoutReportingStatus(bool cancelAccountLookup)
    {
        var canceled = new OperationCanceledException();
        var auth = new FakeAuthenticationService(FakeAuthenticationService.CreateToken())
        {
            CachedAccountException = cancelAccountLookup ? canceled : null,
            TokenRequestException = cancelAccountLookup ? null : canceled,
        };

        var json = await RunStatusAsync(auth, "--output", "json-compact");
        var text = await RunStatusAsync(auth);

        Assert.Equal(1, json.ExitCode);
        Assert.Equal(string.Empty, json.Stdout);
        Assert.Equal(1, text.ExitCode);
        Assert.Equal(string.Empty, text.Stdout);
    }

    /// <summary>Runs the real <c>auth status</c> for a client ID nobody is signed in to.</summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunStatusAsync(params string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var outWriter = new StringWriter();
        using var errorWriter = new StringWriter();
        Console.SetOut(outWriter);
        Console.SetError(errorWriter);

        try
        {
            var exitCode = await Program.Main(["--client-id", UnusedClientId, "auth", "status", .. args]);
            return (exitCode, outWriter.ToString(), errorWriter.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static Task<(int ExitCode, string Stdout, string Stderr)> RunStatusAsync(
        AuthenticationService auth,
        params string[] args) =>
        RunStatusAsync(_ => auth, args);

    /// <summary>Runs <c>status</c> with the sign-in service supplied, never the real token cache.</summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunStatusAsync(
        Func<ParseResult, AuthenticationService> authFactory,
        params string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var outWriter = new StringWriter();
        using var errorWriter = new StringWriter();
        Console.SetOut(outWriter);
        Console.SetError(errorWriter);

        try
        {
            var root = new RootCommand("XBOX Dev CLI")
            {
                Program.ClientIdOption,
                Program.TenantIdOption,
                Program.ScopesOption,
                Program.PlayFabTitleIdOption,
                Program.OutputOption,
                Program.DisableTelemetryOption,
                StatusCommandFactory.Create(authFactory),
            };
            CommandErrorBoundary.Install(root);
            var exitCode = await root.Parse(["status", .. args], Program.NoResponseFileConfig).InvokeAsync();
            return (exitCode, outWriter.ToString(), errorWriter.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}
