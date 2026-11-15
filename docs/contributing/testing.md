---
title: Testing and coverage
description: Build and test the CLI, measure its code coverage against the project's bar, and run its end-to-end tests.
---

# Testing and coverage

Run the XBOX CLI's tests before you suggest a change, and keep its code coverage at
or above the [coverage bar](#coverage-bar). To set up a build, see
[Building from source](building-from-source.md).

## Build and test locally

From the repository root:

```bash
dotnet build
dotnet test
```

This runs the xUnit unit tests in `tests/XboxDevCLI.UnitTest/`, the [fuzz tests](fuzzing.md),
and the offline tests of the end-to-end harness in `tests/XboxDevCLI.EndToEndTests/`. The
harness's
[live tests](#live-end-to-end-tests) and the
[tests that run as your own account](#end-to-end-tests-with-your-own-account) skip until you
set them up.

Tests that redirect `Console.Out` or `Console.Error` must join the
`[Collection("ConsoleRedirect")]` xUnit collection, because console streams are shared by the
whole process and these tests can't run in parallel.

## Live end-to-end tests

The end-to-end harness runs a published CLI executable as a service principal against real
services. Its live tests skip unless `E2E_GITHUB_ENVIRONMENT` is set, and they're meant to run
in a GitHub Actions job bound to an environment that supplies these variables:

| Variable | Meaning |
|----------|---------|
| `E2E_GITHUB_ENVIRONMENT` | The environment's name. Setting it opts in to the live tests. |
| `E2E_CLI_PATH` | Directory holding the published `xbox` executable and `candidate-defaults.json`, a copy of the same commit's `src/XboxDevCLI/Resources/defaults.json`. |
| `XBOX_AUTHENTICATION__CLIENTID` | The service principal's app ID. |
| `XBOX_AUTHENTICATION__TENANTID` | The service principal's tenant ID. |
| `XBOX_AZUREAD__SCOPES` | Optional. Overrides the CLI's built-in `AzureAd:Scopes`; the scope in effect must be the expected audience followed by `/.default`. |
| `E2E_EXPECTED_AUDIENCE` | The expected audience of the API access token, not of the OpenID Connect (OIDC) assertion. |
| `E2E_ALLOWED_HOST_SUFFIX` | One DNS domain suffix, such as `nodpublishers.com`, that both endpoints must be subdomains of. |
| `E2E_PLAYFAB_BASE_URL_TEMPLATE` | The PlayFab HTTPS origin, optionally containing `{0}` for the title ID. |
| `E2E_PLAYFAB_TITLE_ID` | The PlayFab title that holds the read-only fixture. |
| `E2E_XBOX_PARTNER_BASE_URL` | The HTTPS origin for commands such as `xbox game` and `xbox seller`, which the `XboxPartner:BaseUrl` setting holds. |
| `E2E_SELLER_ID` | The seller for the game-list scenario. |

Live jobs need the `id-token: write` permission. When the live tests are opted in, missing or
inconsistent configuration fails the run rather than skipping it. The harness checks the
variables before it signs in, and passes them to the CLI as `XBOX_` variables; it doesn't pass
on other `XBOX_*` or `E2E_*` variables from your environment, and it turns off telemetry.

Two PlayFab scenarios run, `xbox playfab api /Admin/GetTitleData` and
`xbox playfab title-data get foo`. Both read `E2E_PLAYFAB_TITLE_ID`, so before a run, that title
needs:

- Title data with key `foo` and string value `bar`.
- A grant of title **Admin** to the environment's service principal, made by a studio
  administrator with
  [`xbox playfab service-principal set`](../commands/playfab/service-principal.md). Grant
  nothing at studio level and no other title.

The `seller list` and `game list` scenarios are turned off in code, because the service those
commands call doesn't accept service principals yet.

The environment's configuration decides which endpoints and identity the tests use, so
protect access to it and to the jobs that use its identity.

### Run in your own fork

1. Create a GitHub environment in your fork, such as `e2e-nod-arena`. Set its deployment
   branch restrictions and required reviewers before you give it access to an identity.
2. Follow [GitHub Actions authentication setup](../authentication.md#set-up-github-actions)
   to set up a Microsoft Entra ID app registration and a federated identity credential for
   your fork's environment. Use the OIDC subject issued for your repository and environment,
   and grant only the API permissions the scenarios need.
3. Set every variable in the table except `E2E_GITHUB_ENVIRONMENT` and `E2E_CLI_PATH` as
   environment **variables**. Only `XBOX_AZUREAD__SCOPES` is optional, even though the `seller list`
   and `game list` scenarios are turned off. Each endpoint must be an HTTPS origin with no path,
   trailing slash, credentials, query, fragment, or nondefault port. Prepare the title's `foo`
   fixture and the service principal's grant as described earlier.
4. Copy the [manual workflow example](examples/e2e.yml) to `.github/workflows/e2e.yml`
   in your fork, and commit it to your default branch so GitHub enables manual dispatch.
5. In **Actions**, select **E2E (manual)**, choose a trusted branch, and enter the exact
   environment name. Approve the deployment if your environment requires it.

The workflow builds the CLI and harness in a job without the identity, and the job bound to
the environment runs the live tests on that build. Approving a run trusts both the harness and
the CLI executable with the environment's identity, so review both before you approve it.

## End-to-end tests with your own account

The end-to-end tests in `tests/XboxDevCLI.E2ETest/` run an `xbox` executable against production
Partner Center and XBOX services, as the user account it's signed in with rather than a service
principal. They run only commands that read, and `xbl config save` and `xbl config publish` with
`--validate-only`, which XBOX services validate without storing anything. Until their settings are
set, `dotnet test` skips them.

To run them with your own account:

1. Build the CLI, and sign in with the executable you'll test:

   ```bash
   dotnet build
   export XBOXDEVCLI_E2E_CLI="$PWD/src/XboxDevCLI/bin/Debug/net10.0/xbox"
   "$XBOXDEVCLI_E2E_CLI" auth login
   ```

2. Set the other `XBOXDEVCLI_E2E_*` environment variables that
   `tests/XboxDevCLI.E2ETest/E2ESettings.cs` lists. They name the account you signed in with; a
   seller, game, development sandbox, achievement image, and test account batch that the tests
   read, so each must already exist; and a keyword that, among the seller's games, only that game's
   name contains.
3. Run the tests:

   ```bash
   dotnet test tests/XboxDevCLI.E2ETest
   ```

Two of the tests read every page of the seller's games, so they take longer for a seller with many
games. To make missing settings fail the tests instead of skipping them, set
`XBOXDEVCLI_E2E_REQUIRED=true`.

## Code coverage

Measure coverage with the repository's `coverage.runsettings`, which leaves out the generated
code under `src/XboxDevCLI/PlayFab/Generated/`:

```bash
dotnet test --collect:"XPlat Code Coverage" --settings coverage.runsettings
```

## Coverage bar

Changes keep coverage at or above these levels:

| Measure | Minimum |
|---------|---------|
| Overall project line coverage | 70% |
| Coverage of changed lines | 80% |

To check the lines you changed, run the unit tests with coverage, then compare the report
with `main` using [diff-cover](https://github.com/Bachmann1234/diff_cover):

```bash
dotnet test tests/XboxDevCLI.UnitTest/XboxDevCLI.UnitTest.csproj \
  --collect:"XPlat Code Coverage" --settings coverage.runsettings --results-directory TestResults
pip install diff-cover
diff-cover "$(find TestResults -name coverage.cobertura.xml | head -n 1)" \
  --compare-branch origin/main --fail-under 80
```

## Single-file publish safety

Releases are single-file executables, so don't use `Assembly.Location` to find files at run
time. It returns an empty string in a single-file app, and the publish fails on the IL3000
warning it raises. Use `AppContext.BaseDirectory` instead.
[Building from source](building-from-source.md#single-file-publishing) shows how to publish one.

## Fuzz testing

The property-based fuzz tests are in `tests/XboxDevCLI.FuzzTest/`. See
[Fuzz testing](fuzzing.md).
