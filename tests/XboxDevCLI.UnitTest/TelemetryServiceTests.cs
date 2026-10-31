// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Configuration;
using XboxDevCLI.Telemetry;
using Xunit;

namespace XboxDevCLI.UnitTest;

public class TelemetryServiceTests
{
    [Fact]
    public void Create_DisabledConfigurationCreatesNoOpService()
    {
        var configuration = CreateConfiguration(("Telemetry:Enabled", "false"));

        using var service = TelemetryService.Create(configuration);

        Assert.False(service.Options.Enabled);
        service.RecordCommand("status", 0, TimeSpan.FromMilliseconds(1), "user");
        service.RecordUnhandledFailure(new InvalidOperationException());
    }

    [Fact]
    public void Create_ValidConnectionStringBuildsOpenTelemetryProviders()
    {
        var configuration = CreateConfiguration(
            ("Telemetry:Enabled", "true"),
            ("Telemetry:ConnectionString",
                "InstrumentationKey=00000000-0000-0000-0000-000000000000;" +
                "IngestionEndpoint=http://127.0.0.1:1/"),
            ("Telemetry:DependencyTraceSamplingRatio", "0"));

        using var service = TelemetryService.Create(configuration);

        Assert.True(service.Options.Enabled);
    }

    [Fact]
    public void Create_InvalidConnectionStringFailsOpenWithoutBreakingCli()
    {
        var configuration = CreateConfiguration(
            ("Telemetry:Enabled", "true"),
            ("Telemetry:ConnectionString", "not-a-connection-string"));
        string? diagnostic = null;

        using var service = TelemetryService.Create(
            configuration,
            message => diagnostic = message);

        Assert.False(service.Options.Enabled);
        Assert.Contains("could not be initialized", diagnostic);
    }

    [Fact]
    public void Create_InitializationFailureFailsOpen_WithNullDiagnostic()
    {
        var configuration = CreateConfiguration(
            ("Telemetry:Enabled", "true"),
            ("Telemetry:ConnectionString", "not-a-connection-string"));

        // A null diagnostic callback must not turn a best-effort telemetry
        // initialization failure into an exception that escapes Create.
        using var service = TelemetryService.Create(configuration, diagnostic: null);

        Assert.False(service.Options.Enabled);
        service.RecordCommand("status", 0, TimeSpan.FromMilliseconds(1), "user");
    }

    [Fact]
    public async Task Main_DisableTelemetryOptionCompletesNormally()
    {
        var exitCode = await Program.Main(["--disable-telemetry", "feedback", "--no-browser"]);

        Assert.Equal(0, exitCode);
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
