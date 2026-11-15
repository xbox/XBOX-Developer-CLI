---
title: Authentication
description: Sign in to the XBOX CLI interactively or as a service principal, manage cached credentials, and meet PlayFab's permission requirements.
---

# Authentication

The XBOX CLI signs you in with your developer account through Microsoft Entra ID.
For Partner Center workflows, sign in with an account that has access to your Partner Center
seller. See [Getting started](getting-started.md) for choosing a seller and listing its games.

PlayFab checks its own permissions, separately from your Partner Center access. `xbox playfab`
commands need **Admin** permission on the title they act on; see
[How PlayFab authentication works](#how-playfab-authentication-works). Unattended jobs can sign
in as a service principal instead; see
[Automation (service principal) authentication](#automation-service-principal-authentication).

## Sign in

```bash
xbox auth login
```

`auth login` opens your system browser to sign in, then reports the account and when its token
expires:

```text
Successfully signed in as: dev@nodpublishers.com
Token expires at: YYYY-MM-DD HH:MM:SS
```

The CLI uses one account at a time. To switch accounts, run `xbox auth login` again and choose
the other account. Every later command uses it, including in other terminals for your OS user.
See [Auth](commands/auth.md).

## Check status

```bash
xbox auth status
```

`auth status` shows the cached account and its token. It doesn't check that you can use
PlayFab or Partner Center.

```text
Signed in as: dev@nodpublishers.com
Home Account ID: 00000000-0000-0000-0000-000000000000.00000000-0000-0000-0000-000000000000
Environment: login.microsoftonline.com
Tenant ID: 00000000-0000-0000-0000-000000000000
Token type: Bearer
Token expires at: YYYY-MM-DD HH:MM:SS
Scopes: <scope>/.default
```

In a script, pass `--output json` and check `signedIn`. A nonzero exit means the CLI couldn't
read its settings or token cache.

Saved defaults, such as the seller or PlayFab title, are separate from sign-in. See
[Context](commands/context.md).

## Sign out

```bash
xbox auth logout
```

`auth logout` removes the cached accounts and tokens, which signs out every terminal for your
OS user. It keeps your saved context.

## Token cache

Commands reuse and refresh your cached token, so run `xbox auth login` again only when the CLI
asks you to. The token is stored in the Windows protected cache, the macOS Keychain, or the
Linux Secret Service keyring. On Linux without a usable keyring, such as a headless machine, the
CLI warns and uses an unencrypted file protected only by file permissions.

## How PlayFab authentication works

For `xbox playfab` commands, the CLI sends your Entra ID access token to PlayFab, so you never
handle a PlayFab title secret key. PlayFab checks that your account has **Admin** permission on
the title, or on the studio for the
[`playfab service-principal`](commands/playfab/service-principal.md) commands. A Studio Admin
grants it in PlayFab's Game Manager, for example with the built-in **Title Admin** role; see
[PlayFab user roles](https://docs.xbox.com/services/playfab/identity/dev-identity/permissions/playfab-user-roles).

A [service principal](#automation-service-principal-authentication) gets PlayFab access only
when a person grants it with `xbox playfab service-principal set`. A service principal can't
manage grants itself.

See [PlayFab API integration](playfab-api.md) for selecting a title and calling PlayFab APIs.

## Overriding Entra ID settings

The Entra ID application used for sign-in is built into the CLI. Most users don't need to change
it. You can override its `AzureAd` settings with `XBOX_AZUREAD__` environment variables, such as
`XBOX_AZUREAD__CLIENTID`, or for one command with these options:

| Option | Overrides |
|--------|-----------|
| `--client-id` | `AzureAd:ClientId` for user sign-in; refused in service-principal modes |
| `--tenant-id` | `AzureAd:TenantId` for user sign-in; refused in service-principal modes |
| `--scopes` | `AzureAd:Scopes` |

See [Configuration](configuration.md) for all settings.

## Automation (service principal) authentication

> [!IMPORTANT]
> A service principal can sign in, and `auth status` reports a valid token, but its calls fail
> with an authorization error until the service accepts it. PlayFab accepts it after a person
> grants it access with
> [`xbox playfab service-principal set`](commands/playfab/service-principal.md). Other
> commands that call an online service, such as `xbox game` and `xbox xbl`, don't accept
> service principals yet; see
> [Known issues](known-issues.md#xbox-game-xbox-xbl-and-similar-commands-dont-accept-service-principals-yet).

Continuous integration (CI) pipelines and other unattended jobs can't complete a browser sign-in.
Instead, they set `XBOX_AUTHENTICATION__MODE` to a service-principal mode, and each command gets
an app-only Entra ID token as the configured app registration or managed identity. Nothing is
cached, and `auth login` and `auth logout` fail.

A service principal has no saved context, so pass the seller and title with their options or
`XBOX_` variables. See [Context](commands/context.md#behavior).

| Mode | Credential | Settings (each prefixed with `XBOX_AUTHENTICATION__`) |
|------|------------|-------------------------------------------------------------|
| `user` (default) | Interactive sign-in with `auth login`. | None. |
| `clientsecret` | Client secret read from a file. | `TENANTID`, `CLIENTID`, `CLIENTSECRET__FILE` |
| `certificate` | PKCS#12 (`.pfx`) certificate with its private key. | `TENANTID`, `CLIENTID`, `CERTIFICATE__PATH`, optional `CERTIFICATE__PASSWORDFILE` |
| `federatedtoken` | A federated identity token, for example from Azure Pipelines or Kubernetes, read from a file. A rotated token is picked up when the access token is refreshed. | `TENANTID`, `CLIENTID`, `FEDERATEDTOKEN__FILE` |
| `githuboidc` | The GitHub Actions OpenID Connect (OIDC) token. The job needs `permissions: id-token: write`. See [Set up GitHub Actions](#set-up-github-actions). | `TENANTID`, `CLIENTID` |
| `azurecli` | The service principal or managed identity signed in to the Azure CLI. A person signed in to the Azure CLI is refused; a person uses `auth login`. | None. |
| `managedidentity` | The managed identity of the Azure-hosted machine. Omit the client ID for a system-assigned identity. | Optional `CLIENTID` |

- `TENANTID` must be a tenant GUID or verified domain, not `common`, `organizations`, or
  `consumers`. `CLIENTID` must be a GUID.
- These settings come only from environment variables. `--client-id` and `--tenant-id` are
  refused in a service-principal mode; `--scopes` still applies.
- In `azurecli` mode, select the identity by signing in to the Azure CLI. In `managedidentity`
  mode, the identity determines the tenant.
- The CLI never falls back to another mode or to a cached user sign-in. An unknown mode, a
  missing setting, a setting the mode doesn't use, or an unreadable credential file fails with
  an error that names the variable.

Pass secrets only in files, never in environment variables or arguments, which can leak through
process listings and logs. Restrict each file to the account that runs the CLI, and delete it
when the job ends.

### Examples

A client secret on Linux or macOS, read from a file. `$RUNNER_TEMP` is GitHub Actions' temporary
folder; elsewhere, use any file that only the job can read:

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

The Azure CLI's service principal, for example after `az login --service-principal`:

```powershell
$env:XBOX_AUTHENTICATION__MODE = "azurecli"
xbox auth status --output json
```

Azure CLI sign-in is shared by every terminal for your OS user, so give concurrent jobs that
sign in as different identities their own `AZURE_CONFIG_DIR`.

In Azure Pipelines, run `xbox` inside an `AzureCLI@2` task with an Azure Resource Manager service
connection, preferably one that uses workload identity federation. The service connection selects
the tenant and identity, so don't set `XBOX_AUTHENTICATION__TENANTID` or
`XBOX_AUTHENTICATION__CLIENTID`. Replace the service connection name with your own:

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

### Set up GitHub Actions

`githuboidc` mode signs a GitHub Actions job in as a service principal in your Entra ID tenant,
with no secret to store or rotate. The CLI requests the job's OIDC token from GitHub, and Entra
ID exchanges it for an access token if it matches a federated credential on your app
registration. The federated credential decides which jobs can sign in; it doesn't grant access
to any API.

To create the app registration, its service principal, and a federated credential that trusts
your repository, follow
[Configuring OpenID Connect in Azure](https://docs.github.com/actions/security-for-github-actions/security-hardening-your-deployments/configuring-openid-connect-in-azure)
and
[Create a trust relationship between an app and an external identity provider](https://learn.microsoft.com/entra/workload-id/workload-identity-federation-create-trust).
Skip the `azure/login` step and Azure role assignments in those guides, because the CLI requests
the token itself and doesn't call Azure Resource Manager.

The setup must meet these requirements:

- **The federated credential** uses the issuer `https://token.actions.githubusercontent.com`
  and the audience `api://AzureADTokenExchange`.
- **Its subject matches the subject GitHub issues for the job's environment.** The earlier
  default is `repo:<OWNER>/<REPO>:environment:<ENVIRONMENT>`. On GitHub.com, repositories
  created, renamed, or transferred after July 15, 2026 use
  `repo:<OWNER>@<OWNER-ID>/<REPO>@<REPO-ID>:environment:<ENVIRONMENT>`; see
  [GitHub's immutable subject guidance](https://docs.github.com/en/actions/reference/security/oidc#immutable-subject-claims).
  Organizations and repositories can also customize subjects.
- **The job declares that environment and `permissions: id-token: write`.** Without the
  environment, GitHub presents a branch or pull request subject, and Entra ID refuses it.
- **The job sets `XBOX_AUTHENTICATION__MODE`, `XBOX_AUTHENTICATION__TENANTID`, and
  `XBOX_AUTHENTICATION__CLIENTID`,** as in the [example](#examples). The tenant and client IDs
  aren't secrets, so store them as environment variables and read them through `vars`.

The subject names the environment, not the branch, so the environment's protection rules
control who can sign in. Restrict a production environment to protected branches and require
approval. Administrators can bypass these rules unless that's turned off in the environment's
settings.

A `pull_request_target` workflow runs with the base repository's permissions and can request an
OIDC token, even for a pull request from a fork. Never check out or run untrusted pull request
code in a job that can sign in as the service principal.

If the subject doesn't match, Entra ID refuses the token with `AADSTS700213` ("No matching
federated identity record found for presented assertion subject '…'"). The error quotes the
subject GitHub sent, so compare it with the credential's subject.

To keep each environment's settings with the environment, store them as GitHub environment
variables named after the CLI's variables, such as `XBOX_AUTHENTICATION__CLIENTID`, and pass
them to the CLI unchanged.

### Confirm the identity

In a service-principal mode, `auth status` gets a token and reports the mode, principal type,
tenant, app ID, object ID, audience, and app roles. It never prints the token. If it can't get
a token, it exits `1`, so a pipeline step that runs it fails. See
[Auth](commands/auth.md#auth-status).

The `objectId` is the service principal's object ID, not the app registration's. Pass it as
`--principal-object-id` to `xbox playfab service-principal set` to grant the service principal
PlayFab access.
