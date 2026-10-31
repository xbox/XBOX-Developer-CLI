---
title: Code generation
description: Regenerate the PlayFab request and response models and API metadata from PlayFab's Swagger specifications.
---

# Code generation

The XBOX Developer CLI uses `tools/PlayFabCodeGen` to generate PlayFab request and response
models from PlayFab Swagger 2.0 specs. The generated types power typed API calls,
schema inspection, and endpoint metadata for the CLI.

Generated files under `src/XboxDevCLI/PlayFab/Generated/` must not be hand-edited.
Re-run the generator instead. These generated DTOs are excluded from coverage by
`coverage.runsettings`; see [Testing and coverage](testing.md).

## Run the generator

From the repository root:

```bash
cd tools/PlayFabCodeGen
dotnet run -- codegen-config.json
```

If no argument is supplied, the tool looks for `codegen-config.json` in the
current directory. The generator resolves the output directory relative to the
config file, creates it if needed, fetches or reads the configured specs, parses
them, and writes generated C# files.

## Configuration

The generator is configured by `tools/PlayFabCodeGen/codegen-config.json`.

| Key | Description |
|-----|-------------|
| `specSource.type` | `github` to fetch from GitHub, or `local` to read specs from a directory |
| `specSource.repo` | GitHub repository for specs; currently `PlayFab/API_Specs` |
| `specSource.branch` | Branch to fetch from; currently `master` |
| `specSource.path` | Path to Swagger specs; currently `Swagger/PlayFab` |
| `output.directory` | Generated output directory, relative to the config file unless absolute |
| `output.namespace` | Root namespace for generated code |
| `include` | API groups to include; an empty list means all available groups |
| `exclude.groups` | API groups to skip |
| `exclude.apis` | Per-group operation exclusions |
| `preferences.nullableReferenceTypes` | Emits `#nullable enable` and nullable annotations |
| `preferences.generateXmlDocs` | Emits XML documentation from spec descriptions |

The current config includes PlayFab service groups such as `Admin`, `Economy`,
`Multiplayer`, `Groups`, `CloudScript`, and other server-side APIs, while
excluding `Client`, `Server`, and `Matchmaker` groups.

## What gets generated

The generator writes files to `src/XboxDevCLI/PlayFab/Generated/`.

| Output | Purpose |
|--------|---------|
| `{GroupName}Models.cs` | Request and response classes plus enums for an API group, when the group has generated types |
| `PlayFabApiMetadata.cs` | Registry of all generated endpoints, including group, operation ID, API path, tag, description, request type, and response type |

`PlayFabApiMetadata.cs` is the authoritative list of every PlayFab API path the
CLI can call. When adding or reviewing a command, verify the exact path, request
type, and response type there instead of inventing endpoints.

## Use local specs

To generate from a local checkout of PlayFab specs, change the config's
`specSource` to `local` and point `path` at the Swagger directory:

```json
{
  "specSource": {
    "type": "local",
    "path": "D:\\path\\to\\API_Specs\\Swagger\\PlayFab"
  }
}
```

Then run the same generator command:

```bash
cd tools/PlayFabCodeGen
dotnet run -- codegen-config.json
```

The local source expects files named `{GroupName}.swagger.json`.

## When to regenerate

Re-run the generator when:

- PlayFab adds new APIs or changes existing Swagger specs in `PlayFab/API_Specs`.
- The include or exclude lists in `codegen-config.json` change.
- The generator parser, emitter, fetcher, or config model changes.

After regenerating, build and test the repo as described in
[Testing and coverage](testing.md).

## Architecture

| File | Purpose |
|------|---------|
| `Program.cs` | Loads config, resolves output paths, chooses groups, runs the parser and emitter |
| `SpecFetcher.cs` | Lists and fetches specs from GitHub or reads `{GroupName}.swagger.json` files locally |
| `SwaggerParser.cs` | Parses Swagger 2.0 JSON into intermediate API groups, operations, and type definitions |
| `CSharpEmitter.cs` | Emits model files and `PlayFabApiMetadata.cs` from the intermediate representation |
| `CodeGenConfig.cs` | Defines the config model used by `codegen-config.json` |
