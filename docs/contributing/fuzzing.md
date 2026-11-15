---
title: Fuzz testing
description: Run and tune the property-based fuzz tests that feed randomized input to the CLI's parsing code.
---

# Fuzz testing

The XBOX CLI's fuzz tests feed large volumes of randomized, adversarial input to the
code that parses and transforms untrusted input, and check rules that must hold for every
input, such as "this function never throws". They use
[FsCheck](https://fscheck.github.io/FsCheck/) with `FsCheck.Xunit`, and run with `dotnet test`
like the unit tests in `tests/XboxDevCLI.UnitTest/`.

## Where the tests live

The project is `tests/XboxDevCLI.FuzzTest/`:

| File | Holds |
| --- | --- |
| `FuzzArbitraries.cs` | Generators of adversarial input, and their wrapper types |
| `FuzzConfig.cs`, `FuzzPropertyAttribute.cs` | The number of cases per property, and the `[FuzzProperty]` attribute |
| `DestructiveOperationsFuzzTests.cs`, `ParsingFuzzTests.cs`, `FormattingFuzzTests.cs`, `ConfigurationFuzzTests.cs` | The properties, one file per target |

The [coverage bar](testing.md#coverage-bar) is measured from the unit tests only, so fuzz tests
don't count toward it.

## Fuzz targets

| Target | Code under test | Key invariant |
| --- | --- | --- |
| Destructive-operation check | `Safety/DestructiveOperations.cs` | Never throws. `Delete*` and `Shutdown*` stay destructive despite path tricks such as backslashes, percent-encoding, Unicode, and query strings or fragments; `Get`, `List`, and `Search` never are. This is the check behind `--confirm`. |
| Command parsing | `Program.BuildRootCommand().Parse(argv)` | Parsing any arguments never throws; errors come back in the `ParseResult`. |
| Option-value parsing | `PlayFabCommandHelper.ParseCsv` and `TryParseEnum` | Never throw; entries are trimmed and non-empty; enum values round-trip. |
| Output formatting | `PlayFabApiClient.FormatJson`, `Program.FormatOutput`, and `FormatErrorOutput` | Never throw on any input; invalid JSON comes back unchanged; formatting twice gives the same result. |
| Configuration layering | `Program.LoadConfiguration` | Never throws; accepted command-line overrides reach the resolved configuration. |

## Run the fuzz tests

Run the whole suite:

```bash
dotnet test tests/XboxDevCLI.FuzzTest/XboxDevCLI.FuzzTest.csproj
```

Run one target while you work on it:

```bash
dotnet test tests/XboxDevCLI.FuzzTest/XboxDevCLI.FuzzTest.csproj \
  --filter "FullyQualifiedName~DestructiveOperationsFuzzTests"
```

Each property runs `FUZZ_ITERATIONS` generated cases, 500 by default. Raise it to fuzz harder,
such as before a release. In bash or zsh:

```bash
FUZZ_ITERATIONS=50000 dotnet test tests/XboxDevCLI.FuzzTest/XboxDevCLI.FuzzTest.csproj
```

In PowerShell:

```powershell
$env:FUZZ_ITERATIONS = "50000"; dotnet test tests/XboxDevCLI.FuzzTest/XboxDevCLI.FuzzTest.csproj
```

## Reproduce a failure

When a property fails, FsCheck prints the smallest failing input it found and a replay seed:

```text
Falsifiable, after 31 tests (0 shrinks) (17350845101867737935,11032698459973388473).
Replay directly at failing step with (5357314801272863454,5597035677237889357,7).
Original:
JsonFragment { Value = ... }
```

To rerun the property with that seed, set `Replay` on its attribute, such as
`[FuzzProperty(Replay = "5357314801272863454,5597035677237889357")]`, and run only that test.

When a property finds a real defect, fix the product code rather than weakening the property.

## Add a fuzz target

1. Pick a function, ideally without side effects, that processes untrusted or external input.
2. If it needs input of a particular shape, add a generator and wrapper type in
   `FuzzArbitraries.cs`.
3. Add a `[FuzzProperty]` method that returns `bool`, or throws on failure, and checks a rule
   that must hold for every input.
4. Run it with a high `FUZZ_ITERATIONS`, and fix any real defects it finds.
