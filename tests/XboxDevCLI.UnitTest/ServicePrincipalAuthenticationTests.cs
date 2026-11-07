// Copyright (C) Microsoft Corporation. All rights reserved.

using System.CommandLine;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Commands;
using XboxDevCLI.Services;
using XboxDevCLI.Telemetry;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public class ServicePrincipalAuthenticationTests
{
    private const string Scope = "448adbda-b8d8-4f33-a1b0-ac58cf44d4c1/.default";

    private static readonly AutomationAuthenticationSettings ClientSecret = new(
        AuthenticationMode.ClientSecret,
        "15fc7b3e-912c-4c49-ad3d-734293d93a23",
        "3778efb3-5411-47ab-a790-b5db0eed39a6",
        "/tmp/secret");

    private const string AppPayload =
        """{"tid":"15fc7b3e-912c-4c49-ad3d-734293d93a23","oid":"sp","sub":"sp","appid":"3778efb3-5411-47ab-a790-b5db0eed39a6","aud":"api://playfab","idtyp":"app"}""";
    private static readonly string AppJwt = AutomationTestTokens.Jwt(AppPayload);

    [Fact]
    public async Task AcquireTokenSilent_UsesProviderCreatedOnceWithoutPublicClientSettings()
    {
        var provider = new FakeProvider(AutomationTestTokens.Result(AppJwt));
        var created = 0;
        var service = new AuthenticationService(ScopesOnlyConfig(Scope), null, ClientSecret, (settings, scopes) =>
        {
            created++;
            Assert.Same(ClientSecret, settings);
            Assert.Equal([Scope], scopes);
            return provider;
        });

        Assert.True(service.IsAutomation);
        Assert.Equal(0, created);

        var first = await service.AcquireTokenSilentAsync(CancellationToken.None);
        var second = await service.AcquireTokenSilentAsync(CancellationToken.None);

        Assert.Equal(AppJwt, first!.AccessToken);
        Assert.Same(first, second);
        Assert.Equal(1, created);
        Assert.Equal(2, provider.Calls);
    }

    [Theory]
    [InlineData("clientsecret")]
    [InlineData("certificate")]
    [InlineData("federatedtoken")]
    [InlineData("githuboidc")]
    [InlineData("azurecli")]
    [InlineData("managedidentity")]
    public async Task AcquireTokenSilent_AllAutomationModes_KeepOneIdentity(string mode)
    {
        foreach (var claim in new[] { "tid", "oid", "appid" })
        {
            var payload = JsonSerializer.Deserialize<Dictionary<string, string>>(AppPayload)!;
            var first = AutomationTestTokens.Result(AppJwt);
            payload[claim] = "different-identity";
            var changed = AutomationTestTokens.Result(AutomationTestTokens.Jwt(JsonSerializer.Serialize(payload)));
            payload = JsonSerializer.Deserialize<Dictionary<string, string>>(AppPayload)!;
            payload["azp"] = payload["appid"];
            payload.Remove("appid");
            var sameIdentity = AutomationTestTokens.Result(AutomationTestTokens.Jwt(JsonSerializer.Serialize(payload)));
            var provider = new FakeProvider(first, changed, sameIdentity);
            var service = Automation(provider, Enum.Parse<AuthenticationMode>(mode, ignoreCase: true));

            Assert.Same(first, await service.AcquireTokenSilentAsync(CancellationToken.None));

            var error = await Assert.ThrowsAsync<AuthenticationConfigurationException>(() =>
                service.AcquireTokenSilentAsync(CancellationToken.None));

            Assert.Contains("identity changed", error.Message, StringComparison.Ordinal);
            Assert.Contains(mode, error.Message, StringComparison.Ordinal);
            Assert.Contains("rerun the command", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(first.AccessToken, error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(changed.AccessToken, error.Message, StringComparison.Ordinal);
            Assert.Same(sameIdentity, await service.AcquireTokenSilentAsync(CancellationToken.None));
            Assert.Equal(3, provider.Calls);
        }
    }

    [Theory]
    [InlineData("tid", null)]
    [InlineData("oid", null)]
    [InlineData("appid", null)]
    [InlineData("tid", " ")]
    [InlineData("oid", " ")]
    [InlineData("appid", " ")]
    public async Task AcquireTokenSilent_RefusesMissingIdentityClaimsInitiallyAndOnRefresh(string claim, string? value)
    {
        var payload = JsonSerializer.Deserialize<Dictionary<string, string?>>(AppPayload)!;
        payload[claim] = value;
        var incomplete = AutomationTestTokens.Result(AutomationTestTokens.Jwt(JsonSerializer.Serialize(payload)));
        var valid = AutomationTestTokens.Result(AppJwt);
        foreach (var onRefresh in new[] { false, true })
        {
            var provider = onRefresh
                ? new FakeProvider(valid, incomplete, valid)
                : new FakeProvider(incomplete, valid);
            var service = Automation(provider);
            if (onRefresh)
            {
                await service.AcquireTokenSilentAsync(CancellationToken.None);
            }

            var error = await Assert.ThrowsAsync<AuthenticationConfigurationException>(() =>
                service.AcquireTokenSilentAsync(CancellationToken.None));

            Assert.Contains("without the tenant, object, or application ID", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(incomplete.AccessToken, error.Message, StringComparison.Ordinal);
            Assert.Same(valid, await service.AcquireTokenSilentAsync(CancellationToken.None));
        }
    }

    [Theory]
    [InlineData("""{"oid":"o","sub":"x","scp":"user_impersonation"}""", "user")]
    [InlineData("""{"oid":"o","sub":"x"}""", "unknown")]
    [InlineData(null, "unknown")]
    public async Task AcquireTokenSilent_RefusesTokensThatAreNotAppOnly(string? payload, string principalType)
    {
        var token = payload is null ? "opaque-token" : AutomationTestTokens.Jwt(payload);
        var service = Automation(new FakeProvider(AutomationTestTokens.Result(token)));

        var error = await Assert.ThrowsAsync<AuthenticationConfigurationException>(() =>
            service.AcquireTokenSilentAsync(CancellationToken.None));

        Assert.Contains($"principal type is '{principalType}'", error.Message, StringComparison.Ordinal);
        Assert.Contains("xbox auth login", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(token, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcquireTokenSilent_PropagatesProviderFailure()
    {
        var service = new AuthenticationService(ScopesOnlyConfig(Scope), null, ClientSecret,
            (_, _) => new FakeProvider(new MsalServiceException("invalid_client", "AADSTS7000215: Invalid client secret.")));

        var error = await Assert.ThrowsAsync<MsalServiceException>(() => service.AcquireTokenSilentAsync(CancellationToken.None));
        Assert.Contains("AADSTS7000215", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A service principal has no cached account, so it never uses saved context.</summary>
    [Fact]
    public async Task FromCache_ServicePrincipal_HasNoSavedContextIdentity()
    {
        var service = new AuthenticationService(ScopesOnlyConfig(Scope), null, ClientSecret);

        Assert.Null(await SignedInIdentity.FromCacheAsync(service, CancellationToken.None));
    }

    [Fact]
    public void Constructor_RejectsMultipleScopesWithoutCreatingProvider()
    {
        Assert.Throws<AuthenticationConfigurationException>(() => new AuthenticationService(
            ScopesOnlyConfig("openid " + Scope), null, ClientSecret,
            (_, _) => throw new InvalidOperationException("Must not create a provider.")));
    }

    [Fact]
    public async Task InteractiveOperations_AreNotApplicable()
    {
        var service = new AuthenticationService(ScopesOnlyConfig(Scope), null, ClientSecret);

        var login = await Assert.ThrowsAsync<InvalidOperationException>(() => service.LoginAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.LogoutAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetCachedAccountAsync(CancellationToken.None));

        Assert.Contains("auth login' doesn't apply", login.Message, StringComparison.Ordinal);
        Assert.Contains("is 'clientsecret'", login.Message, StringComparison.Ordinal);
        Assert.Contains("XBOX_AUTHENTICATION__* settings", login.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("login")]
    [InlineData("logout")]
    public async Task LoginAndLogout_RefuseInAutomationMode(string commandName)
    {
        var service = Automation(new FakeProvider(AutomationTestTokens.Result(AppJwt)));
        var command = commandName == "login"
            ? LoginCommandFactory.Create(_ => service)
            : LogoutCommandFactory.Create(_ => service);

        var (exitCode, stdout, stderr) = await RunAsync(command, commandName);

        Assert.Equal(1, exitCode);
        Assert.Empty(stdout);
        Assert.Contains($"auth {commandName}' doesn't apply", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_Json_ReportsServicePrincipalClaims()
    {
        var service = Automation(new FakeProvider(AutomationTestTokens.Result(AppJwt)));

        var (exitCode, stdout, stderr) = await RunAsync(
            StatusCommandFactory.Create(_ => service), "--playfab-title-id ABC12 status --output json-compact");

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr);
        Assert.DoesNotContain(AppJwt, stdout, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(stdout);
        var root = json.RootElement;
        Assert.True(root.GetProperty("signedIn").GetBoolean());
        Assert.Equal("clientsecret", root.GetProperty("authenticationMode").GetString());
        Assert.Equal("app", root.GetProperty("principalType").GetString());
        Assert.Equal("15fc7b3e-912c-4c49-ad3d-734293d93a23", root.GetProperty("tenantId").GetString());
        Assert.Equal("3778efb3-5411-47ab-a790-b5db0eed39a6", root.GetProperty("appId").GetString());
        Assert.Equal("sp", root.GetProperty("objectId").GetString());
        Assert.Equal("api://playfab", root.GetProperty("audience").GetString());
        Assert.Equal(0, root.GetProperty("roles").GetArrayLength());
        Assert.False(root.TryGetProperty("titleId", out _));
    }

    [Fact]
    public async Task Status_Text_ReportsServicePrincipal()
    {
        var service = Automation(new FakeProvider(AutomationTestTokens.Result(AppJwt)));

        var (exitCode, stdout, _) = await RunAsync(StatusCommandFactory.Create(_ => service), "status");

        Assert.Equal(0, exitCode);
        Assert.Contains("Authentication mode: clientsecret", stdout, StringComparison.Ordinal);
        Assert.Contains("Principal type: app", stdout, StringComparison.Ordinal);
        Assert.Contains("Roles: (none)", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_AcquisitionFailure_FailsWithErrorOnStderr()
    {
        var service = Automation(new FakeProvider(new MsalServiceException("unauthorized_client", "AADSTS700016: app not found")));

        var (jsonExit, jsonOut, jsonErr) = await RunAsync(StatusCommandFactory.Create(_ => service), "status --output json-compact");
        var (textExit, textOut, textErr) = await RunAsync(StatusCommandFactory.Create(_ => service), "status");

        Assert.Equal(1, jsonExit);
        Assert.Empty(jsonOut);
        using var json = JsonDocument.Parse(jsonErr);
        Assert.False(json.RootElement.GetProperty("signedIn").GetBoolean());
        Assert.Equal("clientsecret", json.RootElement.GetProperty("authenticationMode").GetString());
        Assert.Contains("AADSTS700016", json.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);

        Assert.Equal(1, textExit);
        Assert.Empty(textOut);
        Assert.Contains("Failed to acquire a token in 'clientsecret' mode", textErr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_ConfigurationError_FailsWithErrorOnStderr()
    {
        var (exitCode, stdout, stderr) = await RunAsync(
            StatusCommandFactory.Create(_ => throw new AuthenticationConfigurationException("bad mode")),
            "status --output json-compact");

        Assert.Equal(1, exitCode);
        Assert.Empty(stdout);
        using var json = JsonDocument.Parse(stderr);
        Assert.False(json.RootElement.GetProperty("signedIn").GetBoolean());
        Assert.Equal("bad mode", json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void Invocation_InspectionAndAuthenticationShareCapturedInputsAcrossContextReparse()
    {
        var originalReader = AutomationAuthenticationSettings.ReadEnvironment;
        var variables = new Dictionary<string, string>
        {
            [AutomationAuthenticationSettings.ModeVariable] = "managedidentity",
            [AutomationAuthenticationSettings.ClientIdVariable] = ClientSecret.ClientId!,
        };
        var reads = 0;
        AutomationAuthenticationSettings.ReadEnvironment = () =>
        {
            reads++;
            return variables;
        };
        try
        {
            var root = Program.BuildRootCommand();
            var original = root.Parse("auth status", Program.NoResponseFileConfig);
            Program.Prepare(original);
            var service = Program.GetAuthentication(original);
            variables.Clear();
            var resolved = root.Parse("auth status", Program.NoResponseFileConfig);
            Program.ShareConfiguration(original, resolved);
            Program.Prepare(resolved);

            Assert.Same(service, Program.GetAuthentication(resolved));
            Assert.Equal("managedidentity", service.SelectedModeName);
            Assert.Equal("managedidentity", Program.GetInvocation(resolved).AuthenticationModeName);
            Assert.True(service.IsAutomation);
            Assert.Equal(1, reads);

            var next = root.Parse("auth status", Program.NoResponseFileConfig);
            Assert.Equal("user", Program.GetInvocation(next).AuthenticationModeName);
            Assert.Equal(2, reads);
        }
        finally
        {
            AutomationAuthenticationSettings.ReadEnvironment = originalReader;
        }
    }

    [Theory]
    [InlineData("MODE")]
    [InlineData("CLIENTID")]
    [InlineData("CLIENTSECRET__FILE")]
    public void Invocation_InvalidSnapshotCannotFallBackAfterEnvironmentChanges(string setting)
    {
        var originalReader = AutomationAuthenticationSettings.ReadEnvironment;
        var variables = ConflictingAuthenticationEnvironment(setting);
        AutomationAuthenticationSettings.ReadEnvironment = () => variables;
        try
        {
            var root = Program.BuildRootCommand();
            var original = root.Parse("auth status", Program.NoResponseFileConfig);
            Assert.Equal("invalid", Program.GetInvocation(original).AuthenticationModeName);
            Assert.True(Program.GetInvocation(original).IsAutomationSelected);
            var error = Assert.Throws<AuthenticationConfigurationException>(() => Program.Prepare(original));
            Assert.Contains("differ only by case", error.Message, StringComparison.Ordinal);

            variables.Clear();
            var resolved = root.Parse("auth status", Program.NoResponseFileConfig);
            Program.ShareConfiguration(original, resolved);

            Assert.Equal("invalid", Program.GetInvocation(resolved).AuthenticationModeName);
            var repeated = Assert.Throws<AuthenticationConfigurationException>(() => Program.Prepare(resolved));
            Assert.Contains("differ only by case", repeated.Message, StringComparison.Ordinal);
            Assert.Throws<UnreachableException>(() => Program.GetAuthentication(resolved));
        }
        finally
        {
            AutomationAuthenticationSettings.ReadEnvironment = originalReader;
        }
    }

    [Fact]
    public async Task Service_CapturesInputsAndInitializesConfigurationAndProviderOnlyOnce()
    {
        var variables = new Dictionary<string, string>
        {
            [AutomationAuthenticationSettings.ModeVariable] = "managedidentity",
            [AutomationAuthenticationSettings.ClientIdVariable] = ClientSecret.ClientId!,
        };
        var config = ScopesOnlyConfig(Scope);
        var providerCreations = 0;
        var provider = new FakeProvider(AutomationTestTokens.Result(AppJwt));
        var service = AuthenticationService.Create(
            config,
            AutomationAuthenticationSettings.Capture(variables),
            (settings, scopes) =>
            {
                providerCreations++;
                Assert.Equal(AuthenticationMode.ManagedIdentity, settings.Mode);
                Assert.Equal(ClientSecret.ClientId, settings.ClientId);
                Assert.Equal([Scope], scopes);
                return provider;
            });
        variables.Clear();

        Assert.Equal("managedidentity", service.SelectedModeName);
        Assert.True(service.IsAutomation);
        Assert.Equal(0, providerCreations);
        config["AzureAd:Scopes"] = "invalid-after-initialization";

        await service.AcquireTokenSilentAsync(CancellationToken.None);
        await service.AcquireTokenSilentAsync(CancellationToken.None);

        Assert.Equal(1, providerCreations);
        Assert.Equal(2, provider.Calls);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("azurecli")]
    public void Create_RejectsMissingScopesInEveryMode(string mode)
    {
        var error = Assert.Throws<InvalidOperationException>(() => AuthenticationService.Create(
            new ConfigurationBuilder().Build(),
            AutomationAuthenticationSettings.Capture(new Dictionary<string, string>
            {
                [AutomationAuthenticationSettings.ModeVariable] = mode,
            })));

        Assert.Equal("AzureAd:Scopes is not configured.", error.Message);
    }

    [Fact]
    public async Task Service_ProviderInitializationFailureIsCached()
    {
        var creations = 0;
        var error = new AuthenticationConfigurationException("Credential cannot be read.");
        var service = new AuthenticationService(ScopesOnlyConfig(Scope), null, ClientSecret, (_, _) =>
        {
            creations++;
            throw error;
        });

        Assert.Same(error, await Assert.ThrowsAsync<AuthenticationConfigurationException>(() =>
            service.AcquireTokenSilentAsync(CancellationToken.None)));
        Assert.Same(error, await Assert.ThrowsAsync<AuthenticationConfigurationException>(() =>
            service.AcquireTokenSilentAsync(CancellationToken.None)));

        Assert.Equal(1, creations);
        Assert.Equal("clientsecret", service.SelectedModeName);
    }

    [Fact]
    public void Service_InvalidInputsCannotCreateAService()
    {
        var environment = AutomationAuthenticationSettings.Capture(ConflictingAuthenticationEnvironment("CLIENTID"));

        Assert.Equal("invalid", environment.SelectedModeName);
        Assert.Throws<AuthenticationConfigurationException>(() => AuthenticationService.Create(
            ScopesOnlyConfig(Scope),
            environment,
            (_, _) => throw new InvalidOperationException("Must not create a provider.")));
    }

    [Theory]
    [InlineData("--help", "MODE", false)]
    [InlineData("--help", "CLIENTID", true)]
    [InlineData("help auth", "CLIENTSECRET__FILE", false)]
    [InlineData("auth", "MODE", true)]
    [InlineData("", "CLIENTID", false)]
    [InlineData("info", "MODE", false)]
    [InlineData("info", "CLIENTSECRET__FILE", true)]
    [InlineData("context key list", "CLIENTID", false)]
    [InlineData("auth status --output json", "MODE", false)]
    [InlineData("auth status --output json-compact", "MODE", true)]
    [InlineData("auth status --output json", "CLIENTID", true)]
    [InlineData("auth status --output json-compact", "CLIENTSECRET__FILE", false)]
    [InlineData("auth login", "MODE", true)]
    [InlineData("auth logout", "CLIENTID", true)]
    [InlineData("seller list", "CLIENTID", true)]
    [InlineData("game list --seller-id 1234567", "CLIENTID", true)]
    [InlineData("playfab title-data get --playfab-title-id ABC123", "CLIENTID", true)]
    public async Task Main_ConflictingAuthenticationVariables_PreservesCommandResults(
        string command, string setting, bool disableTelemetry)
    {
        var originalReader = AutomationAuthenticationSettings.ReadEnvironment;
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var reads = 0;
        AutomationAuthenticationSettings.ReadEnvironment = () =>
            ++reads == 1 ? ConflictingAuthenticationEnvironment(setting) : new Dictionary<string, string>();
        KeyValuePair<string, object?>[]? commandTags = null;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == TelemetryInstrumentation.SourceName
                    && instrument.Name == "xboxdevcli.command.invocations")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var captured = tags.ToArray();
            if (captured.Any(tag => tag.Key == "cli.auth_mode" && Equals(tag.Value, "invalid")))
            {
                commandTags = captured;
            }
        });
        listener.Start();
        Console.SetOut(stdout);
        Console.SetError(stderr);

        try
        {
            string[] args = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (disableTelemetry)
            {
                args = [.. args, "--disable-telemetry"];
            }

            var exitCode = await Program.Main(args);
            var requiresAuthentication = command.StartsWith("auth ", StringComparison.Ordinal)
                || command.StartsWith("seller ", StringComparison.Ordinal)
                || command.StartsWith("game ", StringComparison.Ordinal)
                || command.StartsWith("playfab title-data ", StringComparison.Ordinal);

            Assert.Equal(1, reads);
            if (requiresAuthentication)
            {
                Assert.Equal(1, exitCode);
                Assert.Equal("", stdout.ToString());
                Assert.Contains("differ only by case", stderr.ToString(), StringComparison.Ordinal);
                Assert.Contains(AutomationAuthenticationSettings.Prefix + setting, stderr.ToString(), StringComparison.Ordinal);
                Assert.DoesNotContain("first-private-value", stderr.ToString(), StringComparison.Ordinal);
                Assert.DoesNotContain("second-private-value", stderr.ToString(), StringComparison.Ordinal);
                if (command.StartsWith("auth status", StringComparison.Ordinal))
                {
                    using var json = JsonDocument.Parse(stderr.ToString());
                    Assert.False(json.RootElement.GetProperty("signedIn").GetBoolean());
                    Assert.Contains("Remove the duplicate variables.",
                        json.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
                }
            }
            else
            {
                Assert.Equal(0, exitCode);
                Assert.NotEmpty(stdout.ToString());
                Assert.Equal("", stderr.ToString());
                if (command is "" or "--help" or "help auth" or "auth")
                {
                    Assert.Contains("USAGE", stdout.ToString(), StringComparison.Ordinal);
                }
            }

            if (disableTelemetry)
            {
                Assert.Null(commandTags);
            }
            else
            {
                Assert.NotNull(commandTags);
                Assert.Contains(commandTags, tag => tag.Key == "cli.auth_mode" && Equals(tag.Value, "invalid"));
                Assert.Contains(commandTags, tag => tag.Key == "cli.outcome"
                    && Equals(tag.Value, requiresAuthentication ? "failure" : "success"));
            }
        }
        finally
        {
            AutomationAuthenticationSettings.ReadEnvironment = originalReader;
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    /// <summary>
    /// A configuration that can't be read, such as built-in settings missing from the build, fails
    /// status in every mode rather than reporting a signed-out user, and the action never runs.
    /// </summary>
    [Theory]
    [InlineData("azurecli", "json")]
    [InlineData("azurecli", "json-compact")]
    [InlineData(null, "json")]
    [InlineData(null, "json-compact")]
    public async Task Status_ConfigurationFailure_FailsInEveryMode(string? mode, string format)
    {
        using var automation = mode is null ? null : TestEnvironment.UseAuthenticationMode(mode);
        using var unreadable = TestSettings.UseUnreadable();
        var actionRan = false;

        var (exitCode, stdout, stderr) = await RunAsync(
            StatusCommandFactory.Create(parseResult =>
            {
                actionRan = true;
                return Program.GetAuthentication(parseResult);
            }),
            $"status --output {format}");

        Assert.Equal(1, exitCode);
        Assert.False(actionRan);
        Assert.Empty(stdout);
        using var json = JsonDocument.Parse(stderr);
        Assert.False(json.RootElement.GetProperty("signedIn").GetBoolean());
        Assert.Contains(TestSettings.UnreadableMessage, json.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(mode is not null, json.RootElement.TryGetProperty("authenticationMode", out var reported));
        if (mode is not null)
        {
            Assert.Equal(mode, reported.GetString());
        }
    }

    [Theory]
    [InlineData("status --output json-compact")]
    [InlineData("status")]
    public async Task Status_Cancellation_ReachesProviderAndIsNotReportedAsSignedOut(string args)
    {
        using var cancellation = new CancellationTokenSource();
        var provider = new CancelingProvider(cancellation);
        var service = Automation(provider);

        var (exitCode, stdout, _) = await RunAsync(StatusCommandFactory.Create(_ => service), args, cancellation.Token);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(stdout);
        Assert.True(provider.ReceivedToken.CanBeCanceled);
        Assert.True(provider.ReceivedToken.IsCancellationRequested);
    }

    private static AuthenticationService Automation(
        IAppTokenProvider provider, AuthenticationMode mode = AuthenticationMode.ClientSecret) =>
        new(ScopesOnlyConfig(Scope), null, ClientSecret with { Mode = mode }, (_, _) => provider);

    private static System.Collections.IDictionary ConflictingAuthenticationEnvironment(string setting) =>
        new System.Collections.Specialized.OrderedDictionary(StringComparer.Ordinal)
        {
            [AutomationAuthenticationSettings.Prefix + setting] = "first-private-value",
            [(AutomationAuthenticationSettings.Prefix + setting).ToLowerInvariant()] = "second-private-value",
        };

    private static IConfiguration ScopesOnlyConfig(string scopes) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["AzureAd:Scopes"] = scopes })
        .Build();

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        Command command, string args, CancellationToken cancellationToken = default)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var outWriter = new StringWriter();
        using var errorWriter = new StringWriter();
        Console.SetOut(outWriter);
        Console.SetError(errorWriter);

        try
        {
            var rootCommand = new RootCommand("XBOX Dev CLI");
            rootCommand.Options.Add(Program.ClientIdOption);
            rootCommand.Options.Add(Program.TenantIdOption);
            rootCommand.Options.Add(Program.ScopesOption);
            rootCommand.Options.Add(Program.PlayFabTitleIdOption);
            rootCommand.Options.Add(Program.OutputOption);
            rootCommand.Options.Add(Program.DataOnlyOption);
            rootCommand.Options.Add(Program.VerboseOption);
            rootCommand.Options.Add(Program.ConfirmOption);
            rootCommand.Subcommands.Add(command);
            CommandErrorBoundary.Install(rootCommand);
            var exitCode = await rootCommand.Parse(args, Program.NoResponseFileConfig)
                .InvokeAsync(cancellationToken: cancellationToken);
            return (exitCode, outWriter.ToString(), errorWriter.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    /// <summary>Simulates Ctrl-C arriving while a token is being acquired.</summary>
    private sealed class CancelingProvider(CancellationTokenSource cancellation) : IAppTokenProvider
    {
        public CancellationToken ReceivedToken { get; private set; }

        public Task<AuthenticationResult> AcquireTokenAsync(CancellationToken cancellationToken = default)
        {
            ReceivedToken = cancellationToken;
            cancellation.Cancel();
            return Task.FromCanceled<AuthenticationResult>(cancellationToken);
        }
    }

    private sealed class FakeProvider : IAppTokenProvider
    {
        private readonly AuthenticationResult[] _results = [];
        private readonly Exception? _error;

        public FakeProvider(params AuthenticationResult[] results) => _results = results;

        public FakeProvider(Exception error) => _error = error;

        public int Calls { get; private set; }

        public Task<AuthenticationResult> AcquireTokenAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return _error is null
                ? Task.FromResult(_results[Math.Min(Calls - 1, _results.Length - 1)])
                : Task.FromException<AuthenticationResult>(_error);
        }
    }
}
