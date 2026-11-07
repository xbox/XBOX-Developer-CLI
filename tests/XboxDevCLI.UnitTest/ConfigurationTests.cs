// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using Microsoft.Extensions.Configuration;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Safety;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public class ConfigurationTests
{
    /// <summary>
    /// Reads the built-in value for a key so tests don't break when settings change.
    /// </summary>
    private static string? GetBuiltInValue(string key) => TestSettings.BuiltIn(key);

    private static RootCommand CreateRootCommandWithGlobalOptions()
    {
        var rootCommand = new RootCommand();
        rootCommand.Options.Add(Program.ClientIdOption);
        rootCommand.Options.Add(Program.TenantIdOption);
        rootCommand.Options.Add(Program.ScopesOption);
        return rootCommand;
    }

    [Fact]
    public void ShippedBaseUrlTemplateMatchesCompiledInSecurityBaseline()
    {
        Assert.Equal(
            EndpointOverrideWarning.DefaultBaseUrlTemplate,
            GetBuiltInValue(EndpointOverrideWarning.ConfigurationKey));
    }

    [Fact]
    public void ShippedXboxPartnerBaseUrlMatchesCompiledInSecurityBaseline()
    {
        Assert.Equal(
            EndpointOverrideWarning.DefaultXboxPartnerBaseUrl,
            GetBuiltInValue(EndpointOverrideWarning.XboxPartnerConfigurationKey));
    }

    [Fact]
    public void LoadConfiguration_WithCliOverrides_OverridesBuiltInSettings()
    {
        var rootCommand = CreateRootCommandWithGlobalOptions();
        var dummyCommand = new Command("test");
        rootCommand.Subcommands.Add(dummyCommand);

        IConfiguration? capturedConfig = null;

        dummyCommand.SetAction((ParseResult parseResult) =>
        {
            capturedConfig = Program.LoadConfiguration(parseResult);
            return 0;
        });

        // Act — invoke with CLI overrides
        rootCommand.Parse("--client-id override-id test").Invoke();

        // Assert
        Assert.NotNull(capturedConfig);
        Assert.Equal("override-id", capturedConfig!["AzureAd:ClientId"]);
        // Values not overridden should still come from the built-in settings
        Assert.NotNull(capturedConfig["AzureAd:TenantId"]);
    }

    [Fact]
    public void BuiltInSettings_ShipAUsableXboxPartnerGatewayUrl()
    {
        var baseUrl = GetBuiltInValue("XboxPartner:BaseUrl");

        // There is no code fallback, so this key is the CLI's only source of the gateway URL.
        // If it is missing or unusable, every seller command fails for every user of a release
        // build. Assert it here rather than discovering it after shipping.
        Assert.False(
            string.IsNullOrWhiteSpace(baseUrl),
            "The built-in settings must include XboxPartner:BaseUrl; there is no compiled-in default.");

        var requestUri = XboxPartnerClient.BuildRequestUri(baseUrl!, "xboxpartner/v1/sellers");

        Assert.Equal(Uri.UriSchemeHttps, requestUri.Scheme);
        Assert.EndsWith("/xboxpartner/v1/sellers", requestUri.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadConfiguration_WithoutOverrides_UsesBuiltInDefaults()
    {
        var rootCommand = CreateRootCommandWithGlobalOptions();
        var dummyCommand = new Command("test");
        rootCommand.Subcommands.Add(dummyCommand);

        IConfiguration? capturedConfig = null;

        dummyCommand.SetAction((ParseResult parseResult) =>
        {
            capturedConfig = Program.LoadConfiguration(parseResult);
            return 0;
        });

        rootCommand.Parse("test").Invoke();

        Assert.NotNull(capturedConfig);
        // Should load the built-in values
        var expectedClientId = GetBuiltInValue("AzureAd:ClientId");
        Assert.Equal(expectedClientId, capturedConfig!["AzureAd:ClientId"]);
    }

    [Fact]
    public void LoadConfiguration_PartialOverrides_MergesCorrectly()
    {
        var rootCommand = CreateRootCommandWithGlobalOptions();
        var dummyCommand = new Command("test");
        rootCommand.Subcommands.Add(dummyCommand);

        IConfiguration? capturedConfig = null;

        dummyCommand.SetAction((ParseResult parseResult) =>
        {
            capturedConfig = Program.LoadConfiguration(parseResult);
            return 0;
        });

        // Override only tenant-id, leave everything else as defaults
        rootCommand.Parse("--tenant-id custom-tenant test").Invoke();

        Assert.NotNull(capturedConfig);
        Assert.Equal("custom-tenant", capturedConfig!["AzureAd:TenantId"]);
        // ClientId should remain the built-in default
        var expectedClientId = GetBuiltInValue("AzureAd:ClientId");
        Assert.Equal(expectedClientId, capturedConfig["AzureAd:ClientId"]);
    }

    [Theory]
    [InlineData(EndpointOverrideWarning.DefaultBaseUrlTemplate)]
    [InlineData("https://original.example.com")]
    public void LoadConfiguration_EndpointChangesDoNotReplaceTheCheckedSnapshot(string initialEndpoint)
    {
        const string variable = "XBOX_PLAYFAB__BASEURLTEMPLATE";
        var originalValue = Environment.GetEnvironmentVariable(variable);
        var originalError = Console.Error;
        using var stderr = new StringWriter();

        try
        {
            Console.SetError(stderr);
            Environment.SetEnvironmentVariable(variable, initialEndpoint);
            var root = Program.BuildRootCommand();
            var parseResult = root.Parse(
                "--playfab-title-id TEST123 auth status", Program.NoResponseFileConfig);
            var startupConfig = Program.LoadConfiguration(parseResult);
            var initialWarning = EndpointOverrideWarning.GetWarning(initialEndpoint);
            Assert.Equal(initialWarning, PlayFabWarning(parseResult));

            Environment.SetEnvironmentVariable(variable, "https://changed.example.com");
            var clientConfig = Program.LoadConfiguration(parseResult);
            Assert.Same(startupConfig, clientConfig);
            using var httpClient = new HttpClient();
            using var client = new PlayFabApiClient(
                clientConfig, new AuthenticationService(clientConfig), httpClient);
            Assert.Equal(new Uri(initialEndpoint + "/"), httpClient.BaseAddress);
            Assert.Equal(initialWarning, PlayFabWarning(parseResult));

            var nextParseResult = root.Parse("--playfab-title-id TEST123 auth status", Program.NoResponseFileConfig);
            var nextConfig = Program.LoadConfiguration(nextParseResult);
            Assert.NotSame(startupConfig, nextConfig);
            Assert.Equal("https://changed.example.com", nextConfig[EndpointOverrideWarning.ConfigurationKey]);
            Assert.Equal(EndpointOverrideWarning.GetWarning("https://changed.example.com"), PlayFabWarning(nextParseResult));

            // Loading only records the warnings; preparing a command that calls the endpoint writes them.
            Assert.Empty(stderr.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, originalValue);
            Console.SetError(originalError);
        }
    }

    [Fact]
    public void LoadConfiguration_FailedSnapshotPreservesTheErrorForCommandHandlers()
    {
        using var unreadable = TestSettings.UseUnreadable();
        var parseResult = Program.BuildRootCommand().Parse("auth status", Program.NoResponseFileConfig);

        var startupError = Assert.Throws<InvalidDataException>(() => Program.LoadConfiguration(parseResult));
        var commandError = Assert.Throws<InvalidDataException>(() => Program.LoadConfiguration(parseResult));

        Assert.Same(startupError, commandError);
    }

    /// <summary>
    /// Built-in settings that aren't valid JSON are a configuration error the error boundary
    /// reports, not a crash.
    /// </summary>
    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    public void LoadConfiguration_InvalidBuiltInJson_IsAnExpectedFailure(string json)
    {
        using var invalid = TestSettings.Use(json);
        var parseResult = Program.BuildRootCommand().Parse("auth status", Program.NoResponseFileConfig);

        var error = Assert.Throws<InvalidDataException>(() => Program.LoadConfiguration(parseResult));

        Assert.Equal(LoadedConfiguration.InvalidBuiltInSettingsMessage, error.Message);
        Assert.True(error.InnerException is System.Text.Json.JsonException or FormatException);
        Assert.True(CommandErrorBoundary.IsExpected(error));
    }

    /// <summary>
    /// The built-in settings are the source file, embedded, so what's tested and reviewed is what ships.
    /// </summary>
    [Fact]
    public void BuiltInSettings_AreTheEmbeddedSourceFile()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "XboxDevCLI", "Resources", "defaults.json"));
        using var reader = new StreamReader(BuiltInSettings.Open());

        Assert.Equal(source.ReplaceLineEndings(), reader.ReadToEnd().ReplaceLineEndings());
        Assert.Single(
            typeof(Program).Assembly.GetManifestResourceNames(),
            name => name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The built-in settings hold no telemetry connection string; the build embeds it, and a
    /// blank one would only hide that.
    /// </summary>
    [Fact]
    public void BuiltInSettings_HoldNoTelemetryConnectionString() =>
        Assert.Null(GetBuiltInValue("Telemetry:ConnectionString"));

    /// <summary>
    /// A settings file beside the executable isn't read, so an endpoint in it can't redirect
    /// requests, and a command that reads settings notes once that it's ignored.
    /// </summary>
    [Fact]
    public void SettingsFileBesideTheExecutable_IsIgnoredWithANote()
    {
        var directory = Directory.CreateTempSubdirectory("xbox-install-");
        try
        {
            var file = Path.Combine(directory.FullName, "appsettings.json");
            File.WriteAllText(file, """{ "PlayFab": { "BaseUrlTemplate": "https://file.nodpublishers.com" } }""");
            var parseResult = Program.BuildRootCommand().Parse("auth status", Program.NoResponseFileConfig);
            var invocation = new CommandInvocation(
                parseResult,
                () => new ConfigurationBuilder().AddJsonStream(BuiltInSettings.Open()).Build(),
                directory.FullName);

            var stderr = CaptureStderr(() =>
            {
                invocation.Prepare(CommandRequirement.Configuration, ServiceEndpoints.PlayFab);
                invocation.ForResolved(parseResult).Prepare(CommandRequirement.Configuration, ServiceEndpoints.PlayFab);
            });

            Assert.Equal(
                EndpointOverrideWarning.DefaultBaseUrlTemplate,
                invocation.Configuration[EndpointOverrideWarning.ConfigurationKey]);
            Assert.Equal(
                $"Note: '{file}' is no longer read. Settings come from the built-in defaults, XBOX_ environment "
                + $"variables, and command options. Delete the file to hide this note.{Environment.NewLine}",
                stderr);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("appsettings.Development.json", true)]
    [InlineData("appsettings.json", true)]
    [InlineData("settings.json", false)]
    public void RetiredSettingsNotice_NamesOnlyFilesEarlierReleasesRead(string fileName, bool noted)
    {
        var directory = Directory.CreateTempSubdirectory("xbox-install-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, fileName), "{}");

            Assert.Equal(noted, LoadedConfiguration.GetRetiredSettingsNotice(directory.FullName) is not null);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void RetiredSettingsNotice_IsNullForADirectoryThatCantBeRead() =>
        Assert.Null(LoadedConfiguration.GetRetiredSettingsNotice(
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}")));

    /// <summary>
    /// The file's name is written to the terminal, so characters that could reorder or control
    /// the terminal are removed from it. A bidi override is used because every OS allows it in a
    /// file name.
    /// </summary>
    [Fact]
    public void RetiredSettingsNotice_RemovesUnsafeCharactersFromTheFileName()
    {
        var directory = Directory.CreateTempSubdirectory("xbox-install-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "appsettings.\u202Egnp.json"), "{}");

            var notice = LoadedConfiguration.GetRetiredSettingsNotice(directory.FullName);

            Assert.NotNull(notice);
            Assert.DoesNotContain('\u202E', notice);
            Assert.Contains(Path.Combine(directory.FullName, "appsettings.gnp.json"), notice, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// With JSON output, a command such as <c>auth status</c> writes its errors to stderr as
    /// JSON, so the note isn't written before them.
    /// </summary>
    [Theory]
    [InlineData("json")]
    [InlineData("json-compact")]
    public void SettingsFileBesideTheExecutable_IsNotNotedWithJsonOutput(string format)
    {
        var directory = Directory.CreateTempSubdirectory("xbox-install-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "appsettings.json"), "{}");
            var parseResult = Program.BuildRootCommand().Parse(
                ["auth", "status", "--output", format], Program.NoResponseFileConfig);
            var invocation = new CommandInvocation(
                parseResult,
                () => new ConfigurationBuilder().AddJsonStream(BuiltInSettings.Open()).Build(),
                directory.FullName);

            var stderr = CaptureStderr(() => invocation.Prepare(CommandRequirement.Configuration, ServiceEndpoints.None));

            Assert.Empty(stderr);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>An option beats its variable, which beats the built-in value.</summary>
    [Fact]
    public void LoadConfiguration_OptionsBeatVariablesWhichBeatBuiltInSettings()
    {
        var original = Environment.GetEnvironmentVariable("XBOX_AZUREAD__TENANTID");
        var originalClient = Environment.GetEnvironmentVariable("XBOX_AZUREAD__CLIENTID");
        Environment.SetEnvironmentVariable("XBOX_AZUREAD__TENANTID", "variable-tenant");
        Environment.SetEnvironmentVariable("XBOX_AZUREAD__CLIENTID", "variable-client");
        try
        {
            var configuration = Program.LoadConfiguration(Program.BuildRootCommand().Parse(
                "--tenant-id option-tenant auth status", Program.NoResponseFileConfig));

            Assert.Equal("option-tenant", configuration["AzureAd:TenantId"]);
            Assert.Equal("variable-client", configuration["AzureAd:ClientId"]);
            Assert.Equal(GetBuiltInValue("AzureAd:Scopes"), configuration["AzureAd:Scopes"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XBOX_AZUREAD__TENANTID", original);
            Environment.SetEnvironmentVariable("XBOX_AZUREAD__CLIENTID", originalClient);
        }
    }

    /// <summary>
    /// <c>--config-env</c> selected a settings file earlier releases read. It's hidden, and using
    /// it fails with what replaced it rather than being silently ignored.
    /// </summary>
    [Fact]
    public void ConfigEnvOption_IsHiddenAndRefused()
    {
        var parseResult = Program.BuildRootCommand().Parse("--config-env Development auth status", Program.NoResponseFileConfig);

        Assert.True(Program.ConfigEnvOption.Hidden);
        Assert.Equal(Program.ConfigEnvRetiredMessage, Assert.Single(parseResult.Errors).Message);
    }

    private static string? PlayFabWarning(ParseResult parseResult) =>
        Program.GetInvocation(parseResult).LoadedConfiguration.Warnings.GetValueOrDefault(ServiceEndpoints.PlayFab);

    private static string CaptureStderr(Action run)
    {
        var original = Console.Error;
        using var stderr = new StringWriter();
        Console.SetError(stderr);
        try
        {
            run();
        }
        finally
        {
            Console.SetError(original);
        }

        return stderr.ToString();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "XboxDevCLI.sln")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "Could not locate the repository root from the test directory.");
        return directory!.FullName;
    }
}
