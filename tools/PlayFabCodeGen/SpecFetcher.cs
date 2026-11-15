// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;

namespace PlayFabCodeGen;

/// <summary>
/// Fetches PlayFab Swagger spec files from GitHub or a local directory.
/// </summary>
public sealed class SpecFetcher : IDisposable
{
    private const string SpecSuffix = ".swagger.json";

    private readonly SpecSourceConfig _source;
    private readonly string? _overlayDirectory;
    private readonly HttpClient _http;

    /// <param name="source">Where to read published specs from.</param>
    /// <param name="overlayDirectory">
    /// Optional resolved directory whose specs add to or replace the source's specs.
    /// </param>
    public SpecFetcher(SpecSourceConfig source, string? overlayDirectory = null)
    {
        _source = source;
        _overlayDirectory = overlayDirectory;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PlayFabCodeGen/1.0");
    }

    /// <summary>
    /// Returns the list of available API group names from the spec source and the overlay.
    /// </summary>
    public async Task<List<string>> GetAvailableGroupsAsync()
    {
        var groups = await GetSourceGroupsAsync();
        return groups
            .Concat(GetOverlayGroups())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Returns the group names that have a spec file in the overlay directory.
    /// </summary>
    public IReadOnlyList<string> GetOverlayGroups() =>
        _overlayDirectory is null || !Directory.Exists(_overlayDirectory)
            ? []
            : Directory.GetFiles(_overlayDirectory, "*" + SpecSuffix)
                .Select(f => Path.GetFileName(f)[..^SpecSuffix.Length])
                .ToList();

    private async Task<List<string>> GetSourceGroupsAsync()
    {
        if (_source.Type == "local")
        {
            return Directory.GetFiles(_source.Path, "*" + SpecSuffix)
                .Select(f => Path.GetFileName(f)[..^SpecSuffix.Length])
                .ToList();
        }

        // GitHub: list files via Contents API
        var url = $"https://api.github.com/repos/{_source.Repo}/contents/{_source.Path}?ref={_source.Branch}";
        var json = await _http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);

        return doc.RootElement.EnumerateArray()
            .Where(e => e.GetProperty("name").GetString()?.EndsWith(SpecSuffix) == true)
            .Select(e => e.GetProperty("name").GetString()![..^SpecSuffix.Length])
            .ToList();
    }

    /// <summary>
    /// Fetches the Swagger spec JSON for the given API group, preferring the overlay.
    /// </summary>
    public async Task<string> FetchSpecAsync(string groupName)
    {
        if (_overlayDirectory is not null)
        {
            var overlayFile = Path.Combine(_overlayDirectory, groupName + SpecSuffix);
            if (File.Exists(overlayFile))
            {
                return await File.ReadAllTextAsync(overlayFile);
            }
        }

        if (_source.Type == "local")
        {
            var filePath = Path.Combine(_source.Path, groupName + SpecSuffix);
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Spec file not found: {filePath}");
            }

            return await File.ReadAllTextAsync(filePath);
        }

        // GitHub: fetch raw content
        var url = $"https://raw.githubusercontent.com/{_source.Repo}/{_source.Branch}/{_source.Path}/{groupName}{SpecSuffix}";
        return await _http.GetStringAsync(url);
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
