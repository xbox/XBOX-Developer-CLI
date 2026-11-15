---
title: Building from source
description: Clone the repository and build the CLI with the .NET SDK.
---

# Building from source

Build the XBOX CLI yourself from a clone of the repository with the .NET SDK. Read
`CONTRIBUTING.md` at the root of the repository before you suggest a change.

## Before you begin

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or later.

## Restore and build

From the repository root:

```bash
dotnet restore
dotnet build
```

Builds treat warnings as errors, so a change must build without warnings.

## Run from source

Prefix commands with `dotnet run --project src/XboxDevCLI --`:

```bash
dotnet run --project src/XboxDevCLI -- auth login
dotnet run --project src/XboxDevCLI -- --playfab-title-id ABC123 playfab api /Admin/GetTitleData
```

## Run tests

```bash
dotnet test
```

See [Testing and coverage](testing.md) for the coverage bar and end-to-end tests, and
[Fuzz testing](fuzzing.md) for the property-based tests.

## Single-file publishing

Releases are single-file, self-contained executables for the runtime identifiers `win-x64`,
`linux-x64`, `osx-x64`, and `osx-arm64`. To publish one the same way, pass the runtime
identifier you want:

```bash
dotnet publish src/XboxDevCLI/XboxDevCLI.csproj --configuration Release \
  --runtime linux-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## PlayFab code generation

Don't edit the generated PlayFab models and API metadata in `src/XboxDevCLI/PlayFab/Generated/`
by hand. Regenerate them as [Code generation](code-generation.md) describes.
