// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.Configuration;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace XboxDevCLI.Telemetry;

internal sealed class TelemetryService : IDisposable
{
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(2);

    private readonly TracerProvider? _tracerProvider;
    private readonly MeterProvider? _meterProvider;
    private readonly Action<string>? _diagnostic;

    private TelemetryService(
        TelemetryOptions options,
        TracerProvider? tracerProvider,
        MeterProvider? meterProvider,
        Action<string>? diagnostic)
    {
        Options = options;
        _tracerProvider = tracerProvider;
        _meterProvider = meterProvider;
        _diagnostic = diagnostic;
    }

    internal TelemetryOptions Options { get; }

    /// <summary>
    /// True when telemetry providers were built and the CLI is actively exporting telemetry
    /// (a valid destination is configured and initialization succeeded). This is the correct
    /// gate for the first-run disclosure: it is only shown when data is really being collected.
    /// </summary>
    internal bool IsCollecting => _meterProvider != null || _tracerProvider != null;

    internal static TelemetryService Create(
        IConfiguration configuration,
        Action<string>? diagnostic = null)
    {
        TelemetryOptions options;
        try
        {
            options = TelemetryOptions.FromConfiguration(configuration);
        }
        catch (Exception exception)
        {
            // Resolving options reads configuration and assembly metadata; if that ever
            // fails, telemetry must still not break the CLI. Fail open to a disabled no-op.
            diagnostic?.Invoke(
                $"Telemetry configuration could not be resolved ({TelemetryInstrumentation.ClassifyException(exception)}).");
            return new TelemetryService(
                new TelemetryOptions(false, null, false, 0),
                null,
                null,
                diagnostic);
        }

        if (!options.Enabled || options.ConnectionString == null)
        {
            return new TelemetryService(options, null, null, diagnostic);
        }

        TracerProvider? tracerProvider = null;
        MeterProvider? meterProvider = null;
        try
        {
            var resource = ResourceBuilder.CreateDefault()
                .AddService(
                    TelemetryInstrumentation.SourceName,
                    serviceVersion: TelemetryInstrumentation.ServiceVersion);

            tracerProvider = Sdk.CreateTracerProviderBuilder()
                .SetResourceBuilder(resource)
                .AddSource(TelemetryInstrumentation.SourceName)
                .SetSampler(new ParentBasedSampler(
                    new TraceIdRatioBasedSampler(options.DependencyTraceSamplingRatio)))
                .AddAzureMonitorTraceExporter(exporter =>
                    exporter.ConnectionString = options.ConnectionString)
                .Build();

            meterProvider = Sdk.CreateMeterProviderBuilder()
                .SetResourceBuilder(resource)
                .AddMeter(TelemetryInstrumentation.SourceName)
                .AddAzureMonitorMetricExporter(exporter =>
                    exporter.ConnectionString = options.ConnectionString)
                .Build();

            return new TelemetryService(options, tracerProvider, meterProvider, diagnostic);
        }
        catch (Exception exception)
        {
            // Telemetry is best-effort: any failure to initialize the exporter (including
            // unexpected SDK/exporter or assembly/type-load failures under single-file
            // publish, or offline-storage I/O errors in locked-down environments) must
            // never break the CLI or change its exit code. Fail open to a disabled no-op.
            diagnostic?.Invoke(
                $"Telemetry exporter could not be initialized ({TelemetryInstrumentation.ClassifyException(exception)}).");

            // Dispose any provider already built before the failure to avoid leaking its
            // exporter/background resources.
            TryDispose(tracerProvider, diagnostic);
            TryDispose(meterProvider, diagnostic);

            return new TelemetryService(
                options with { Enabled = false },
                null,
                null,
                diagnostic);
        }
    }

    private static void TryDispose(IDisposable? disposable, Action<string>? diagnostic)
    {
        if (disposable == null)
        {
            return;
        }

        try
        {
            disposable.Dispose();
        }
        catch (Exception exception)
        {
            diagnostic?.Invoke(
                $"Telemetry exporter disposal failed ({TelemetryInstrumentation.ClassifyException(exception)}).");
        }
    }

    internal void RecordCommand(string commandName, int exitCode, TimeSpan duration, string authMode)
    {
        if (Options.Enabled)
        {
            TelemetryInstrumentation.RecordCommand(commandName, exitCode, duration, authMode);
        }
    }

    internal void RecordUnhandledFailure(Exception exception)
    {
        if (Options.Enabled)
        {
            TelemetryInstrumentation.RecordUnhandledFailure(exception);
        }
    }

    public void Dispose()
    {
        var timeoutMilliseconds = (int)FlushTimeout.TotalMilliseconds;
        FlushAndDispose(
            () => _tracerProvider?.ForceFlush(timeoutMilliseconds),
            () => _tracerProvider?.Dispose());
        FlushAndDispose(
            () => _meterProvider?.ForceFlush(timeoutMilliseconds),
            () => _meterProvider?.Dispose());
    }

    private void FlushAndDispose(Action flush, Action dispose)
    {
        try
        {
            flush();
        }
        catch (Exception exception)
        {
            _diagnostic?.Invoke(
                $"Telemetry exporter flush failed ({TelemetryInstrumentation.ClassifyException(exception)}).");
        }

        try
        {
            dispose();
        }
        catch (Exception exception)
        {
            _diagnostic?.Invoke(
                $"Telemetry exporter disposal failed ({TelemetryInstrumentation.ClassifyException(exception)}).");
        }
    }
}
