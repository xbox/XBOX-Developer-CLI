// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text.RegularExpressions;

namespace XboxDevCLI.Services;

/// <summary>
/// Reads and switches the XBOX sandbox this Windows PC uses, through <c>XblPCSandbox.exe</c>.
/// </summary>
/// <remarks>
/// Every operation is delegated to <c>XblPCSandbox.exe</c>, the PC Sandbox Switcher in the GDK,
/// so the CLI reports and does exactly what the GDK documentation describes. Its <c>/get</c> asks
/// Gaming Services for the sandbox in use, which can differ from the registry value after a switch
/// that did not finish. Its <c>/set</c> calls the supported <c>XboxAuthManager</c> API, restarts
/// the dependent services, and handles the UAC elevation split; reimplementing those steps here
/// would copy OS internals that are not a public contract.
/// </remarks>
internal sealed record PcSandboxDependencies(
    Func<bool> IsWindows,
    Func<string, bool> FileExists,
    Func<IEnumerable<string>> GetSearchDirectories,
    Func<string, IReadOnlyList<string>, TextWriter, CancellationToken, Task<int>> RunToolAsync,
    Func<bool> IsInteractive,
    Func<string?> ReadLine)
{
    internal static PcSandboxDependencies Default { get; } = new(
        OperatingSystem.IsWindows,
        File.Exists,
        PcSandbox.DefaultSearchDirectories,
        PcSandbox.RunProcessAsync,
        () => Environment.UserInteractive && !Console.IsInputRedirected && !Console.IsErrorRedirected,
        Console.ReadLine);
}

internal static partial class PcSandbox
{
    internal const string Retail = "RETAIL";
    internal const string ToolFileName = "XblPCSandbox.exe";
    internal const string ToolPathConfigurationKey = "XblPCSandbox:Path";
    internal const string TroubleshootingUrl = "https://aka.ms/XboxLiveSandboxHelp";

    // Lines XblPCSandbox.exe prints for /get and after a /set. The tool ships no localized strings.
    internal const string CurrentSandboxPrefix = "Current Sandbox:";
    internal const string SandboxIsNowPrefix = "Sandbox is now:";

    internal static IReadOnlyList<string> GetArguments { get; } = ["/get"];
    internal static IReadOnlyList<string> LaunchAppsArguments { get; } = ["/launchApps"];

    private const string FeedbackHint = "To report a problem, run 'XblPCSandbox /feedback'.";

    /// <summary>Closes every failure message so it points where the tool's own errors point.</summary>
    internal static string HelpHint { get; } =
        $"For troubleshooting help, visit {TroubleshootingUrl}. {FeedbackHint}";

    /// <summary>
    /// <see cref="HelpHint"/>, without the troubleshooting link when the tool's output, shown just
    /// above, already printed it.
    /// </summary>
    internal static string HelpHintAfter(IEnumerable<string> toolOutput) =>
        toolOutput.Any(line => line.Contains(TroubleshootingUrl, StringComparison.OrdinalIgnoreCase))
            ? FeedbackHint
            : HelpHint;

    // Win32 ERROR_CANCELLED: the tool reports this when its UAC prompt is declined.
    internal const int ElevationDeclinedExitCode = 1223;

    // COR_E_INVALIDOPERATION: the tool reports every Gaming Services failure this way (missing,
    // out of date, or in a bad state) and prints the specific cause first.
    internal const int GamingServicesFailureExitCode = unchecked((int)0x80131509);

    /// <summary>
    /// Mirrors the <c>IsValidSandboxName</c> rule in GRTS (letters, digits, and dots), plus a
    /// leading alphanumeric and a length cap, so the CLI rejects an ID GRTS would reject before
    /// it asks for elevation.
    /// </summary>
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9.]{0,63}$")]
    private static partial Regex SandboxIdPattern();

    internal static bool IsPlausibleSandboxId(string value) => SandboxIdPattern().IsMatch(value);

    /// <summary>
    /// Finds the sandbox in the last tool output line that starts with <paramref name="prefix"/>,
    /// such as <c>Current Sandbox: XDKS.1</c>. Returns null when no such line names a sandbox.
    /// </summary>
    internal static string? ParseSandbox(IEnumerable<string> lines, string prefix)
    {
        string? sandbox = null;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed[prefix.Length..].Trim();
                if (value.Length > 0)
                {
                    sandbox = value;
                }
            }
        }

        return sandbox;
    }

    /// <summary>
    /// Finds <c>XblPCSandbox.exe</c>: the configured path when one is set, otherwise the CLI's own
    /// directory followed by each <c>PATH</c> entry.
    /// </summary>
    /// <exception cref="FileNotFoundException">The tool could not be found.</exception>
    internal static string LocateTool(string? configuredPath, PcSandboxDependencies dependencies)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var path = configuredPath.Trim();
            if (dependencies.FileExists(path))
            {
                return path;
            }

            throw new FileNotFoundException(
                $"{ToolPathConfigurationKey} is set to '{path}', but no file exists there.");
        }

        foreach (var directory in dependencies.GetSearchDirectories())
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            var candidate = Path.Combine(directory.Trim().Trim('"'), ToolFileName);
            if (dependencies.FileExists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"{ToolFileName} was not found next to the CLI or on PATH. Install the XBOX services development " +
            "tools (XboxLiveTools.zip from https://github.com/microsoft/xbox-live-developer-tools/releases), " +
            $"then add its folder to PATH or set {ToolPathConfigurationKey} " +
            "(environment variable XBOX_XBLPCSANDBOX__PATH).");
    }

    /// <summary>The <c>XblPCSandbox.exe</c> arguments that switch to <paramref name="sandbox"/>.</summary>
    /// <remarks>
    /// Like the tool, a switch reopens the Microsoft Store and XBOX app when they are already open;
    /// <paramref name="noApps"/> passes <c>/noApps</c> to skip that.
    /// </remarks>
    internal static IReadOnlyList<string> BuildSetArguments(string sandbox, bool noApps)
    {
        List<string> arguments = string.Equals(sandbox, Retail, StringComparison.Ordinal)
            ? ["/retail"]
            : ["/set", sandbox];
        if (noApps)
        {
            arguments.Add("/noApps");
        }

        return arguments;
    }

    /// <summary>
    /// Explains a non-zero <c>XblPCSandbox.exe</c> exit code where the cause is known.
    /// <paramref name="toolOutput"/> is what the tool printed, so the link is not repeated.
    /// </summary>
    internal static string DescribeToolFailure(int exitCode, IEnumerable<string> toolOutput)
    {
        var code = FormattableString.Invariant($"0x{exitCode:X8}");
        var cause = exitCode switch
        {
            ElevationDeclinedExitCode =>
                $"The administrator prompt was declined, so the sandbox was not changed ({code}).",
            GamingServicesFailureExitCode =>
                $"Gaming Services is missing, out of date, or not working on this PC ({code}). " +
                $"The {ToolFileName} output above names the cause. Repair it with the Gaming " +
                "Services Repair Tool: https://aka.ms/gamingserviceshelp",
            _ => $"{ToolFileName} failed with exit code {code}. Its output is shown above. Run " +
                "'xbox pc sandbox get' to see which sandbox this PC uses now.",
        };
        return $"{cause} {HelpHintAfter(toolOutput)}";
    }

    internal static IEnumerable<string> DefaultSearchDirectories()
    {
        yield return AppContext.BaseDirectory;
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            yield return entry;
        }
    }

    /// <summary>
    /// Runs the tool without a shell, forwarding everything it prints to
    /// <paramref name="diagnostics"/> so the CLI's stdout carries only the JSON result.
    /// </summary>
    internal static async Task<int> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TextWriter diagnostics,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"{ToolFileName} could not be started.");
        var stdout = ForwardAsync(process.StandardOutput, diagnostics, CancellationToken.None);
        var stderr = ForwardAsync(process.StandardError, diagnostics, CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The tool runs in its own hidden console, so Ctrl-C does not reach it. Stop it here.
            // An elevated copy that UAC already approved cannot be stopped from this process and
            // may still finish the switch, so say so.
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException
                or System.ComponentModel.Win32Exception or AggregateException)
            {
                // The tool already exited, or an elevated descendant cannot be stopped from here.
            }

            throw;
        }

        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        return process.ExitCode;
    }

    private static async Task ForwardAsync(
        StreamReader reader,
        TextWriter diagnostics,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length > 0)
            {
                lock (diagnostics)
                {
                    diagnostics.WriteLine($"  {line}");
                }
            }
        }
    }
}
