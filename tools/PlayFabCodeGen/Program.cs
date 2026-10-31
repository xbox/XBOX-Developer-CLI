// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using PlayFabCodeGen;

const string defaultConfigFile = "codegen-config.json";

// Determine config path from args or default
var configPath = args.Length > 0 ? args[0] : defaultConfigFile;

if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"Config file not found: {configPath}");
    Console.Error.WriteLine("Usage: PlayFabCodeGen [config-file-path]");
    Console.Error.WriteLine($"Default: {defaultConfigFile}");
    return 1;
}

Console.WriteLine($"Loading config from: {configPath}");
var configJson = await File.ReadAllTextAsync(configPath);
var config = JsonSerializer.Deserialize<CodeGenConfig>(configJson)
    ?? throw new InvalidOperationException("Failed to parse config file.");

// Resolve output directory relative to config file
var configDir = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".";
var outputDir = Path.IsPathRooted(config.Output.Directory)
    ? config.Output.Directory
    : Path.GetFullPath(Path.Combine(configDir, config.Output.Directory));

Console.WriteLine($"Output directory: {outputDir}");
Directory.CreateDirectory(outputDir);

// Fetch specs
using var fetcher = new SpecFetcher(config.SpecSource);
Console.WriteLine($"Spec source: {config.SpecSource.Type}");

var availableGroups = await fetcher.GetAvailableGroupsAsync();
Console.WriteLine($"Available API groups: {string.Join(", ", availableGroups)}");

// Determine which groups to process
var groupsToProcess = availableGroups;
if (config.Include.Count > 0)
{
    groupsToProcess = availableGroups
        .Where(g => config.Include.Contains(g, StringComparer.OrdinalIgnoreCase))
        .ToList();
}

if (config.Exclude.Groups.Count > 0)
{
    groupsToProcess = groupsToProcess
        .Where(g => !config.Exclude.Groups.Contains(g, StringComparer.OrdinalIgnoreCase))
        .ToList();
}

Console.WriteLine($"Processing {groupsToProcess.Count} groups: {string.Join(", ", groupsToProcess)}");

// Parse all specs
var allGroups = new List<ApiGroup>();
foreach (var groupName in groupsToProcess.Order())
{
    Console.Write($"  Fetching {groupName}...");
    var specJson = await fetcher.FetchSpecAsync(groupName);
    var group = SwaggerParser.Parse(groupName, specJson);

    // Apply per-group API exclusions
    if (config.Exclude.Apis.TryGetValue(groupName, out var excludedApis))
    {
        group.Operations.RemoveAll(
            op => excludedApis.Contains(op.OperationId, StringComparer.OrdinalIgnoreCase));
    }

    allGroups.Add(group);
    Console.WriteLine($" {group.Operations.Count} operations, {group.Types.Count} types");
}

// Generate C#
var emitter = new CSharpEmitter(config);
var totalFiles = 0;

// Emit per-group model files
foreach (var group in allGroups)
{
    var modelsCode = emitter.EmitModels(group);
    if (modelsCode == null)
    {
        Console.WriteLine($"  Skipping {group.Name}Models.cs (no types)");
        continue;
    }

    var modelsPath = Path.Combine(outputDir, $"{group.Name}Models.cs");
    await File.WriteAllTextAsync(modelsPath, modelsCode);
    totalFiles++;
}

// Emit API metadata
var metadataCode = emitter.EmitApiMetadata(allGroups);
var metadataPath = Path.Combine(outputDir, "PlayFabApiMetadata.cs");
await File.WriteAllTextAsync(metadataPath, metadataCode);
totalFiles++;

var totalOps = allGroups.Sum(g => g.Operations.Count);
var totalTypes = allGroups.Sum(g => g.Types.Count);
Console.WriteLine();
Console.WriteLine($"Generated {totalFiles} files ({totalOps} operations, {totalTypes} types)");
Console.WriteLine($"Output: {outputDir}");

return 0;
