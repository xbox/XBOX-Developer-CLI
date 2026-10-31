// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XboxDevCLI.EndToEndTests;

internal sealed record CliResult(int ExitCode, string StandardOutput, string StandardError)
{
    private const int MaxDiagnosticLength = 2000;

    /// <summary>
    /// Describes the result for an assertion message.
    /// </summary>
    /// <remarks>
    /// Never includes stdout: it carries service responses, which can hold player or economy
    /// data and would otherwise end up in test logs and TRX files. Stderr carries the CLI's own
    /// diagnostics and error envelopes, and is truncated.
    /// </remarks>
    public string Describe()
    {
        var stderr = StandardError.Length > MaxDiagnosticLength
            ? StandardError[..MaxDiagnosticLength] + "…(truncated)"
            : StandardError;
        return $"exit code {ExitCode}, {StandardOutput.Length} characters on stdout; stderr:{Environment.NewLine}{stderr}";
    }
}

/// <summary>
/// Runs a copy of the CLI under test as a child process with controlled settings.
/// </summary>
internal sealed class CliRunner : IDisposable
{
    internal const string AppSettingsFileName = "appsettings.json";
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    private readonly string _root;
    private readonly string _cliDirectory;
    private readonly IReadOnlyDictionary<string, string> _environment;

    private CliRunner(string root, string cliDirectory, IReadOnlyDictionary<string, string> environment)
    {
        _root = root;
        _cliDirectory = cliDirectory;
        _environment = environment;
    }

    internal static string ExecutableName => OperatingSystem.IsWindows() ? "xbox.exe" : "xbox";

    internal string WorkingDirectory => Path.Combine(_root, "work");

    /// <summary>
    /// Copies the CLI into a private directory, points its settings at the profile, and
    /// prepares an isolated state directory.
    /// </summary>
    /// <remarks>
    /// The shipped appsettings.json must be rewritten rather than overlaid: configuration
    /// layers merge arrays by index, so no overlay or environment variable can remove the
    /// inherited routing header that a development stamp must not receive.
    /// </remarks>
    internal static CliRunner Create(LiveSettings settings, IDictionary parentEnvironment)
    {
        var root = Path.Combine(Path.GetTempPath(), "xbox-e2e-" + Guid.NewGuid().ToString("N"));
        var cliDirectory = Path.Combine(root, "cli");
        var stateDirectory = Path.Combine(root, "state");
        Directory.CreateDirectory(stateDirectory);
        Directory.CreateDirectory(Path.Combine(root, "work"));
        CopyDirectory(settings.CliDirectory, cliDirectory);

        var appSettingsPath = Path.Combine(cliDirectory, AppSettingsFileName);
        var appSettings = ReadAppSettings(cliDirectory);
        File.WriteAllText(
            appSettingsPath,
            ApplyProfile(appSettings, settings.Profile).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        return new CliRunner(
            root,
            cliDirectory,
            BuildEnvironment(parentEnvironment, settings, stateDirectory));
    }

    internal static JsonObject ReadAppSettings(string cliDirectory) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(cliDirectory, AppSettingsFileName))) as JsonObject
        ?? throw new InvalidDataException($"{AppSettingsFileName} in the CLI under test is not a JSON object.");

    internal static JsonObject ApplyProfile(JsonObject appSettings, E2EProfile profile)
    {
        var azureAd = GetOrAddObject(appSettings, "AzureAd");
        if (profile.Scope is not null)
        {
            azureAd["Scopes"] = profile.Scope;
        }

        if (E2EProfiles.GetScopeError((string?)azureAd["Scopes"], profile.Audience) is { } scopeError)
        {
            throw new InvalidDataException(scopeError);
        }

        var playFab = GetOrAddObject(appSettings, "PlayFab");
        playFab["BaseUrlTemplate"] = profile.PlayFab.BaseUrlTemplate;
        playFab["RequestHeaders"] = new JsonArray(profile.PlayFab.RequestHeaders
            .Select(header => (JsonNode)new JsonObject
            {
                ["Name"] = header.Name,
                ["Value"] = header.Value,
            })
            .ToArray());

        var xboxPartner = GetOrAddObject(appSettings, "XboxPartner");
        xboxPartner["BaseUrl"] = profile.XboxPartner.BaseUrl;

        var telemetry = GetOrAddObject(appSettings, "Telemetry");
        telemetry["Enabled"] = false;

        return appSettings;
    }

    /// <summary>
    /// Builds the child process environment from the parent's, keeping no inherited
    /// <c>XBOX_*</c> or <c>E2E_*</c> variable and redirecting per-user state.
    /// </summary>
    internal static Dictionary<string, string> BuildEnvironment(
        IDictionary parentEnvironment,
        LiveSettings settings,
        string stateDirectory)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in parentEnvironment)
        {
            var name = (string)entry.Key;
            if (name.StartsWith("XBOX_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("E2E_", StringComparison.OrdinalIgnoreCase)
                || entry.Value is not string value)
            {
                continue;
            }

            environment[name] = value;
        }

        environment["XBOX_AUTHENTICATION__MODE"] = "githuboidc";
        environment[LiveSettings.ClientIdVariable] = settings.ClientId;
        environment[LiveSettings.TenantIdVariable] = settings.TenantId;
        environment["XBOX_TELEMETRY__ENABLED"] = "false";
        environment["XBOX_TELEMETRY_OPTOUT"] = "1";

        // Keeps saved context, user settings and caches out of the run on Linux and macOS.
        // Windows resolves LocalApplicationData without these variables; the live workflow
        // runs on Linux.
        environment["HOME"] = stateDirectory;
        environment["XDG_DATA_HOME"] = Path.Combine(stateDirectory, "data");
        environment["XDG_CONFIG_HOME"] = Path.Combine(stateDirectory, "config");
        environment["XDG_CACHE_HOME"] = Path.Combine(stateDirectory, "cache");
        environment["LOCALAPPDATA"] = Path.Combine(stateDirectory, "local");
        environment["APPDATA"] = Path.Combine(stateDirectory, "roaming");
        return environment;
    }

    public Task<CliResult> RunAsync(params string[] arguments) =>
        RunAsync(arguments, DefaultTimeout);

    public async Task<CliResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo(Path.Combine(_cliDirectory, ExecutableName))
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = WorkingDirectory,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment.Clear();
        foreach (var (name, value) in _environment)
        {
            startInfo.Environment[name] = value;
        }

        return await RunProcessAsync(startInfo, timeout);
    }

    internal static async Task<CliResult> RunProcessAsync(ProcessStartInfo startInfo, TimeSpan timeout)
    {
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {startInfo.FileName}.");
        process.StandardInput.Close();

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw new TimeoutException(
                $"{Path.GetFileName(startInfo.FileName)} did not exit within {timeout.TotalSeconds:F0} seconds.");
        }

        return new CliResult(process.ExitCode, await stdout, await stderr);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort: the temp directory holds no credentials, only the CLI copy and state.
        }
    }

    private static JsonObject GetOrAddObject(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}
