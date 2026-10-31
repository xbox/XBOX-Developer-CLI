---
title: Testing and coverage
description: Build and test the CLI, and measure its code coverage against the project's bar.
---

# Testing and coverage

The XBOX Developer CLI targets **.NET 10** and keeps builds warning-free. `Directory.Build.props`
sets `Nullable=enable`, `ImplicitUsings=enable`, and
`TreatWarningsAsErrors=true`, so warnings fail the build.

## Build and test locally

Use the repository root as the working directory:

```bash
dotnet build
dotnet test
```

The unit tests are xUnit tests under `tests/XboxDevCLI.UnitTest/`. The offline tests
in `tests/XboxDevCLI.EndToEndTests/` run too; its live tests, which sign in as a service
principal and call real services, skip unless `E2E_GITHUB_ENVIRONMENT` is set.

Tests that redirect `Console.Out` or `Console.Error` must join the
`[Collection("ConsoleRedirect")]` xUnit collection. Console streams are
process-global, so these tests must not run in parallel with other console
redirecting tests.

## Live end-to-end tests

The end-to-end harness tests a separately published CLI executable through child processes.
Its offline tests use fictitious configuration; no deployment profiles ship with the source.
Live configuration comes from environment variables, typically supplied by a GitHub Actions
job bound to an environment:

| Variable | Meaning |
|----------|---------|
| `E2E_GITHUB_ENVIRONMENT` | Selected environment name and live opt-in. Leave unset for offline tests. |
| `E2E_CLI_PATH` | Directory containing the published `xbox` executable and `appsettings.json`. |
| `XBOX_AUTHENTICATION__CLIENTID` | Service principal's app ID. |
| `XBOX_AUTHENTICATION__TENANTID` | Service principal's tenant ID. |
| `XBOX_AZUREAD__SCOPES` | Optional API scope override. Unset or empty preserves the candidate's `AzureAd:Scopes`; the effective scope must equal the expected audience followed by `/.default`. |
| `E2E_EXPECTED_AUDIENCE` | Expected API access-token audience, not the OIDC assertion audience. |
| `E2E_ALLOWED_HOST_SUFFIX` | One explicit DNS domain suffix, such as `nodpublishers.com`; both gateways must be subdomains. |
| `E2E_PLAYFAB_BASE_URL_TEMPLATE` | PlayFab HTTPS origin, optionally containing `{0}` for the title ID. |
| `E2E_PLAYFAB_TITLE_ID` | PlayFab title containing the read-only fixture. |
| `E2E_XBOX_PARTNER_BASE_URL` | XBOX partner HTTPS origin. |
| `E2E_SELLER_ID` | Seller used by the game-list scenario. |

Live jobs need `id-token: write`; GitHub supplies `ACTIONS_ID_TOKEN_REQUEST_URL` and
`ACTIONS_ID_TOKEN_REQUEST_TOKEN`. Missing or inconsistent configuration fails an opted-in run
instead of silently skipping it. `LiveConfigurationTests` validates these settings, including
the effective scope from the candidate's `appsettings.json` when no override is supplied,
without acquiring a token or calling a service. A missing or mismatched candidate scope
fails preflight.

PlayFab and XBOX partner scenarios are currently disabled in code because the services do
not yet authorize service principals. There is no variable to enable them. Only configuration
validation and `auth status` run when opted in. The title-data sentinel is fixed to key `foo`
and value `bar`.

The harness validates scope/audience consistency, endpoint origins, host suffixes, and fixture
identifiers. The environment configuration is trusted policy,
not inferred from the endpoints. Protect access to that configuration and to jobs using its
identity.

Each CLI invocation uses a private copy of its configuration and strips inherited `XBOX_*`
and `E2E_*` settings. It sets automation authentication explicitly and disables telemetry.
Temporary home/data directories isolate saved settings on Linux and macOS; Windows does not
support redirecting the CLI's data directory this way.

### Run in your own fork

The harness ships with the source and does not depend on a particular repository or
deployment. To run it against your own environment:

1. Create a GitHub environment in your fork, such as `e2e-nod-arena`. Configure deployment
   branch restrictions and required reviewers before enabling access to its identity.
2. Follow [GitHub Actions authentication setup](../authentication.md#set-up-github-actions)
   to configure an Entra app registration and a federated identity credential for your
   fork's environment. Use the actual OIDC subject issued for that repository and
   environment, not another repository's credential. Grant only the API permissions needed
   for the scenarios you intend to run.
3. Set the identity and deployment variables from the table above as environment
   **variables**. All listed variables are required except `XBOX_AZUREAD__SCOPES`,
   even while service scenarios are disabled. The endpoints
   must be HTTPS origins with no path, trailing slash, credentials, query, fragment, or
   non-default port, and both must be subdomains of `E2E_ALLOWED_HOST_SUFFIX`.
4. Copy the [manual workflow example](examples/e2e.yml) to `.github/workflows/e2e.yml`
   in your fork. Commit it to your default branch so GitHub enables manual dispatch.
   In **Actions**, select **E2E (manual)**, choose a trusted branch, and enter the exact
   environment name. Approve the deployment if your environment requires it.

The example builds the CLI and harness and runs offline checks in an unprivileged job.
The environment-bound job downloads that run's artifact by ID, validates configuration,
and runs live tests without checking out source, restoring packages, or building code.
It supplies `E2E_GITHUB_ENVIRONMENT` and `E2E_CLI_PATH`; GitHub supplies the OIDC request
variables. Do not configure those four variables yourself.

Approving a run trusts both the harness and the CLI executable with the environment's
identity. Review both before dispatching; the build/live split does not make an untrusted
artifact safe. Configuration validation acquires no token, but the live job already has
OIDC request permission. The example runs only on manual dispatch, not on pushes or pull
requests, and does not provision titles, sellers, permissions, or fixtures. Currently only
configuration validation and the authentication scenario run; the four service scenarios
remain disabled in code.

## Code coverage

Coverage is measured with Coverlet's `XPlat Code Coverage` collector and the
repository's `coverage.runsettings` file:

```bash
dotnet test --collect:"XPlat Code Coverage" --settings coverage.runsettings
```

The runsettings file emits Cobertura output and excludes generated PlayFab DTOs
under `src/XboxDevCLI/PlayFab/Generated/` so coverage reflects hand-written code.

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

Releases are single-file executables, so code must work when published that way. Do not use
`Assembly.Location` for runtime file discovery. It returns an empty string in single-file
apps, and a single-file publish fails on the IL3000 warning it raises. Use
`AppContext.BaseDirectory` instead. [Building from source](building-from-source.md#single-file-publishing)
shows how to publish one.

## Fuzz testing

Property-based tests live in the fuzz test project. See [Fuzz Testing](fuzzing.md) for the fuzzing targets, iteration budget,
and failure reproduction workflow.
