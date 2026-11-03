// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Xunit.Sdk;

namespace XboxDevCLI.E2ETest;

/// <summary>Runs the <c>xbox</c> executable under test as a separate process.</summary>
internal static class Cli
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Runs a command for the configured seller, with compact JSON output.</summary>
    public static Task<CliResult> RunForSellerAsync(params string[] arguments) =>
        RunAsync([.. arguments, "--seller-id", E2ESettings.Current.SellerId, "--output", "json-compact"]);

    /// <summary>Runs a command.</summary>
    public static async Task<CliResult> RunAsync(params string[] arguments)
    {
        string commandLine = string.Join(' ', arguments.Select(Quote));
        string cli = E2ESettings.Current.Cli;
        var startInfo = new ProcessStartInfo(cli)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Test the shipped configuration, whatever XBOX_ variables the runner has set.
        foreach (string name in startInfo.Environment.Keys
            .Where(name => name.StartsWith("XBOX_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            startInfo.Environment.Remove(name);
        }

        startInfo.Environment["XBOX_TELEMETRY_OPTOUT"] = "1";

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        process.StandardInput.Close();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        bool timedOut = false;
        using (var timeout = new CancellationTokenSource(CommandTimeout))
        {
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                timedOut = true;
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }

        return new CliResult(
            $"{Path.GetFileName(cli)} {commandLine}", timedOut ? null : process.ExitCode, await stdout, await stderr);
    }

    private static string Quote(string argument) =>
        argument.Length == 0 || argument.Any(char.IsWhiteSpace) ? $"\"{argument}\"" : argument;
}

/// <summary>What one run of the CLI did, all of which goes into a failure's message.</summary>
internal sealed class CliResult(string commandLine, int? exitCode, string stdout, string stderr)
{
    private const int MaxShownLength = 8000;

    /// <summary>The exit code, or null if the command timed out and was killed.</summary>
    public int? ExitCode => exitCode;

    public string Stdout => stdout;

    public string Stderr => stderr;

    /// <summary>Checks that the command succeeded and printed JSON, and returns the JSON.</summary>
    public JsonElement Json()
    {
        Check(exitCode == 0, "Expected exit code 0.");
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(stdout);
        }
        catch (JsonException exception)
        {
            throw new XunitException($"Expected JSON on stdout: {exception.Message}{Environment.NewLine}{this}");
        }
    }

    public void Check([DoesNotReturnIf(false)] bool condition, string expectation)
    {
        if (!condition)
        {
            throw new XunitException($"{expectation}{Environment.NewLine}{this}");
        }
    }

    public override string ToString() =>
        $"""
        Command: {commandLine}
        Exit code: {exitCode?.ToString(CultureInfo.InvariantCulture) ?? "none; it timed out and was killed"}
        Stdout:
        {Shorten(stdout)}
        Stderr:
        {Shorten(stderr)}
        """;

    private static string Shorten(string text) =>
        text.Length <= MaxShownLength ? text.TrimEnd() : $"{text[..MaxShownLength]}... (cut)";
}
