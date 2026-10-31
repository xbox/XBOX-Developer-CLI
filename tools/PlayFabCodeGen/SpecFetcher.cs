// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;

namespace PlayFabCodeGen;

/// <summary>
/// Fetches PlayFab Swagger spec files from GitHub or a local directory.
/// </summary>
public sealed class SpecFetcher : IDisposable
{
    private readonly SpecSourceConfig _source;
    private readonly HttpClient _http;

    public SpecFetcher(SpecSourceConfig source)
    {
        _source = source;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PlayFabCodeGen/1.0");
    }

    /// <summary>
    /// Returns the list of available API group names from the spec source.
    /// </summary>
    public async Task<List<string>> GetAvailableGroupsAsync()
    {
        if (_source.Type == "local")
        {
            return Directory.GetFiles(_source.Path, "*.swagger.json")
                .Select(f => Path.GetFileNameWithoutExtension(f).Replace(".swagger", ""))
                .ToList();
        }

        // GitHub: list files via Contents API
        var url = $"https://api.github.com/repos/{_source.Repo}/contents/{_source.Path}?ref={_source.Branch}";
        var json = await _http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);

        return doc.RootElement.EnumerateArray()
            .Where(e => e.GetProperty("name").GetString()?.EndsWith(".swagger.json") == true)
            .Select(e => e.GetProperty("name").GetString()!.Replace(".swagger.json", ""))
            .ToList();
    }

    /// <summary>
    /// Fetches the Swagger spec JSON for the given API group.
    /// </summary>
    public async Task<string> FetchSpecAsync(string groupName)
    {
        if (_source.Type == "local")
        {
            var filePath = Path.Combine(_source.Path, $"{groupName}.swagger.json");
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Spec file not found: {filePath}");
            }

            return await File.ReadAllTextAsync(filePath);
        }

        // GitHub: fetch raw content
        var url = $"https://raw.githubusercontent.com/{_source.Repo}/{_source.Branch}/{_source.Path}/{groupName}.swagger.json";
        return await _http.GetStringAsync(url);
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
