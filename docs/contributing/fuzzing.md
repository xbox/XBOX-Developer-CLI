---
title: Fuzz testing
description: Run and tune the property-based fuzz tests that feed randomized input to the CLI's parsing code.
---

# Fuzz testing

The XBOX Developer CLI runs **property-based fuzz tests** to harden how it handles untrusted
input. The suite feeds large volumes of randomized, adversarial input into
the CLI's parsing and data-transformation code and asserts invariants that must
hold for *every* input (for example, "this function never throws" or "malformed
JSON is echoed back unchanged"). This complements the example-based unit tests in
`tests/XboxDevCLI.UnitTest/`, which check specific known cases.

## Framework

- **[FsCheck](https://fscheck.github.io/FsCheck/)** with **`FsCheck.Xunit`** —
  property-based testing integrated with the existing xUnit runner. It is
  cross-platform, has no native dependencies, and runs in-process, so it fits the
  project's .NET 10 and xUnit tests without extra tooling.

Coverage-guided fuzzers (e.g. SharpFuzz/libFuzzer) were evaluated, but FsCheck was chosen:
the CLI's attack surface is mostly pure string/JSON transformation, which
property-based testing exercises effectively while staying fast, deterministic to
reproduce, and portable across Windows/Linux/macOS.

## Where it lives

- Project: `tests/XboxDevCLI.FuzzTest/`
- Adversarial generators and input wrapper types: `FuzzArbitraries.cs`
- Iteration budget + `[FuzzProperty]` attribute: `FuzzConfig.cs`,
  `FuzzPropertyAttribute.cs`
- Properties (one file per target): `DestructiveOperationsFuzzTests.cs`,
  `ParsingFuzzTests.cs`, `FormattingFuzzTests.cs`, `ConfigurationFuzzTests.cs`

The fuzz project is intentionally **separate** from `XboxDevCLI.UnitTest` and is
**excluded from coverage measurement** so its randomized runs never affect
coverage numbers.

## Fuzz targets

| Target | Code under test | Key invariant |
| --- | --- | --- |
| Destructive-op gate | `Safety/DestructiveOperations.cs` | Never throws; `Delete*`/`Shutdown*` stay classified destructive under path-normalization tricks (backslashes, percent-encoding, unicode, query/fragment); `Get`/`List`/`Search` are never gated. **Highest priority — this guards `--confirm`.** |
| Command parsing | `Program.BuildRootCommand().Parse(argv)` | Parsing arbitrary argv never throws; errors surface via `ParseResult`. |
| Option-value parsing | `PlayFabCommandHelper.ParseCsv` / `TryParseEnum` | Never throw; produced entries are non-empty and trimmed; enum results round-trip. |
| Output formatting | `PlayFabApiClient.FormatJson`, `Program.FormatOutput` / `FormatErrorOutput` | Never throw on arbitrary/malformed JSON; invalid input is returned unchanged; formatting is idempotent. |
| Configuration layering | `Program.LoadConfiguration` | Never throws; accepted CLI overrides round-trip into the resolved configuration. |

## Running locally

Run the whole suite:

```bash
dotnet test tests/XboxDevCLI.FuzzTest/XboxDevCLI.FuzzTest.csproj
```

Run a single target while iterating:

```bash
dotnet test tests/XboxDevCLI.FuzzTest/XboxDevCLI.FuzzTest.csproj \
  --filter "FullyQualifiedName~DestructiveOperationsFuzzTests"
```

### Tuning the fuzzing budget

Each property runs `FUZZ_ITERATIONS` generated cases (default **500**). Raise it to fuzz harder before a release:

```bash
# PowerShell
$env:FUZZ_ITERATIONS = "50000"; dotnet test tests/XboxDevCLI.FuzzTest/XboxDevCLI.FuzzTest.csproj

# bash / zsh
FUZZ_ITERATIONS=50000 dotnet test tests/XboxDevCLI.FuzzTest/XboxDevCLI.FuzzTest.csproj
```

## Reproducing a failure

On failure FsCheck prints the shrunk counterexample **and** a replay seed, e.g.:

```text
Falsifiable, after 31 tests (0 shrinks) (17350845101867737935,11032698459973388473).
Replay directly at failing step with (5357314801272863454,5597035677237889357,7).
Original:
JsonFragment { Value = ... }
```

To re-run a specific property against that exact seed, set the seed on the
`[FuzzProperty]` (or a temporary `[Property]`) attribute via its `Replay`
property, for example `[FuzzProperty(Replay = "5357314801272863454,5597035677237889357")]`,
then run just that test.

When a property surfaces a genuine defect, **fix the product code** rather than
weakening the property. (The initial suite already caught and fixed three real
robustness bugs where `JsonDocument.Parse` throws `ArgumentException` — not the
expected `JsonException` — on strings containing an unpaired UTF-16 surrogate.)

## Adding a new target

1. Pick a pure or near-pure function that processes untrusted/external input.
2. If it needs a specialized input shape, add a generator + wrapper type in
   `FuzzArbitraries.cs`.
3. Add a `[FuzzProperty]` method that returns `bool` (or throws on failure) and
   asserts an invariant that must hold for every input.
4. Run locally with a high `FUZZ_ITERATIONS`, fix any real defects found, and
   commit.
