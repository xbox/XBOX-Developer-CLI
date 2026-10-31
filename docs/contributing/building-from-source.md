---
title: Building from source
description: Clone the repository and build the CLI with the .NET SDK.
---

# Building from source

Prefer to build the XBOX Developer CLI yourself? Clone the repository and build it with the
.NET SDK.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or later.

The repository targets `net10.0`. `Directory.Build.props` enables nullable
reference types, implicit usings, latest C# language version, and
`TreatWarningsAsErrors=true`, so local builds must be warning-free.

## Restore and build

```bash
dotnet restore
dotnet build
```

## Run from source

When running from source, prefix commands with
`dotnet run --project src/XboxDevCLI --`:

```bash
dotnet run --project src/XboxDevCLI -- auth login
dotnet run --project src/XboxDevCLI -- --playfab-title-id ABC123 playfab api /Admin/GetTitleData
```

## Run tests

For the normal unit test pass:

```bash
dotnet test
```

For the coverage bar, fuzz testing, and deeper validation guidance, see
[Testing and coverage](testing.md) and [Fuzz Testing](fuzzing.md).

## Single-file publishing

Releases are single-file, self-contained executables for these runtime identifiers:

- `win-x64`
- `linux-x64`
- `osx-x64`
- `osx-arm64`

To publish one the same way, pass the runtime identifier you want:

```bash
dotnet publish src/XboxDevCLI/XboxDevCLI.csproj --configuration Release \
  --runtime linux-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Each release packages these as `xbox-<rid>.zip`. See
[Installation](../installation.md) for the downloadable archive names.

## PlayFab code generation

Generated PlayFab request/response models and endpoint metadata live under
`src/XboxDevCLI/PlayFab/Generated/`. If you need to update generated API data,
use the dedicated generator workflow documented in [Code Generation](code-generation.md)
rather than editing generated files by hand.

## Contributing

Please read `CONTRIBUTING.md` at the root of the repository
for the code of conduct and how to report issues and suggest changes.
