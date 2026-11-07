// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Configuration;
using XboxDevCLI.Telemetry;
using Xunit;

namespace XboxDevCLI.UnitTest;

public class TelemetryOptionsTests
{
    [Fact]
    public void FromConfiguration_DefaultsToEnabledWithoutExporter()
    {
        var configuration = CreateConfiguration();

        var options = TelemetryOptions.FromConfiguration(configuration, _ => null);

        Assert.True(options.Enabled);
        Assert.Null(options.ConnectionString);
        Assert.True(options.DependencyTracingEnabled);
        Assert.Equal(0.1, options.DependencyTraceSamplingRatio);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("YES")]
    public void FromConfiguration_OptOutEnvironmentVariableWins(string optOutValue)
    {
        var configuration = CreateConfiguration(
            ("Telemetry:Enabled", "true"),
            ("Telemetry:ConnectionString", "InstrumentationKey=00000000-0000-0000-0000-000000000000"));

        var options = TelemetryOptions.FromConfiguration(
            configuration,
            name => name == TelemetryOptions.OptOutEnvironmentVariable ? optOutValue : null);

        Assert.False(options.Enabled);
    }

    /// <summary>
    /// A host's own Application Insights connection string isn't the CLI's, so the CLI's
    /// telemetry never goes to it; the built-in one is used instead.
    /// </summary>
    [Fact]
    public void FromConfiguration_IgnoresTheHostsApplicationInsightsConnectionString()
    {
        const string embedded = "InstrumentationKey=00000000-0000-0000-0000-000000000000";
        var configuration = CreateConfiguration();

        var options = TelemetryOptions.FromConfiguration(
            configuration,
            name => name == "APPLICATIONINSIGHTS_CONNECTION_STRING" ? "InstrumentationKey=host" : null,
            embeddedConnectionString: embedded);

        Assert.Equal(embedded, options.ConnectionString);
    }

    [Fact]
    public void FromConfiguration_UsesEmbeddedReleaseConnectionStringAsFallback()
    {
        const string connectionString =
            "InstrumentationKey=00000000-0000-0000-0000-000000000000";
        var configuration = CreateConfiguration();

        var options = TelemetryOptions.FromConfiguration(
            configuration,
            _ => null,
            embeddedConnectionString: connectionString);

        Assert.Equal(connectionString, options.ConnectionString);
    }

    [Fact]
    public void FromConfiguration_EmbeddedDestinationWinsOverConfiguration()
    {
        const string embedded = "InstrumentationKey=00000000-0000-0000-0000-000000000000";
        var configuration = CreateConfiguration(
            ("Telemetry:ConnectionString",
                "InstrumentationKey=11111111-1111-1111-1111-111111111111"));

        var options = TelemetryOptions.FromConfiguration(
            configuration,
            _ => null,
            embeddedConnectionString: embedded);

        Assert.Equal(embedded, options.ConnectionString);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void FromConfiguration_UsesConfiguredDestinationWhenNoneIsEmbedded(string embedded)
    {
        const string configuredConnectionString =
            "InstrumentationKey=11111111-1111-1111-1111-111111111111";
        var configuration = CreateConfiguration(
            ("Telemetry:ConnectionString", configuredConnectionString));

        var options = TelemetryOptions.FromConfiguration(
            configuration,
            _ => null,
            embeddedConnectionString: embedded);

        Assert.Equal(configuredConnectionString, options.ConnectionString);
    }

    [Theory]
    [InlineData("-1", 0)]
    [InlineData("0.25", 0.25)]
    [InlineData("2", 1)]
    public void FromConfiguration_ClampsDependencySamplingRatio(
        string configuredValue,
        double expectedValue)
    {
        var configuration = CreateConfiguration(
            ("Telemetry:DependencyTraceSamplingRatio", configuredValue));

        var options = TelemetryOptions.FromConfiguration(configuration, _ => null);

        Assert.Equal(expectedValue, options.DependencyTraceSamplingRatio);
    }

    private static IConfiguration CreateConfiguration(
        params (string Key, string Value)[] values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(
                value => value.Key,
                value => (string?)value.Value))
            .Build();
    }
}
