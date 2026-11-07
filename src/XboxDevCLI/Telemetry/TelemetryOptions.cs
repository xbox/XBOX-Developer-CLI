// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Configuration;
using System.Reflection;

namespace XboxDevCLI.Telemetry;

internal sealed record TelemetryOptions(
    bool Enabled,
    string? ConnectionString,
    bool DependencyTracingEnabled,
    double DependencyTraceSamplingRatio)
{
    internal const string OptOutEnvironmentVariable = "XBOX_TELEMETRY_OPTOUT";

    internal static TelemetryOptions FromConfiguration(
        IConfiguration configuration,
        Func<string, string?>? getEnvironmentVariable = null,
        string? embeddedConnectionString = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        embeddedConnectionString ??= GetEmbeddedConnectionString();

        var optedOut = IsTruthy(getEnvironmentVariable(OptOutEnvironmentVariable));
        var enabled = configuration.GetValue("Telemetry:Enabled", true) && !optedOut;
        // A release build always sends to the destination built into it; the configured one is
        // only for builds without one, so maintainers can test. APPLICATIONINSIGHTS_CONNECTION_STRING
        // isn't read: hosts set it for their own monitoring, which would send the CLI's telemetry to them.
        var connectionString = string.IsNullOrWhiteSpace(embeddedConnectionString)
            ? configuration["Telemetry:ConnectionString"]
            : embeddedConnectionString;

        var samplingRatio = configuration.GetValue("Telemetry:DependencyTraceSamplingRatio", 0.1);
        samplingRatio = Math.Clamp(samplingRatio, 0, 1);

        return new TelemetryOptions(
            enabled,
            string.IsNullOrWhiteSpace(connectionString) ? null : connectionString,
            configuration.GetValue("Telemetry:DependencyTracingEnabled", true),
            samplingRatio);
    }

    private static string? GetEmbeddedConnectionString() =>
        typeof(Program).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute =>
                string.Equals(
                    attribute.Key,
                    "XboxDevCLI.TelemetryConnectionString",
                    StringComparison.Ordinal))
            ?.Value;

    private static bool IsTruthy(string? value) =>
        string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
}
