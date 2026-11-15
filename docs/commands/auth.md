---
title: Auth
description: Sign in and out with Microsoft Entra ID, and check who is signed in.
---

# Auth

`xbox auth` manages the Microsoft Entra ID account that other commands use. See
[Authentication](../authentication.md) for how sign-in works, including as a service principal.

```text
xbox auth <command>
```

| Command | Description |
| --- | --- |
| `xbox auth login` | Sign in through the system browser, replacing any account already signed in. |
| `xbox auth logout` | Sign out every cached account. |
| `xbox auth status` | Show the signed-in account. |

No `auth` command accepts `--output table`.

When `XBOX_AUTHENTICATION__MODE` selects a service-principal mode, `auth login` and
`auth logout` exit `1`, and `auth status` describes the service principal.

## `auth login`

Sign in with Microsoft Entra ID, replacing any account already signed in.

```bash
xbox auth login
```

The command opens the system browser and, on success, prints the account and when its token
expires. Every later command uses that account. If more than one account is cached, for example
by an earlier CLI version, commands fail until you run `auth login` again.

## `auth logout`

Sign out every cached account.

```bash
xbox auth logout
```

The command prints the accounts it signed out, or that no account was signed in. Values saved
with [`context set`](context.md) are kept, and signing back in as the same account restores
them.

## `auth status`

Show the signed-in account and its token. The command can refresh the token with Entra ID, but
doesn't use it, so it doesn't confirm that other commands will accept it.
To see the saved seller and PlayFab title, run [`xbox context get`](context.md).

```bash
xbox auth status
```

The output is text unless you pass `--output`. For a script, request JSON and check `signedIn`:

```bash
xbox auth status --output json-compact
```

```json
{"signedIn":false}
```

| Exit code | Meaning |
| --- | --- |
| `0` | The status was read, whether or not an account is signed in. |
| `1` | The CLI couldn't read its settings or token cache, or, in a service-principal mode, couldn't get a token. The error is on stderr, and stdout is empty. |

### Service-principal modes

In a service-principal mode, `auth status` gets an app-only token and reports its claims. It
never prints the token.

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

`principalType` is `app` for a service principal or managed identity. See
[Confirm the identity](../authentication.md#confirm-the-identity).
