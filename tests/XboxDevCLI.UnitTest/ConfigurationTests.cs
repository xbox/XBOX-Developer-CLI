// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using Microsoft.Extensions.Configuration;
using XboxDevCLI.Safety;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public class ConfigurationTests
{
    /// <summary>
    /// Reads the current appsettings.json value for a key so tests don't break when settings change.
    /// </summary>
    private static string? GetAppsettingsValue(string key)
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();
        return config[key];
    }

    private static RootCommand CreateRootCommandWithGlobalOptions()
    {
        var rootCommand = new RootCommand();
        rootCommand.Options.Add(Program.ConfigEnvOption);
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
            GetAppsettingsValue(EndpointOverrideWarning.ConfigurationKey));
    }

    [Fact]
    public void ShippedXboxPartnerBaseUrlMatchesCompiledInSecurityBaseline()
    {
        Assert.Equal(
            EndpointOverrideWarning.DefaultXboxPartnerBaseUrl,
            GetAppsettingsValue(EndpointOverrideWarning.XboxPartnerConfigurationKey));
    }

    [Fact]
    public void BuildConfiguration_WithCliOverrides_OverridesAppsettings()
    {
        var rootCommand = CreateRootCommandWithGlobalOptions();
        var dummyCommand = new Command("test");
        rootCommand.Subcommands.Add(dummyCommand);

        IConfiguration? capturedConfig = null;

        dummyCommand.SetAction((ParseResult parseResult) =>
        {
            capturedConfig = Program.BuildConfiguration(parseResult);
            return 0;
        });

        // Act — invoke with CLI overrides
        rootCommand.Parse("--client-id override-id test").Invoke();

        // Assert
        Assert.NotNull(capturedConfig);
        Assert.Equal("override-id", capturedConfig!["AzureAd:ClientId"]);
        // Values not overridden should still come from appsettings.json
        Assert.NotNull(capturedConfig["AzureAd:TenantId"]);
    }

    [Fact]
    public void Appsettings_ShipsAUsableXboxPartnerGatewayUrl()
    {
        var baseUrl = GetAppsettingsValue("XboxPartner:BaseUrl");

        // There is no code fallback, so this key is the CLI's only source of the gateway URL.
        // If it is missing or unusable, every seller command fails for every user of a release
        // build. Assert it here rather than discovering it after shipping.
        Assert.False(
            string.IsNullOrWhiteSpace(baseUrl),
            "appsettings.json must ship XboxPartner:BaseUrl; there is no compiled-in default.");

        var requestUri = XboxPartnerClient.BuildRequestUri(baseUrl!, "xboxpartner/v1/sellers");

        Assert.Equal(Uri.UriSchemeHttps, requestUri.Scheme);
        Assert.EndsWith("/xboxpartner/v1/sellers", requestUri.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildConfiguration_WithoutOverrides_UsesAppsettingsDefaults()
    {
        var rootCommand = CreateRootCommandWithGlobalOptions();
        var dummyCommand = new Command("test");
        rootCommand.Subcommands.Add(dummyCommand);

        IConfiguration? capturedConfig = null;

        dummyCommand.SetAction((ParseResult parseResult) =>
        {
            capturedConfig = Program.BuildConfiguration(parseResult);
            return 0;
        });

        rootCommand.Parse("test").Invoke();

        Assert.NotNull(capturedConfig);
        // Should load base appsettings.json values
        var expectedClientId = GetAppsettingsValue("AzureAd:ClientId");
        Assert.Equal(expectedClientId, capturedConfig!["AzureAd:ClientId"]);
    }

    [Fact]
    public void BuildConfiguration_PartialOverrides_MergesCorrectly()
    {
        var rootCommand = CreateRootCommandWithGlobalOptions();
        var dummyCommand = new Command("test");
        rootCommand.Subcommands.Add(dummyCommand);

        IConfiguration? capturedConfig = null;

        dummyCommand.SetAction((ParseResult parseResult) =>
        {
            capturedConfig = Program.BuildConfiguration(parseResult);
            return 0;
        });

        // Override only tenant-id, leave everything else as defaults
        rootCommand.Parse("--tenant-id custom-tenant test").Invoke();

        Assert.NotNull(capturedConfig);
        Assert.Equal("custom-tenant", capturedConfig!["AzureAd:TenantId"]);
        // ClientId should remain the appsettings.json default
        var expectedClientId = GetAppsettingsValue("AzureAd:ClientId");
        Assert.Equal(expectedClientId, capturedConfig["AzureAd:ClientId"]);
    }

    [Theory]
    [InlineData(EndpointOverrideWarning.DefaultBaseUrlTemplate)]
    [InlineData("https://original.example.com")]
    public void BuildConfiguration_EndpointChangesDoNotReplaceTheCheckedSnapshot(string initialEndpoint)
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
            var startupConfig = Program.BuildConfiguration(parseResult);
            var initialWarning = EndpointOverrideWarning.GetWarning(initialEndpoint);
            Assert.Equal(initialWarning is null ? "" : initialWarning + Environment.NewLine, stderr.ToString());

            Environment.SetEnvironmentVariable(variable, "https://changed.example.com");
            var clientConfig = Program.BuildConfiguration(parseResult);
            Assert.Same(startupConfig, clientConfig);
            using var httpClient = new HttpClient();
            using var client = new PlayFabApiClient(
                clientConfig, new AuthenticationService(clientConfig), httpClient);
            Assert.Equal(new Uri(initialEndpoint + "/"), httpClient.BaseAddress);
            Assert.Equal(initialWarning is null ? "" : initialWarning + Environment.NewLine, stderr.ToString());

            stderr.GetStringBuilder().Clear();
            var nextConfig = Program.BuildConfiguration(root.Parse(
                "--playfab-title-id TEST123 auth status", Program.NoResponseFileConfig));
            Assert.NotSame(startupConfig, nextConfig);
            Assert.Equal("https://changed.example.com", nextConfig[EndpointOverrideWarning.ConfigurationKey]);
            Assert.Equal(
                EndpointOverrideWarning.GetWarning("https://changed.example.com") + Environment.NewLine,
                stderr.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, originalValue);
            Console.SetError(originalError);
        }
    }

    [Fact]
    public void BuildConfiguration_FailedSnapshotPreservesTheErrorForCommandHandlers()
    {
        var parseResult = Program.BuildRootCommand().Parse(
            $"--config-env missing-{Guid.NewGuid():N} auth status", Program.NoResponseFileConfig);

        var startupError = Assert.Throws<FileNotFoundException>(() => Program.BuildConfiguration(parseResult));
        var commandError = Assert.Throws<FileNotFoundException>(() => Program.BuildConfiguration(parseResult));

        Assert.Same(startupError, commandError);
    }
}
