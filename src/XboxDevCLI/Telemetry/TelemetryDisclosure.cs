// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using XboxDevCLI.Presentation;
using XboxDevCLI.Services;

namespace XboxDevCLI.Telemetry;

/// <summary>
/// Emits a one-time, first-run telemetry disclosure to standard error, mirroring the
/// notice pattern used by first-party Microsoft CLIs (for example the .NET SDK). The
/// disclosure is shown once per machine/user profile; a sentinel file under the CLI
/// state directory records that it has been displayed.
/// </summary>
internal static class TelemetryDisclosure
{
    internal const string SentinelFileName = ".telemetry-disclosure-shown";

    internal static string DisclosureText => FormatDisclosure(useAnsi: false);

    internal static string FormatDisclosure(bool useAnsi) =>
        string.Join(
            Environment.NewLine,
            useAnsi ? "\u001b[1mTELEMETRY\u001b[0m" : "TELEMETRY",
            $"  {WelcomeExperience.ColorizeBrand("XBOX", useAnsi)} CLI collects anonymous usage telemetry.",
            $"  Learn more or opt out: {ProjectLinks.TelemetryPolicy}");

    /// <summary>
    /// Writes the telemetry disclosure to <paramref name="writer"/> the first time it is
    /// invoked for a given <paramref name="stateDirectory"/>, then records a sentinel so it
    /// is not shown again. This is best-effort: any I/O failure is swallowed so the CLI is
    /// never broken or delayed by the disclosure.
    /// </summary>
    internal static void WriteIfFirstRun(
        string stateDirectory,
        TextWriter writer,
        bool useAnsi = false)
    {
        string sentinelPath;
        try
        {
            sentinelPath = Path.Combine(stateDirectory, SentinelFileName);
            if (File.Exists(sentinelPath))
            {
                return;
            }
        }
        catch
        {
            // If we cannot even determine or probe the sentinel path, skip the disclosure
            // rather than risk throwing on the CLI's hot path.
            return;
        }

        try
        {
            writer.WriteLine(FormatDisclosure(useAnsi));
        }
        catch
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(stateDirectory);
            File.WriteAllText(
                sentinelPath,
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        }
        catch
        {
            // Persisting the sentinel is best-effort. If it fails we may re-show the
            // disclosure on a later run, which is acceptable and preferable to failing.
        }
    }

    /// <summary>
    /// Resolves the per-user state directory used to persist the disclosure sentinel. This
    /// matches the location used for the CLI's token cache.
    /// </summary>
    internal static string GetDefaultStateDirectory() =>
        CliState.GetDefaultDirectory();
}
