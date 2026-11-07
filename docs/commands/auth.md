---
title: Auth
description: Sign in and out with Microsoft Entra ID, and check who is signed in.
---

# Auth

`xbox auth` manages the Microsoft Entra ID account that later commands use to call
PlayFab and the XBOX partner services.

```bash
xbox auth <command>
```

| Command | Description |
| --- | --- |
| `xbox auth login` | Sign in with Microsoft Entra ID through the system browser, replacing any account already signed in. |
| `xbox auth logout` | Sign out every cached account. |
| `xbox auth status` | Show the signed-in account. |

The CLI uses one account at a time. `auth login` signs out any other account, so signing in
with a different account switches every later command to it. `auth status` shows which
account that is. If more than one account is cached, for example by an older CLI version,
commands fail until you run `auth login` again.

No `auth` command declares a table view, so all three refuse `--output table`
rather than quietly returning their default output.

When `XBOX_AUTHENTICATION__MODE` selects a service-principal mode, there is no
account to sign in or out: `auth login` and `auth logout` print why on stderr and exit
`1`, and `auth status` describes the service principal instead. See
[Automation (service principal) authentication](../authentication.md#automation-service-principal-authentication).

See [Authentication](../authentication.md) for the full
sign-in flow and [Configuration](../configuration.md) for
configuration precedence.

## `auth login`

Sign in interactively with Microsoft Entra ID, replacing any account already signed in.

```bash
xbox auth login
```

The command opens the system browser for authentication and prints the signed-in
username and token expiration time on success. It then signs out every other cached
account, so later commands use the account you just chose. It does not require a PlayFab
title ID.

## `auth logout`

Sign out every cached account and clear cached Microsoft Entra credentials.

```bash
xbox auth logout
```

The command reports whether no cached account was present, one account was
removed, or multiple cached accounts were removed. Values saved with
[`context set`](context.md) are kept, and signing back in as the same account
restores them.

## `auth status`

Show the signed-in developer identity. It reads the local token cache, refreshing the
token with Microsoft Entra ID when needed, and doesn't call PlayFab or the XBOX partner
services, so it doesn't confirm access to them. To see the saved PlayFab title and
seller, run [`xbox context get`](context.md).

```bash
xbox auth status
```

`auth status` defaults to human-readable text rather than JSON. For automation,
request JSON explicitly:

```bash
xbox auth status --output json-compact
```

When JSON output is requested and the user is not signed in, `auth status` still
exits `0` with the payload `{"signedIn":false}`.
That lets scripts branch on the JSON instead of treating signed-out state as a
process failure. Only a missing account counts as signed out: if the configuration,
the sign-in settings, or the token cache can't be read, `auth status` exits `1`. With
JSON output, it writes `"signedIn": false` and an `error` message to stderr and nothing
to stdout; text output writes `Failed to retrieve status:` and the error to stderr.

### Service-principal modes

In a service-principal mode, `auth status` acquires an app-only token and reports
its claims. It never prints the token itself.

```bash
XBOX_AUTHENTICATION__MODE=azurecli xbox auth status --output json
```

```json
{
  "signedIn": true,
  "authenticationMode": "azurecli",
  "principalType": "app",
  "tenantId": "00000000-0000-0000-0000-000000000000",
  "appId": "11111111-1111-1111-1111-111111111111",
  "objectId": "22222222-2222-2222-2222-222222222222",
  "audience": "api://example",
  "roles": [],
  "tokenType": "Bearer",
  "tokenExpiresAt": "2030-01-01T00:00:00+00:00",
  "scopes": ["<scope>/.default"]
}
```

`principalType` is `app` for a service principal or managed identity. Every
service-principal mode accepts only an app-only token, so `azurecli` fails, rather than
reporting `user`, when the Azure CLI is signed in as a person. If the token can't be
acquired, or the service-principal settings or configuration are invalid, or a profile is selected with
`--profile` or `XBOX_PROFILE`, `auth status` exits `1`. With JSON output, it
writes `"signedIn": false` and an `error` message to stderr and nothing to stdout; text
output writes the error to stderr. Unlike a signed-out user, a broken service-principal
credential is a failure, so a pipeline step running `auth status` fails with it.
