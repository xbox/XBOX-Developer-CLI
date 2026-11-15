// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Configuration;
using OpenTelemetry;
using OpenTelemetry.Logs;
using XboxDevCLI.Telemetry;
using Xunit;

namespace XboxDevCLI.UnitTest;

public sealed class TelemetryServiceTests : IDisposable
{
    private readonly string _stateDirectory =
        Path.Combine(Path.GetTempPath(), $"xbox-telemetry-service-{Guid.NewGuid():N}");

    private const string ConnectionString =
        "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=http://127.0.0.1:1/";

    private static readonly CommandInvocationEvent Invocation = new(
        "playfab-mps-build-list",
        "user",
        ExitCode: 0,
        ErrorType: null,
        HttpStatusCode: 200,
        TimeSpan.FromMilliseconds(42),
        ["verbose"],
        ["playfab-title-id"],
        Ci: false);

    [Fact]
    public void Emit_DisabledTelemetrySendsNothing()
    {
        var exporter = new CapturingProcessor();
        var service = TelemetryService.Create(
            CreateConfiguration(("Telemetry:Enabled", "false"), ("Telemetry:ConnectionString", ConnectionString)),
            configureExport: logging => logging.AddProcessor(exporter),
            getEnvironmentVariable: _ => null);

        service.Emit(Invocation);

        Assert.False(service.IsCollecting);
        Assert.Empty(exporter.Records);
    }

    [Fact]
    public void Emit_WithoutAConnectionStringSendsNothing()
    {
        var exporter = new CapturingProcessor();
        var service = TelemetryService.Create(
            CreateConfiguration(("Telemetry:Enabled", "true")),
            configureExport: logging => logging.AddProcessor(exporter),
            getEnvironmentVariable: _ => null);

        service.Emit(Invocation);

        Assert.True(service.Options.Enabled);
        Assert.False(service.IsCollecting);
        Assert.Empty(exporter.Records);
    }

    public void Dispose()
    {
        if (Directory.Exists(_stateDirectory))
        {
            Directory.Delete(_stateDirectory, recursive: true);
        }
    }

    [Fact]
    public void Emit_SendsNothingUntilTheDisclosureIsShown()
    {
        var exporter = new CapturingProcessor();
        var service = CreateCollecting(exporter);

        service.Emit(Invocation);
        Assert.Empty(exporter.Records);

        using var writer = new StringWriter();
        service.ShowDisclosure(_stateDirectory, writer, useAnsi: false);
        service.Emit(Invocation);

        Assert.Contains("TELEMETRY", writer.ToString(), StringComparison.Ordinal);
        Assert.Single(exporter.Records);
    }

    /// <summary>A notice that couldn't be written wasn't shown, so nothing is sent.</summary>
    [Fact]
    public void Emit_SendsNothingWhenTheDisclosureCannotBeWritten()
    {
        var exporter = new CapturingProcessor();
        var service = CreateCollecting(exporter);

        service.ShowDisclosure(_stateDirectory, new FailingWriter(), useAnsi: false);
        service.Emit(Invocation);

        Assert.Empty(exporter.Records);
        Assert.False(File.Exists(Path.Combine(_stateDirectory, TelemetryDisclosure.SentinelFileName)));
    }

    [Fact]
    public void Emit_SendsWhenAnEarlierRunShowedTheDisclosure()
    {
        Directory.CreateDirectory(_stateDirectory);
        File.WriteAllText(Path.Combine(_stateDirectory, TelemetryDisclosure.SentinelFileName), "shown");
        var exporter = new CapturingProcessor();
        var service = CreateCollecting(exporter);

        service.ShowDisclosure(_stateDirectory, new FailingWriter(), useAnsi: false);
        service.Emit(Invocation);

        Assert.Single(exporter.Records);
    }

    [Fact]
    public void Emit_SendsOneCustomEventWithTheTimestampFlooredToTheHour()
    {
        var exporter = new CapturingProcessor();
        var service = CreateCollecting(exporter);
        service.ShowDisclosure(_stateDirectory, TextWriter.Null, useAnsi: false);

        service.Emit(Invocation);

        var record = Assert.Single(exporter.Records);
        Assert.Equal(0, record.Timestamp.Ticks % TimeSpan.TicksPerHour);
        Assert.Equal(DateTimeKind.Utc, record.Timestamp.Kind);
        Assert.Equal(
            TelemetryInstrumentation.ToAttributes(Invocation).Select(attribute => $"{attribute.Key}={attribute.Value}"),
            record.Attributes);
        Assert.Equal(string.Empty, record.Body ?? string.Empty);
    }

    [Fact]
    public void Emit_SwallowsAnExportFailureAndReportsItToTheDiagnostic()
    {
        string? diagnostic = null;
        var service = TelemetryService.Create(
            CreateConfiguration(("Telemetry:Enabled", "true"), ("Telemetry:ConnectionString", ConnectionString)),
            message => diagnostic = message,
            configureExport: _ => throw new IOException("disk full"),
            getEnvironmentVariable: _ => null);
        service.ShowDisclosure(_stateDirectory, TextWriter.Null, useAnsi: false);

        service.Emit(Invocation);

        Assert.Equal("Telemetry could not be sent (io).", diagnostic);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("true", true)]
    public void IsCi_ReadsOnlyWhetherACiVariableIsSet(string? value, bool expected)
    {
        var service = TelemetryService.Create(
            CreateConfiguration(),
            getEnvironmentVariable: name => name == "TF_BUILD" ? value : null);

        Assert.Equal(expected, service.IsCi);
    }

    [Fact]
    public void FloorToHour_ConvertsLocalTimeToUtc()
    {
        var local = new DateTime(2025, 1, 2, 3, 45, 6, DateTimeKind.Local);

        var floored = TelemetryService.HourFloorProcessor.FloorToHour(local);

        var utc = local.ToUniversalTime();
        Assert.Equal(new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc), floored);
        Assert.Equal(DateTimeKind.Utc, floored.Kind);
    }

    [Fact]
    public async Task Main_DisableTelemetryOptionCompletesNormally()
    {
        var exitCode = await Program.Main(["--disable-telemetry", "feedback", "--no-browser"]);

        Assert.Equal(0, exitCode);
    }

    private static TelemetryService CreateCollecting(CapturingProcessor exporter) =>
        TelemetryService.Create(
            CreateConfiguration(("Telemetry:Enabled", "true"), ("Telemetry:ConnectionString", ConnectionString)),
            configureExport: logging => logging.AddProcessor(exporter),
            getEnvironmentVariable: _ => null);

    private sealed class FailingWriter : StringWriter
    {
        public override void WriteLine(string? value) => throw new IOException("stderr is closed");
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

    private sealed record CapturedRecord(DateTime Timestamp, string[] Attributes, string? Body);

    private sealed class CapturingProcessor : BaseProcessor<LogRecord>
    {
        public List<CapturedRecord> Records { get; } = [];

        public override void OnEnd(LogRecord data)
        {
            // Log records are pooled, so copy what the test checks before the record is reused.
            Records.Add(new CapturedRecord(
                data.Timestamp,
                data.Attributes?.Select(attribute => $"{attribute.Key}={attribute.Value}").ToArray() ?? [],
                data.Body));
        }
    }
}
