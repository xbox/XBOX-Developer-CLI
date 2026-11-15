---
title: Code generation
description: Regenerate the PlayFab request and response models and API metadata from PlayFab's Swagger specifications.
---

# Code generation

The XBOX CLI's PlayFab request and response models, and the list of PlayFab APIs it
can call, are generated from PlayFab's Swagger 2.0 specifications by `tools/PlayFabCodeGen`.
Don't edit the generated files in `src/XboxDevCLI/PlayFab/Generated/` by hand; change the
configuration or the generator, and run it again.

## Run the generator

From the repository root:

```bash
cd tools/PlayFabCodeGen
dotnet run -- codegen-config.json
```

Without an argument, the generator reads `codegen-config.json` from the current directory.
After it runs, build and test as [Testing and coverage](testing.md) describes.

## When to regenerate

Run the generator again when:

- PlayFab adds or changes APIs in `PlayFab/API_Specs`. Move `specSource.branch` to the new
  commit in the same change.
- The `include` or `exclude` lists in `codegen-config.json` change.
- The generator's code changes.

## What gets generated

| Output | Holds |
|--------|-------|
| `{GroupName}Models.cs` | The request and response classes and enums of an API group that has any |
| `PlayFabApiMetadata.cs` | Every generated API, with its group, operation ID, path, tag, description, request type, and response type |

`PlayFabApiMetadata.cs` is the list of every PlayFab API path the CLI can call. When you add or
review a command, check its path, request type, and response type there.

## Configuration

`tools/PlayFabCodeGen/codegen-config.json` configures the generator.

| Key | Description |
|-----|-------------|
| `specSource.type` | `github` to fetch specs from GitHub, or `local` to read them from a directory |
| `specSource.repo` | The GitHub repository of the specs, `PlayFab/API_Specs` |
| `specSource.branch` | The branch, tag, or commit to fetch. It's pinned to the commit the checked-in models came from, so a run reproduces them. |
| `specSource.path` | The path to the Swagger specs, `Swagger/PlayFab` |
| `specSource.overlayPath` | Optional. A directory of `{GroupName}.swagger.json` files used instead of `specSource` for their groups, relative to the configuration file. Currently `specs`. See [Overlay specs](#overlay-specs). |
| `output.directory` | The directory for generated files, relative to the configuration file |
| `output.namespace` | The root namespace of the generated code |
| `include` | The API groups to generate; an empty list means every group |
| `exclude.groups` | API groups to skip |
| `exclude.apis` | Operations to skip, by group |
| `preferences.nullableReferenceTypes` | Whether to add `#nullable enable` and nullable annotations |
| `preferences.generateXmlDocs` | Whether to add XML documentation from the specs' descriptions |

The configuration includes server-side groups such as `Admin`, `Economy`, `Multiplayer`,
`Groups`, and `CloudScript`, and excludes `Client`, `Server`, and `Matchmaker`.

## Use local specs

To generate from a local copy of the PlayFab specs, set `specSource.type` to `local`, and set
`specSource.path` to the directory of `{GroupName}.swagger.json` files:

```json
{
  "specSource": {
    "type": "local",
    "path": "D:\\path\\to\\API_Specs\\Swagger\\PlayFab"
  }
}
```

Then run the generator as usual.

## Overlay specs

Some PlayFab APIs are available before their specs are published to `PlayFab/API_Specs`. Their
specs are in `tools/PlayFabCodeGen/specs/`, which `specSource.overlayPath` names. A group with a
file there is generated from that file instead of `specSource`, and must still be listed in
`include`.

`Studio` is generated from an overlay. When its spec is published, delete the overlay file and
regenerate, so the group comes from `specSource` again.

## Generator source

| File | Purpose |
|------|---------|
| `Program.cs` | Loads the configuration, chooses the groups, and runs the parser and emitter |
| `SpecFetcher.cs` | Fetches specs from GitHub, or reads them from a directory or the overlay |
| `SwaggerParser.cs` | Parses Swagger 2.0 JSON into API groups, operations, and types |
| `CSharpEmitter.cs` | Writes the model files and `PlayFabApiMetadata.cs` |
| `CodeGenConfig.cs` | Defines the configuration model of `codegen-config.json` |
