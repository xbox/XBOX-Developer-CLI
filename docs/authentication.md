---
title: Authentication
description: Sign in to the XBOX Developer CLI interactively or as a service principal, manage cached credentials, and meet PlayFab's permission requirements.
---

# Authentication

The XBOX Developer CLI signs you in with your developer account to authenticate
service requests.
For Partner Center workflows, sign in with the account that has access to your
intended Partner Center seller account. See [Getting started](getting-started.md)
for choosing a seller and listing its games.
For `xbox playfab` commands, also see
[How PlayFab authentication works](#how-playfab-authentication-works).
Unattended jobs can authenticate as a service principal instead; see
[Automation (service principal) authentication](#automation-service-principal-authentication).

## Sign in

```bash
xbox auth login
```

`auth login` opens your system browser to complete an interactive sign-in with
your developer account. After a successful sign-in, the CLI reports the
signed-in account and token expiration:

```text
Successfully signed in as: dev@nodpublishers.com
Token expires at: YYYY-MM-DD HH:MM:SS
```

The CLI uses one account at a time. To switch accounts, run `xbox auth login`
again and choose the intended account in the browser. `auth login` signs out any
other cached account, so later commands, including in other terminals for your OS
user, use the account you choose. See [Auth](commands/auth.md).

## Check status

```bash
xbox auth status
```

`auth status` reports your cached account and token details when available.
It can contact Microsoft Entra ID to refresh a token, but does not verify service
access:

```text
Signed in as: dev@nodpublishers.com
Home Account ID: 00000000-0000-0000-0000-000000000000.00000000-0000-0000-0000-000000000000
Environment: login.microsoftonline.com
Tenant ID: 00000000-0000-0000-0000-000000000000
Token type: Bearer
Token expires at: YYYY-MM-DD HH:MM:SS
Scopes: <scope>/.default
```

Pass `--output json` or `--output json-compact` to emit machine-readable status.
In JSON mode, `auth status` exits successfully with `"signedIn": false` when no cached
account is available, so automation can probe state without treating that as a
crash. A configuration, sign-in setting, or token cache that can't be read isn't
reported as signed out; it exits 1 with the error JSON on stderr. This status output
applies only to user sign-in; see
[Confirm the identity](#confirm-the-identity) for service-principal modes.

Saved defaults, such as the seller or PlayFab title, are separate from sign-in.
See [Context](commands/context.md) to view and manage them.

## Sign out

```bash
xbox auth logout
```

`auth logout` removes cached accounts and clears the persisted MSAL
token cache for the CLI. This affects other CLI sessions for your
OS user but does not delete saved context. If no account is cached, it prints that
no account is currently signed in and exits successfully. See
[Auth](commands/auth.md) for the full command reference.

## Token cache

Commands reuse and refresh your cached token automatically; run `xbox auth login`
again only if the CLI asks you to.

The CLI uses MSAL's cross-platform token cache support:

- Windows uses the platform-protected MSAL cache behavior.
- macOS uses Keychain.
- Linux uses Secret Service/keyring when available.
- Headless Linux environments without a usable keyring fall back to an
  unencrypted cache file under the user's local application data directory,
  protected only by filesystem permissions.

## How PlayFab authentication works

This section applies only to `xbox playfab` commands. PlayFab checks its own
permissions on each title, separately from your Partner Center access.

When you call a PlayFab API, the CLI silently acquires a Microsoft Entra access
token and sends it in the `Authorization: Bearer <access-token>` header. No
`X-SecretKey` or `X-EntityToken` headers are used.

PlayFab validates the Entra token and checks that the developer has **Admin**
permission on the target title.

This means:

- You never handle or store a PlayFab title secret key.
- Access is governed by your developer account's permissions on the title.
- The signed-in developer must have **Admin** permission on the target title for
  `playfab api` and higher-level PlayFab commands to succeed.

See [PlayFab API integration](playfab-api.md) for selecting a title and calling
PlayFab APIs.

## Overriding Entra ID settings

The Entra ID application used for sign-in is built into the CLI (the `AzureAd`
settings). You can override individual values with `XBOX_AZUREAD__` environment
variables, such as `XBOX_AZUREAD__CLIENTID`, or per invocation with global options:

| Option | Overrides |
|--------|-----------|
| `--client-id` | `AzureAd:ClientId` for user sign-in; refused in service-principal modes |
| `--tenant-id` | `AzureAd:TenantId` for user sign-in; refused in service-principal modes |
| `--scopes` | `AzureAd:Scopes` |

These are advanced options — most users never need to set them. See
[Configuration](configuration.md) for the full configuration model.

## Automation (service principal) authentication

CI pipelines and other unattended jobs can't complete a browser sign-in. Instead,
they set `XBOX_AUTHENTICATION__MODE` to a service-principal mode. Each
command then acquires an app-only Microsoft Entra token for PlayFab and the XBOX partner
services (`AzureAd:Scopes`) as the configured app registration or managed identity, in that
identity's own tenant. Nothing is written to the token cache, and `auth login` and
`auth logout` don't apply.

Every service-principal mode requires an app-only token with tenant, object ID, and
application ID claims. The first accepted token pins those IDs for the invocation;
later tokens identifying a different principal are refused before use. Tokens with
missing identity claims are also refused.

A service principal has no saved context. `--profile`, `XBOX_PROFILE`, and the `xbox
context` commands that read or change saved values are refused, and a value that would
come from saved context must instead be passed with its flag or `XBOX_*` variable, such as
`--playfab-title-id` or `XBOX_PLAYFAB_TITLE_ID`. `xbox context key list` still works.

> PlayFab and the XBOX partner services must accept the service principal before
> calls succeed. Until a service grants access to it, calls fail with an
> authorization error even though `auth status` reports a valid token.

| Mode | Credential | Settings (each prefixed with `XBOX_AUTHENTICATION__`) |
|------|------------|-------------------------------------------------------------|
| `user` (default) | Interactive sign-in with `auth login`. | None. |
| `clientsecret` | Client secret read from a file. | `TENANTID`, `CLIENTID`, `CLIENTSECRET__FILE` |
| `certificate` | PKCS#12 (`.pfx`) certificate with its private key. | `TENANTID`, `CLIENTID`, `CERTIFICATE__PATH`, optional `CERTIFICATE__PASSWORDFILE` |
| `federatedtoken` | A federated identity token (for example from Azure Pipelines or Kubernetes) read from a file whenever a fresh access token is requested, so a rotated token is picked up on refresh. | `TENANTID`, `CLIENTID`, `FEDERATEDTOKEN__FILE` |
| `githuboidc` | The GitHub Actions OIDC token, requested with audience `api://AzureADTokenExchange`. The job needs `permissions: id-token: write`. | `TENANTID`, `CLIENTID` |
| `azurecli` | The service principal or managed identity signed in to the Azure CLI. A token for a user signed in to the Azure CLI is refused; a person signs in with `auth login`. | None. |
| `managedidentity` | The Azure managed identity of the host. Omit the client ID for a system-assigned identity. | Optional `CLIENTID` |

`XBOX_AUTHENTICATION__TENANTID` and `XBOX_AUTHENTICATION__CLIENTID` are shared by every
mode that takes them, so switching modes changes only `MODE` and the credential setting.
The mode name and the setting names are case-insensitive. Authentication settings and
GitHub token-request inputs whose variable names differ only by case must have identical
values; conflicting duplicates are rejected without printing their values.
Tenant IDs must be a tenant GUID or verified domain;
`common`, `organizations`, and `consumers` are rejected because an app-only token
needs a specific tenant. Client IDs must be GUIDs.

`--client-id` and `--tenant-id` configure only user sign-in and are refused in every
service-principal mode, not silently ignored, by every command that signs in. Help and
commands that run offline, such as `xbox info`, ignore them. For an app registration, set
`XBOX_AUTHENTICATION__CLIENTID` and `XBOX_AUTHENTICATION__TENANTID` instead. For
`managedidentity`, only `XBOX_AUTHENTICATION__CLIENTID` applies (optional for a
user-assigned identity); the identity determines the tenant. For `azurecli`, select the
identity by signing in to the Azure CLI. `--scopes` still sets the requested API scope
in every mode.

`managedidentity` works only on Azure-hosted workloads with a managed identity enabled.
Token acquisition has a 10-second timeout. If the managed-identity endpoint is
unavailable or rejects the request, the error explains the host requirement.

These settings are read **only from environment variables**, never from the
built-in defaults or CLI options. They are read once per command and validated when
the command first needs to authenticate; a command that never authenticates, such as
`context key list`, doesn't validate them. The CLI fails rather than guess when:

- the mode is unknown, or a required setting is missing;
- a setting doesn't apply to the selected mode, such as `CLIENTID` with `azurecli`,
  `TENANTID` with `managedidentity`, or any service-principal setting while the mode is
  `user` or unset;
- a setting under `XBOX_AUTHENTICATION__` isn't recognized;
- a credential file doesn't exist, is empty, or can't be read.

Credential files are read with surrounding whitespace trimmed, except the certificate
password file: a password is used exactly as written, minus one trailing line ending.
MSAL reuses access tokens in memory until they need refreshing. A cached access token
doesn't reread the federated token file or request another GitHub Actions OIDC token.

An empty variable counts as unset. The CLI never falls back to another mode or to
a cached user sign-in, so a misconfigured pipeline fails instead of running as the
wrong identity. Errors name the variable, never a secret or file contents.

Secrets are always passed in files, never in environment variables or arguments,
which leak more easily through process listings and logs. Restrict each file to
the account that runs the CLI, and delete it when the job ends.

### Examples

A client secret on Linux or macOS:

```bash
export XBOX_AUTHENTICATION__MODE=clientsecret
export XBOX_AUTHENTICATION__TENANTID=00000000-0000-0000-0000-000000000000
export XBOX_AUTHENTICATION__CLIENTID=11111111-1111-1111-1111-111111111111
export XBOX_AUTHENTICATION__CLIENTSECRET__FILE="$RUNNER_TEMP/xbox-secret"
xbox auth status
```

A GitHub Actions job with workload identity federation; see
[Set up GitHub Actions](#set-up-github-actions) for the Entra ID and GitHub setup:

```yaml
jobs:
  example:
    runs-on: ubuntu-latest
    environment: production
    permissions:
      id-token: write
      contents: read
    steps:
      - run: xbox --playfab-title-id ABC123 playfab api /Admin/GetTitleData
        env:
          XBOX_AUTHENTICATION__MODE: githuboidc
          XBOX_AUTHENTICATION__TENANTID: ${{ vars.ENTRA_TENANT_ID }}
          XBOX_AUTHENTICATION__CLIENTID: ${{ vars.ENTRA_CLIENT_ID }}
```

The Azure CLI's service principal, for example after `az login --service-principal` or
in an `AzureCLI@2` pipeline task. The CLI asks the Azure CLI for a token, waiting up to
two minutes, and reuses it until five minutes before it expires:

```powershell
$env:XBOX_AUTHENTICATION__MODE = "azurecli"
xbox auth status --output json
```

Azure CLI sign-in state can be shared across terminals under the same OS user; use a
separate `AZURE_CONFIG_DIR` for concurrent jobs that sign in as different identities.

For Azure DevOps, use an `AzureCLI@2` task with an Azure Resource Manager service
connection, preferably configured for workload identity federation. This example assumes
the XBOX CLI is already installed and on `PATH`; replace the service connection name
with your own:

```yaml
steps:
  - task: AzureCLI@2
    displayName: Check XBOX CLI identity
    inputs:
      azureSubscription: 'nod-ci-service-connection'
      scriptType: pscore
      scriptLocation: inlineScript
      inlineScript: |
        xbox auth status --output json
    env:
      XBOX_AUTHENTICATION__MODE: azurecli
```

The task signs Azure CLI in using the service connection before running the script.
Run `xbox` inside that task so it can use the task's Azure CLI authentication context.
The XBOX CLI asks Azure CLI for an access token for the target API; it doesn't obtain
an Azure DevOps OIDC token itself. Don't set `XBOX_AUTHENTICATION__TENANTID` or
`XBOX_AUTHENTICATION__CLIENTID` in `azurecli` mode: the service connection selects the
tenant and identity. `addSpnToEnvironment` isn't needed, and no token file is required.
The target API must separately grant access to the service principal.

### How GitHub OIDC authentication works

In `githuboidc` mode, the XBOX CLI uses two tokens: a GitHub OIDC token that proves
which workflow job is running, and an Entra access token that represents your registered
application's service principal to the target API.

1. **GitHub provides the token-request credentials.** With `permissions: id-token: write`,
   GitHub Actions exposes `ACTIONS_ID_TOKEN_REQUEST_URL` and
   `ACTIONS_ID_TOKEN_REQUEST_TOKEN` to the job. The CLI reads them automatically; you
   don't create or store them as repository secrets.
2. **The CLI requests the GitHub OIDC token.** It sends an HTTP GET to the request URL,
   using the request token as a bearer credential and adding the audience
   `api://AzureADTokenExchange`. GitHub returns a signed token whose issuer (`iss`) is
   `https://token.actions.githubusercontent.com`. For a job using an environment, the
   subject (`sub`) identifies that environment; its exact format depends on the
   repository's OIDC settings, including whether immutable IDs are included (see
   [Set up GitHub Actions](#set-up-github-actions)). Without an environment, the subject
   typically identifies a branch or pull request instead. The exact subject must match
   the trust configured in Entra.
3. **The CLI asks Entra for an app-only access token through MSAL.** The tenant ID
   (`XBOX_AUTHENTICATION__TENANTID`) selects the Entra authority, and the application
   client ID (`XBOX_AUTHENTICATION__CLIENTID`) identifies your app registration. MSAL
   sends a client-credentials request to
   `https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token`, presenting the
   GitHub OIDC token as a *client assertion* instead of a client secret or certificate.
   The requested scope is the target API's `<resource>/.default`, from `AzureAd:Scopes`.
4. **Entra validates the trust and issues the access token.** Entra verifies the GitHub
   token's signature and validity and checks that its issuer, subject, and audience match
   a federated identity credential on the selected app registration. If successful, Entra
   returns an access token representing the application's service principal in that
   tenant, intended for the requested API.

The GitHub request token only authorizes requesting the GitHub OIDC token; neither is
the Entra access token used for API calls. The federated credential determines which
GitHub jobs may authenticate as your application. It does **not** grant API permissions:
the target service must separately authorize the service principal.

### Set up GitHub Actions

`githuboidc` mode signs a GitHub Actions job in as a service principal in your Entra
tenant, with no secret to store or rotate. To create the app registration, its service
principal, and a federated credential that trusts your repository, follow
[Configuring OpenID Connect in Azure](https://docs.github.com/actions/security-for-github-actions/security-hardening-your-deployments/configuring-openid-connect-in-azure)
and
[Create a trust relationship between an app and an external identity provider](https://learn.microsoft.com/entra/workload-id/workload-identity-federation-create-trust).
Skip the `azure/login` step and Azure role assignments in those guides: the CLI
requests the GitHub OIDC token itself, and it doesn't call Azure Resource Manager.

What the XBOX CLI depends on:

- **The federated credential** uses the issuer `https://token.actions.githubusercontent.com`
  and the audience `api://AzureADTokenExchange`, which is the audience the CLI requests.
- **Its subject matches the exact `sub` GitHub issues for the job's environment.**
  The legacy default is `repo:<OWNER>/<REPO>:environment:<ENVIRONMENT>`. On GitHub.com,
  repositories created, renamed, or transferred after July 15, 2026 use the immutable
  default `repo:<OWNER>@<OWNER-ID>/<REPO>@<REPO-ID>:environment:<ENVIRONMENT>`.
  Existing repositories retain the legacy format unless they opt in; immutable subjects
  aren't available on GitHub Enterprise Server. See
  [GitHub's immutable subject guidance](https://docs.github.com/en/actions/reference/security/oidc#immutable-subject-claims).
  Organizations and repositories can also customize subjects. Inspect repository
  customization with `gh api repos/<OWNER>/<REPO>/actions/oidc/customization/sub`,
  but don't treat that configuration response as the issued subject.
- **The job declares that environment and `permissions: id-token: write`.** Without the
  environment, GitHub presents a branch or pull-request subject and Entra ID refuses it.
- **The job sets `XBOX_AUTHENTICATION__MODE`, `XBOX_AUTHENTICATION__TENANTID`, and
  `XBOX_AUTHENTICATION__CLIENTID`,** as in the [example](#examples) above. The tenant
  and client IDs aren't secrets, because the trust is in the federated credential, so
  store them as environment variables and read them through `vars`.

`xbox auth status` confirms the sign-in. The `objectId` it reports is the service
principal's object ID, not the app registration's. PlayFab and XBOX partner calls also
need the service principal to be granted access, which those services don't support yet
(see the note at the top of this section).

The subject identifies the environment, not the branch, so the environment's
deployment branch policy and required reviewers control who can sign in. Restrict
production environments to protected branches and require approval. For non-production
branch testing, an environment can instead require approval from a trusted team for each
run while allowing self-approval: add the team under **Required reviewers** and leave
**Prevent self-review** unchecked. A team member must still manually approve each run;
membership alone does not approve it. Administrators can bypass protection rules unless
that ability is disabled in the environment settings.

Fork origin alone is not a credential boundary. A `pull_request_target` workflow runs
with base-repository permissions and can request an OIDC token with `id-token: write`,
even when the PR comes from a fork. Never check out or execute untrusted PR code in a
job that can authenticate as the service principal; use environment protection rules
to control access to that identity.

If the subject doesn't match, Entra ID refuses the token with `AADSTS700213` ("No
matching federated identity record found for presented assertion subject '…'"). The
error quotes the subject GitHub sent, so compare it with the credential's subject.

To keep each environment's settings with the environment, store them as environment
variables named after the CLI's own environment variables, such as
`XBOX_AUTHENTICATION__CLIENTID`, and pass every `XBOX_*` variable to the CLI unchanged.
One environment can then request a token for a different API by setting
`XBOX_AZUREAD__SCOPES`, while another keeps the default.

### Confirm the identity

In a service-principal mode, `auth status` acquires a token and reports the mode,
principal type, tenant, app ID, object ID, audience, and any app roles from the
token's claims. It never prints the token. If no token can be acquired, or the settings
or configuration are invalid, it exits `1` and writes the error to stderr, with nothing
on stdout, so a pipeline step running it fails. See [Auth](commands/auth.md#auth-status).
