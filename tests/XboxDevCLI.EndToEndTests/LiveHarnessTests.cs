// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Diagnostics;
using Xunit;

namespace XboxDevCLI.EndToEndTests;

/// <summary>Offline checks of the live opt-in and the child process setup.</summary>
public sealed class LiveHarnessTests : IDisposable
{
    private readonly string _cliDirectory = Directory.CreateTempSubdirectory("xbox-e2e-cli-").FullName;

    public LiveHarnessTests()
    {
        File.WriteAllText(Path.Combine(_cliDirectory, CliRunner.ExecutableName), string.Empty);
        File.WriteAllText(Path.Combine(_cliDirectory, CliRunner.CandidateDefaultsFileName), "{}");
    }

    [Theory]
    [InlineData(LiveService.Authentication)]
    [InlineData(LiveService.PlayFab)]
    [InlineData(LiveService.XboxPartner)]
    public void GetSkipReason_SkipsWhenNotRequested(LiveService service)
    {
        Assert.Equal(
            LiveSettings.NotRequestedReason,
            LiveSettings.GetSkipReason(service, _ => null));
    }

    [Theory]
    [InlineData(LiveService.PlayFab, "PlayFab")]
    [InlineData(LiveService.XboxPartner, "XboxPartner")]
    public void GetSkipReason_SkipsADisabledServiceWithTheReason(LiveService service, string name)
    {
        var variables = E2EProfileTests.Variables();

        Assert.Null(LiveSettings.GetSkipReason(LiveService.Authentication, variables));
        Assert.StartsWith(
            $"{name} is disabled in environment 'e2e-nod-arena': ",
            LiveSettings.GetSkipReason(service, variables));
    }

    [Theory]
    [InlineData(LiveService.Authentication)]
    [InlineData(LiveService.PlayFab)]
    [InlineData(LiveService.XboxPartner)]
    public void GetSkipReason_RunsSoThatInvalidConfigurationFails(LiveService service)
    {
        var variables = E2EProfileTests.Variables((E2EProfiles.AudienceVariable, null));

        Assert.Null(LiveSettings.GetSkipReason(service, variables));
        Assert.Throws<InvalidDataException>(() => LiveSettings.Load(variables));
    }

    [Fact]
    public void Load_FailsClosedWhenOptedInWithoutSettings()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => LiveSettings.Load(E2EProfileTests.Variables()));

        Assert.Contains(LiveSettings.CliPathVariable, exception.Message, StringComparison.Ordinal);
        Assert.Contains(LiveSettings.ClientIdVariable, exception.Message, StringComparison.Ordinal);
        Assert.Contains("id-token: write", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RequiresAnExplicitLiveOptIn()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            LiveSettings.Load(CompleteVariables((LiveSettings.EnvironmentVariable, null))));

        Assert.Equal(LiveSettings.NotRequestedReason, exception.Message);
    }

    [Fact]
    public void Load_AcceptsCompleteSettings()
    {
        var settings = LiveSettings.Load(CompleteVariables());

        Assert.Equal("e2e-nod-arena", settings.Profile.Environment);
        Assert.Equal("client", settings.ClientId);
        Assert.Equal(Path.GetFullPath(_cliDirectory), settings.CliDirectory);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Load_ValidatesTheCandidateScopeWhenTheOverrideIsOmitted(string? scope)
    {
        var variables = CompleteVariables((LiveSettings.ScopesVariable, scope));
        File.WriteAllText(
            Path.Combine(_cliDirectory, CliRunner.CandidateDefaultsFileName),
            """{"AzureAd":{"Scopes":"00000000-0000-0000-0000-000000000042/.default"}}""");

        var settings = LiveSettings.Load(variables);
        using var runner = CliRunner.Create(settings, new System.Collections.Hashtable());

        Assert.Null(settings.Profile.Scope);
        Assert.Equal("00000000-0000-0000-0000-000000000042/.default", settings.Scope);
        Assert.Empty(Directory.EnumerateFiles(
            Path.Combine(runner.WorkingDirectory, "..", "cli"), "appsettings*.json"));
    }

    [Fact]
    public void Load_RequiresTheCandidateDefaults()
    {
        File.Delete(Path.Combine(_cliDirectory, CliRunner.CandidateDefaultsFileName));

        var exception = Assert.Throws<InvalidOperationException>(() => LiveSettings.Load(CompleteVariables()));

        Assert.Contains(CliRunner.CandidateDefaultsFileName, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"AzureAd":{"Scopes":null}}""")]
    [InlineData("""{"AzureAd":{"Scopes":""}}""")]
    [InlineData("""{"AzureAd":{"Scopes":"other/.default"}}""")]
    public void Load_RejectsAMissingOrMismatchedCandidateScope(string candidateDefaults)
    {
        File.WriteAllText(Path.Combine(_cliDirectory, CliRunner.CandidateDefaultsFileName), candidateDefaults);

        var exception = Assert.Throws<InvalidDataException>(() =>
            LiveSettings.Load(CompleteVariables((LiveSettings.ScopesVariable, null))));

        Assert.Contains("AzureAd:Scopes", exception.Message, StringComparison.Ordinal);
        Assert.Contains(E2EProfiles.AudienceVariable, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{titleId}.playfabapi.com")]
    public void BuildEnvironment_PointsTheCliAtTheProfile(string? header)
    {
        var settings = LiveSettings.Load(CompleteVariables((E2EProfiles.HostHeaderVariable, header)));

        var environment = CliRunner.BuildEnvironment(new System.Collections.Hashtable(), settings, "/state");

        Assert.Equal(settings.Scope, environment[LiveSettings.ScopesVariable]);
        Assert.Equal(settings.Profile.PlayFab.BaseUrlTemplate, environment[CliRunner.PlayFabBaseUrlTemplateVariable]);
        Assert.Equal(header ?? CliRunner.NoHostHeader, environment[CliRunner.PlayFabHostHeaderVariable]);
        Assert.Equal(settings.Profile.XboxPartner.BaseUrl, environment[CliRunner.XboxPartnerBaseUrlVariable]);
    }

    [Fact]
    public void BuildEnvironment_DropsInheritedCliAndHarnessVariables()
    {
        var settings = LiveSettings.Load(CompleteVariables());
        var parent = new System.Collections.Hashtable
        {
            ["PATH"] = "/bin",
            ["ACTIONS_ID_TOKEN_REQUEST_URL"] = "https://token",
            ["XBOX_PLAYFAB_TITLE_ID"] = "PROD1",
            ["xbox_seller_id"] = "1",
            ["XBOX_AZUREAD__SCOPES"] = "other/.default",
            ["E2E_GITHUB_ENVIRONMENT"] = "e2e-nod-rally",
            ["E2E_PLAYFAB_TITLE_ID"] = "OTHER1",
        };

        var environment = CliRunner.BuildEnvironment(parent, settings, "/state");

        Assert.Equal("/bin", environment["PATH"]);
        Assert.Equal("https://token", environment["ACTIONS_ID_TOKEN_REQUEST_URL"]);
        Assert.Equal("githuboidc", environment["XBOX_AUTHENTICATION__MODE"]);
        Assert.Equal("client", environment[LiveSettings.ClientIdVariable]);
        Assert.Equal("false", environment["XBOX_TELEMETRY__ENABLED"]);
        Assert.Equal("/state", environment["HOME"]);
        Assert.DoesNotContain("XBOX_PLAYFAB_TITLE_ID", environment.Keys);
        Assert.DoesNotContain("xbox_seller_id", environment.Keys);
        Assert.Equal(settings.Scope, environment["XBOX_AZUREAD__SCOPES"]);
        Assert.DoesNotContain("E2E_GITHUB_ENVIRONMENT", environment.Keys);
        Assert.DoesNotContain("E2E_PLAYFAB_TITLE_ID", environment.Keys);
    }

    [Fact]
    public async Task RunProcessAsync_CapturesExitCodeAndStreams()
    {
        var result = await CliRunner.RunProcessAsync(DotNet("--version"), TimeSpan.FromMinutes(1));

        Assert.Equal(0, result.ExitCode);
        Assert.NotEmpty(result.StandardOutput.Trim());
    }

    [Fact]
    public async Task RunProcessAsync_KillsAProcessThatOverrunsItsTimeout()
    {
        var startInfo = OperatingSystem.IsWindows()
            ? Shell("cmd.exe", "/c", "ping -n 30 127.0.0.1 > nul")
            : Shell("/bin/sh", "-c", "sleep 30");

        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(
            () => CliRunner.RunProcessAsync(startInfo, TimeSpan.FromMilliseconds(500)));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), "The timed-out process was not killed promptly.");
    }

    [Fact]
    public void Describe_OmitsStdoutAndTruncatesStderr()
    {
        var result = new CliResult(1, "{\"secret\":\"player data\"}", new string('e', 5000));

        var description = result.Describe();

        Assert.DoesNotContain("player data", description, StringComparison.Ordinal);
        Assert.Contains("24 characters on stdout", description, StringComparison.Ordinal);
        Assert.Contains("(truncated)", description, StringComparison.Ordinal);
        Assert.True(description.Length < 2200);
    }

    public void Dispose() => Directory.Delete(_cliDirectory, recursive: true);

    private Func<string, string?> CompleteVariables(params (string Name, string? Value)[] overrides) =>
        E2EProfileTests.Variables(
        [
            (LiveSettings.CliPathVariable, _cliDirectory),
            (LiveSettings.ClientIdVariable, "client"),
            (LiveSettings.TenantIdVariable, "tenant"),
            (LiveSettings.OidcRequestUrlVariable, "https://token"),
            (LiveSettings.OidcRequestTokenVariable, "request-token"),
            .. overrides,
        ]);

    private static ProcessStartInfo DotNet(params string[] arguments) => Shell("dotnet", arguments);

    private static ProcessStartInfo Shell(string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}
