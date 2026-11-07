// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Reflection;
using System.Text.Json;
using NuGet.Versioning;

namespace XboxDevCLI.Services;

internal sealed class CliUpdateChecker(HttpClient client)
{
    internal const int MaximumMetadataBytes = 1024 * 1024;
    internal static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);

    internal static string CurrentVersion =>
        typeof(CliUpdateChecker).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? CliIdentity.Version;

    internal async Task<CliUpdateResult> CheckAsync(string currentVersion, CancellationToken cancellationToken)
    {
        var current = ParseVersion(currentVersion);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(CheckTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, ProjectLinks.LatestReleaseApi);
        request.Headers.UserAgent.ParseAdd("XboxDevCLI-UpdateCheck/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode)
        {
            var guidance = response.StatusCode switch
            {
                HttpStatusCode.NotFound =>
                    "No published stable release is accessible anonymously. The repository may be private or have no stable release.",
                HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests =>
                    "GitHub denied or rate-limited the request. Try again later.",
                _ => "GitHub release metadata is unavailable. Try again later.",
            };
            throw new HttpRequestException(
                $"HTTP {(int)response.StatusCode}: {guidance} Open {ProjectLinks.Releases} in your browser.");
        }

        if (response.Content.Headers.ContentLength > MaximumMetadataBytes)
            throw new InvalidDataException("Release metadata exceeds the 1 MiB limit.");
        await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var metadata = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, deadline.Token)) > 0)
        {
            if (metadata.Length + read > MaximumMetadataBytes)
                throw new InvalidDataException("Release metadata exceeds the 1 MiB limit.");
            metadata.Write(buffer, 0, read);
        }

        using var document = JsonDocument.Parse(metadata.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False
            || !root.TryGetProperty("prerelease", out var prerelease) || prerelease.ValueKind != JsonValueKind.False
            || !root.TryGetProperty("tag_name", out var tagElement) || tagElement.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("GitHub did not return valid published stable-release metadata.");

        var tag = tagElement.GetString()!;
        var versionText = tag.StartsWith('v') ? tag[1..] : tag;
        var latest = ParseVersion(versionText);
        if (latest.IsPrerelease)
            throw new InvalidDataException("GitHub returned a prerelease tag instead of a stable release.");

        var updateAvailable = VersionComparer.VersionRelease.Compare(latest, current) > 0;
        var releaseUrl = $"{ProjectLinks.ReleaseTags}/{Uri.EscapeDataString(tag)}";
        return new CliUpdateResult(
            SchemaVersion: 1,
            CurrentVersion: currentVersion,
            LatestVersion: versionText,
            UpdateAvailable: updateAvailable,
            ReleaseNotesUrl: releaseUrl,
            Message: updateAvailable
                ? "A newer stable XBOX CLI release is available. Update manually using the instructions below."
                : "The running CLI version is equal to or newer than the latest stable release.",
            Instructions: updateAvailable
                ? [
                    $"Open {releaseUrl} and review the release notes and installation instructions.",
                    $"For a portable install, download the ZIP for your operating system and CPU architecture and extract the whole archive into a new directory. Installation and PATH steps: {ProjectLinks.InstallationGuide}",
                    "Keep a backup of the old installation. Close running CLI processes before replacing it, especially on Windows.",
                    "Keep any XBOX_ environment variables you set. Settings come from them, command options, and the new release's built-in defaults, not from a settings file.",
                    "Leave existing user-local state in place (%LOCALAPPDATA%\\XboxDevCLI on Windows). Saved seller selections and authentication cache are automatically reused for the same OS user; seller lookup still uses the signed-in identity. No import or seller reset is needed.",
                    "If you keep the same installation path, PATH needs no change. Otherwise replace the old PATH entry with the directory containing the new executable, not the executable filename. On Windows edit your user Path environment variable; on Linux/macOS update PATH in your shell startup file.",
                    "Open a new terminal. Inspect 'Get-Command xbox -All' in PowerShell or 'type -a xbox' in Bash/Zsh to ensure an old copy or alias is not selected, then run 'xbox --version' and compare it with the downloaded release.",
                    "For a source build or package-managed install, use its original build or installation mechanism instead of manually overwriting managed files. A package channel may lag the GitHub release."
                ]
                : []);
    }

    private static NuGetVersion ParseVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsWhiteSpace)
            || !NuGetVersion.TryParse(value, out var version))
            throw new InvalidDataException("Cannot compare versions: expected a semantic version such as '1.2.3' or '1.2.3-preview.1'.");
        return version;
    }
}

internal sealed record CliUpdateResult(
    int SchemaVersion, string CurrentVersion, string LatestVersion, bool UpdateAvailable,
    string ReleaseNotesUrl, string Message, string[] Instructions);
