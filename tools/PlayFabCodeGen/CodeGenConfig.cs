// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace PlayFabCodeGen;

/// <summary>
/// Configuration for the PlayFab code generator, loaded from codegen-config.json.
/// </summary>
public sealed class CodeGenConfig
{
    [JsonPropertyName("specSource")]
    public SpecSourceConfig SpecSource { get; set; } = new();

    [JsonPropertyName("output")]
    public OutputConfig Output { get; set; } = new();

    [JsonPropertyName("include")]
    public List<string> Include { get; set; } = [];

    [JsonPropertyName("exclude")]
    public ExcludeConfig Exclude { get; set; } = new();

    [JsonPropertyName("preferences")]
    public PreferencesConfig Preferences { get; set; } = new();
}

public sealed class SpecSourceConfig
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "github";

    [JsonPropertyName("repo")]
    public string Repo { get; set; } = "PlayFab/API_Specs";

    [JsonPropertyName("branch")]
    public string Branch { get; set; } = "master";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "Swagger/PlayFab";
}

public sealed class OutputConfig
{
    [JsonPropertyName("directory")]
    public string Directory { get; set; } = "../../src/XboxDevCLI/PlayFab/Generated";

    [JsonPropertyName("namespace")]
    public string Namespace { get; set; } = "XboxDevCLI.PlayFab.Generated";
}

public sealed class ExcludeConfig
{
    [JsonPropertyName("groups")]
    public List<string> Groups { get; set; } = [];

    [JsonPropertyName("apis")]
    public Dictionary<string, List<string>> Apis { get; set; } = [];
}

public sealed class PreferencesConfig
{
    [JsonPropertyName("nullableReferenceTypes")]
    public bool NullableReferenceTypes { get; set; } = true;

    [JsonPropertyName("generateXmlDocs")]
    public bool GenerateXmlDocs { get; set; } = true;
}
