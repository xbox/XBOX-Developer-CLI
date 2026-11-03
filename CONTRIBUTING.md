# Contributing to the XBOX CLI

Thank you for your interest in the XBOX CLI. This page explains how to report problems
and suggest changes today, and how to build and change the CLI from source.

This project has adopted the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/). See [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) for the full text and resources.

## Issues

Issues are how to contribute right now. Use
[GitHub Issues](https://github.com/microsoft/XBOX-Developer-CLI/issues) to report bugs or
request features. If you know how you'd fix something, describe the change in the issue.

## Pull requests

We don't accept pull requests yet. If you open one, we'll close it and ask you to open an
issue instead. The CLA bot may still ask you to sign the CLA on it; you don't need to. We'll
start accepting them once we can review them promptly, and this page will explain how they
work.

When we do, most contributions will require you to agree to a Contributor License Agreement (CLA) declaring that you have the right to, and actually do, grant us the rights to use your contribution. For details, visit https://cla.opensource.microsoft.com.

When you submit a pull request, a CLA bot will automatically determine whether you need to provide a CLA and decorate the PR appropriately (e.g., status check, comment). Simply follow the instructions provided by the bot. You will only need to do this once across all repos using our CLA.

## Development

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), feature band
  `10.0.4xx` (`10.0.400` or a later patch in that band)

### Building

```bash
dotnet build
```

When running from source, prefix commands with `dotnet run --project src/XboxDevCLI --`:

```bash
dotnet run --project src/XboxDevCLI -- auth login
```

The installed executable name is defined once by `CliCommandName` in
`Directory.Build.props`. Runtime help and examples must use
`CliIdentity.CommandName`; release packaging resolves the same MSBuild property.
Keep the executable name independent from the `XboxDevCLI` identifier used in
namespaces, project paths, the configuration prefix, telemetry identity, and the
token cache, so changing the command doesn't break existing user state.

Use **XBOX** in all authored prose; never use "Xbox" as standalone brand copy.
Do not change established identifiers or generated PlayFab source to enforce
this copy convention.

### Testing

```bash
dotnet test
```

The end-to-end tests in `tests/XboxDevCLI.E2ETest/` skip until you set them up to run as your own
account; see
[End-to-end tests with your own account](docs/contributing/testing.md#end-to-end-tests-with-your-own-account).

### Command architecture

The command tree is split into small factory files, so adding a command does not mean editing one
large entrypoint.

- **Root composition** lives in [`src/XboxDevCLI/Program.cs`](src/XboxDevCLI/Program.cs): global
  options, configuration layering, the shared API invocation path, destructive-operation
  confirmation, and output/error formatting.
- **Top-level commands** (`auth`, `info`, `feedback`) are created in
  [`src/XboxDevCLI/Commands/`](src/XboxDevCLI/Commands/), one `*CommandFactory.cs` per command.
  `AuthCommandFactory` groups the neighboring `login`, `logout`, and `status` factories.
- **The `playfab` group** is assembled in
  [`src/XboxDevCLI/PlayFab/Commands/PlayFabCommandFactory.cs`](src/XboxDevCLI/PlayFab/Commands/PlayFabCommandFactory.cs),
  which composes one focused factory per command family (API and type metadata, MPS, file, group,
  Cloud Script, and so on). `CloudScriptCommandFactory` in turn composes the `classic` and
  `function` sub-groups.

The shape is deliberate: each family keeps its parsing, validation, and help text together;
contributors usually touch one file instead of competing over a central one; tests can target a
single factory surface; and the cross-cutting behavior that must stay consistent — auth,
configuration, output formatting, and the destructive gate — stays centralized in one invocation
path.

When adding a command:

1. Put it in the nearest command-family factory, or create a new factory file for a new family.
2. Wire it at one composition point — `Program.cs` for a top-level command, or
   `PlayFabCommandFactory.cs` for a `playfab` subgroup.
3. Route PlayFab calls through `Program.InvokeApiAsync` or a shared helper rather than
   constructing a client directly, so output formatting and the destructive gate still apply.
4. Verify the API path exists in
   [`PlayFabApiMetadata.cs`](src/XboxDevCLI/PlayFab/Generated/PlayFabApiMetadata.cs). Do not
   invent endpoints — a hallucinated path will fail only at runtime, against a real title.
5. Add or update tests under [`tests/XboxDevCLI.UnitTest`](tests/XboxDevCLI.UnitTest).
6. Update the docs, as described below.

### Documenting commands

The docs under `docs/` are hand-written Markdown. **Nothing generates the command
reference from the CLI**, so if you add or change a command, verb, or option, update its
page in the same pull request — otherwise the change ships undocumented.

- The pages follow the command tree: `xbox <command>` is documented in
  `docs/commands/<command>.md`, and `xbox playfab <service>` in
  `docs/commands/playfab/<service>.md`.
- New command group → give it its own page in the matching place, a row in its parent
  page's command table, and an entry in the tree in `docs/README.md`.
- New verb or option → extend that command's existing tables.
- Keep the existing shape: subcommand → PlayFab API path → description, then
  subcommand → options, then runnable examples.
- Explain non-obvious behavior (a required option, mutually exclusive flags, a
  deliberately omitted verb), not just the syntax.
- Say on the command's page whether it accepts `--output table`.
- If the change affects destructive behavior or confirmation, update
  `docs/safety.md` as well.
- Link between pages with relative file paths, such as `[Commands](commands.md)`.
  Front matter carries `title` and a one-sentence `description`, and nothing else, and
  the page's `#` heading matches its `title`. See
  [Editing these docs](docs/README.md#editing-these-docs).

### Code coverage

Coverage is measured with [Coverlet](https://github.com/coverlet-coverage/coverlet)
(`XPlat Code Coverage`) using [`coverage.runsettings`](coverage.runsettings), which emits a
Cobertura report and excludes checked-in generated sources — the PlayFab DTO models under
`src/XboxDevCLI/PlayFab/Generated/` and the Unicode width table under
`src/XboxDevCLI/Output/Generated/`.

```bash
dotnet test --collect:"XPlat Code Coverage" --settings coverage.runsettings
```

Changes keep coverage at or above these levels:

| Measure                        | Minimum |
|--------------------------------|---------|
| Overall project line coverage  | 70%     |
| Coverage of changed lines      | 80%     |

New code should ship with tests that keep coverage of the changed lines above the floor.
[Testing and coverage](docs/contributing/testing.md) shows how to check it locally.

### Fuzz testing

The repo includes a property-based fuzz suite (FsCheck). See [`docs/contributing/fuzzing.md`](docs/contributing/fuzzing.md) for what
it covers, how to run it locally, how to tune the fuzzing budget, and how to
reproduce a failure from its replay seed.

```bash
dotnet test tests/XboxDevCLI.FuzzTest/XboxDevCLI.FuzzTest.csproj
```

## PlayFab code generation

The [`tools/PlayFabCodeGen`](tools/PlayFabCodeGen) project generates the C# request/response
models and the endpoint metadata registry from PlayFab's Swagger 2.0 specs. The generated output
under `src/XboxDevCLI/PlayFab/Generated/` is checked in — regenerate it rather than editing it by
hand.

```bash
cd tools/PlayFabCodeGen
dotnet run -- codegen-config.json
```

This fetches specs from [PlayFab/API_Specs](https://github.com/PlayFab/API_Specs) and writes
per-group model files (`{GroupName}Models.cs`) plus `PlayFabApiMetadata.cs`, the registry of every
endpoint with its operation ID, path, description, and request/response type names. That registry
is what command wiring validates against, and what telemetry uses to recognize a known endpoint.

Regenerate when PlayFab changes its specs, when you change the include/exclude lists, or when you
modify the generator itself.

### Generator configuration

`tools/PlayFabCodeGen/codegen-config.json` controls the run:

| Key | Description |
|-----|-------------|
| `specSource.type` | `"github"` (fetch from the GitHub API) or `"local"` (read from a directory) |
| `specSource.repo` | Repository holding the specs (default: `PlayFab/API_Specs`) |
| `specSource.branch` | Branch to fetch from (default: `master`) |
| `specSource.path` | Path within the repo to the Swagger files |
| `output.directory` | Output directory, relative to the config file |
| `output.namespace` | Root namespace for the generated code |
| `include` | API groups to include (empty = all) |
| `exclude.groups` | API groups to exclude |
| `exclude.apis` | Per-group operation exclusions (`{ "GroupName": ["OpId1"] }`) |
| `preferences.nullableReferenceTypes` | Emit `#nullable enable` and nullable annotations |
| `preferences.generateXmlDocs` | Emit XML doc comments from the spec descriptions |

Note that `exclude.groups` currently omits `Client`, `Server`, and `Matchmaker`, so those paths
are absent from the metadata and cannot be called without regenerating.

To work against a local checkout of the specs:

```json
{ "specSource": { "type": "local", "path": "/path/to/API_Specs/Swagger/PlayFab" } }
```

### Generator internals

| File | Purpose |
|------|---------|
| `Program.cs` | Entry point: loads config, fetches specs, runs the parser and emitter |
| `SwaggerParser.cs` | Parses Swagger 2.0 JSON into an intermediate representation |
| `CSharpEmitter.cs` | Transforms that representation into C# source files |
| `SpecFetcher.cs` | Fetches specs from GitHub or reads them from disk |
| `CodeGenConfig.cs` | Config model for `codegen-config.json` |

## Unicode width code generation

`--output table` sizes and pads columns in terminal cells, which means it needs the East Asian
Width property to know that a CJK character occupies two of them. .NET exposes no API for that
property, so the [`tools/UnicodeWidthCodeGen`](tools/UnicodeWidthCodeGen) project transcribes the
Wide and Fullwidth ranges from the Unicode Character Database into
`src/XboxDevCLI/Output/Generated/EastAsianWidthTable.cs`. That output is checked in — regenerate
it rather than editing it by hand.

```bash
dotnet run --project tools/UnicodeWidthCodeGen
```

The Unicode release is pinned in the generator rather than tracking `latest`, so a regeneration
is reproducible and a version bump is a reviewable diff. To move to a new release, or to work
from a local copy of the file:

```bash
dotnet run --project tools/UnicodeWidthCodeGen -- --unicode-version 18.0.0
dotnet run --project tools/UnicodeWidthCodeGen -- --input ./EastAsianWidth.txt
```

Everything else about a character's width — whether it is a combining mark, a spacing mark, a
joiner, or a grapheme cluster boundary — comes from .NET's own Unicode data at runtime, so this
table is the only thing that has to be regenerated. That data tracks the runtime's Unicode
version rather than this table's, and is normally a release or two behind it. The two are pinned
separately on purpose: East Asian Width is the one property .NET does not expose, and a character
assigned since the runtime's release is better measured from newer width data than left to
default to narrow.
