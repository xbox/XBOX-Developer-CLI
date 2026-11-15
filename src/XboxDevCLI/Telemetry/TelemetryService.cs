// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;

namespace XboxDevCLI.Telemetry;

/// <summary>
/// Sends the invocation's one <see cref="CommandInvocationEvent"/> as an Application Insights
/// custom event. Telemetry is best effort: nothing here may fail the CLI or change its exit code.
/// </summary>
internal sealed class TelemetryService
{
    internal const string ServiceName = "xbox-cli";
    internal const string StorageSubDirectory = "xbox-cli";
    internal const int FailureDrainBudgetMilliseconds = 300;

    private const string ExporterSettingPrefix = "Azure.Monitor.OpenTelemetry.Exporter.";

    private readonly Action<string>? _diagnostic;
    private readonly Action<OpenTelemetryLoggerOptions>? _configureExport;
    private readonly Func<string, string?> _getEnvironmentVariable;
    private bool _disclosureShown;

    private TelemetryService(
        TelemetryOptions options,
        Action<string>? diagnostic,
        Action<OpenTelemetryLoggerOptions>? configureExport,
        Func<string, string?> getEnvironmentVariable)
    {
        Options = options;
        _diagnostic = diagnostic;
        _configureExport = configureExport;
        _getEnvironmentVariable = getEnvironmentVariable;
    }

    internal TelemetryOptions Options { get; }

    /// <summary>
    /// True when telemetry is enabled and has somewhere to go, which is when the first-run
    /// disclosure is shown.
    /// </summary>
    internal bool IsCollecting => Options.Enabled && Options.ConnectionString is not null;

    /// <summary>
    /// Shows the first-run disclosure, if telemetry is collecting. Nothing is sent unless this has
    /// shown it, on this run or an earlier one.
    /// </summary>
    internal void ShowDisclosure(string stateDirectory, TextWriter writer, bool useAnsi)
    {
        if (IsCollecting)
        {
            _disclosureShown = TelemetryDisclosure.WriteIfFirstRun(stateDirectory, writer, useAnsi);
        }
    }

    /// <summary>Whether this run is in CI, by the presence of a CI variable.</summary>
    internal bool IsCi => TelemetryInstrumentation.IsCi(_getEnvironmentVariable);

    /// <summary>Resolves the telemetry settings. Never throws: a setting that can't be read turns telemetry off.</summary>
    /// <param name="configuration">The invocation's configuration.</param>
    /// <param name="diagnostic">Receives what went wrong with telemetry, for <c>--verbose</c>.</param>
    /// <param name="configureExport">
    /// Replaces the Azure Monitor exporter, so tests can see what would be sent.
    /// </param>
    /// <param name="getEnvironmentVariable">Reads environment variables; the process's by default.</param>
    internal static TelemetryService Create(
        IConfiguration configuration,
        Action<string>? diagnostic = null,
        Action<OpenTelemetryLoggerOptions>? configureExport = null,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        try
        {
            return new TelemetryService(
                TelemetryOptions.FromConfiguration(configuration, getEnvironmentVariable),
                diagnostic,
                configureExport,
                getEnvironmentVariable);
        }
        catch (Exception exception)
        {
            diagnostic?.Invoke(
                $"Telemetry configuration could not be resolved ({TelemetryInstrumentation.ClassifyException(exception)}).");
            return new TelemetryService(
                new TelemetryOptions(false, null),
                diagnostic,
                configureExport,
                getEnvironmentVariable);
        }
    }

    /// <summary>
    /// Sends <paramref name="invocation"/>, if telemetry is collecting and the disclosure has been
    /// shown (<see cref="ShowDisclosure"/>). On success the exporter
    /// gets no time to send before exit and saves the event to send on a later run; on failure it
    /// gets <see cref="FailureDrainBudgetMilliseconds"/>.
    /// </summary>
    internal void Emit(CommandInvocationEvent invocation)
    {
        if (!IsCollecting || !_disclosureShown)
        {
            return;
        }

        try
        {
            var attributes = TelemetryInstrumentation.ToAttributes(invocation);
            ConfigureExporter(failed: invocation.ExitCode != 0, ci: invocation.Ci);

            using var loggerFactory = LoggerFactory.Create(builder => builder.AddOpenTelemetry(logging =>
            {
                logging.IncludeScopes = false;
                logging.IncludeFormattedMessage = false;
                logging.SetResourceBuilder(ResourceBuilder.CreateEmpty()
                    .AddService(ServiceName, serviceVersion: TelemetryInstrumentation.ServiceVersion, autoGenerateServiceInstanceId: false)
                    // A fixed instance keeps the exporter from sending the machine name as the role instance.
                    .AddAttributes([new("service.instance.id", ServiceName)]));
                logging.AddProcessor(new HourFloorProcessor());
                if (_configureExport is not null)
                {
                    _configureExport(logging);
                }
                else
                {
                    logging.AddAzureMonitorLogExporter(exporter =>
                    {
                        exporter.ConnectionString = Options.ConnectionString;
                        exporter.EnableLiveMetrics = false;
                        exporter.EnableTraceBasedLogsSampler = false;
                    });
                }
            }));

            loggerFactory.CreateLogger(string.Empty).Log(
                LogLevel.Information,
                default,
                attributes,
                null,
                static (_, _) => string.Empty);
        }
        catch (Exception exception)
        {
            _diagnostic?.Invoke(
                $"Telemetry could not be sent ({TelemetryInstrumentation.ClassifyException(exception)}).");
        }
    }

    /// <summary>
    /// Sets the exporter's process-wide settings, which it reads when it starts and when it shuts down.
    /// </summary>
    private static void ConfigureExporter(bool failed, bool ci)
    {
        AppContext.SetData(
            ExporterSettingPrefix + "ShutdownDrainBudgetMilliseconds",
            failed ? FailureDrainBudgetMilliseconds : 0);
        AppContext.SetData(ExporterSettingPrefix + "StorageSubDirectory", StorageSubDirectory);
        // A CI machine rarely runs the CLI again, so an event saved for a later run would never be sent.
        AppContext.SetSwitch(ExporterSettingPrefix + "DisablePersistOnShutdown", ci);
        // The exporter's own usage reporting is separate telemetry, so turn it off.
        Environment.SetEnvironmentVariable("APPLICATIONINSIGHTS_STATSBEAT_DISABLED", "true");
        Environment.SetEnvironmentVariable("APPLICATIONINSIGHTS_SDKSTATS_DISABLED", "true");
    }

    /// <summary>Rounds the event's time down to the hour, so it can't pin down when a command ran.</summary>
    internal sealed class HourFloorProcessor : BaseProcessor<LogRecord>
    {
        public override void OnEnd(LogRecord data)
        {
            data.Timestamp = FloorToHour(data.Timestamp);
        }

        internal static DateTime FloorToHour(DateTime timestamp)
        {
            var utc = timestamp.Kind == DateTimeKind.Local ? timestamp.ToUniversalTime() : timestamp;
            return new DateTime(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerHour), DateTimeKind.Utc);
        }
    }
}
