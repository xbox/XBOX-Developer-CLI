---
title: Configuration
description: How the CLI layers settings from configuration files, environment variables, and command-line options.
---

# Configuration

The XBOX Developer CLI layers configuration from multiple sources so you can keep base
defaults in files while overriding individual values per environment or per
invocation.

## Configuration sources and precedence

Configuration is loaded in the following order — **later sources override
earlier ones**:

1. `appsettings.json`: base defaults loaded from the application base directory.
2. `appsettings.{environment}.json`: environment-specific overlay, loaded only
   when `--config-env` is set. The file is required when requested.
3. Environment variables: `XBOX_` and the key's path. See [Environment variables](#environment-variables).
4. CLI option overrides.

The seller, PlayFab title, and XBOX Live sandbox aren't configuration. They're context values,
resolved from their flag, then `XBOX_` plus the option's name, such as `XBOX_SELLER_ID`, then the
value saved with [`xbox context set`](commands/context.md). Configuration files don't set them.
See [Saved context](#saved-context).

> [!NOTE]
> `appsettings.json` ships inside the release archive and must sit next to the
> executable. If you move the binary, copy `appsettings.json` alongside it. See
> [Installation](installation.md).

## Environment-specific files

To use an environment-specific configuration file, pass `--config-env`:

```bash
# Uses appsettings.Development.json as an overlay on top of appsettings.json
xbox --config-env Development auth login
```

## Environment variables

The CLI's own variables all start with `XBOX_`. A setting's or context value's variable is
`XBOX_` and its key in capitals, with `__` for each level of nesting and `_` for a hyphen:
`Telemetry:Enabled` is `XBOX_TELEMETRY__ENABLED`, and the context key `seller-id` is
`XBOX_SELLER_ID`. `XBOX_PROFILE` picks the profile saved values come from. A variable applies to
the shell that sets it and every command that shell, an orchestrator, or an agent starts:

```bash
# Linux / macOS
export XBOX_PLAYFAB_TITLE_ID=ABC123
xbox playfab api /Admin/GetTitleData
```

```powershell
# Windows PowerShell
$env:XBOX_PLAYFAB_TITLE_ID = "ABC123"
xbox playfab api /Admin/GetTitleData
```

Useful environment variables include:

| Environment variable | Configuration key or behavior |
|----------------------|-------------------------------|
| `XBOX_PLAYFAB_TITLE_ID` | The PlayFab title, a context value |
| `XBOX_SELLER_ID` | The Partner Center seller, a context value |
| `XBOX_SANDBOX` | The XBOX Live sandbox, a context value |
| `XBOX_PROFILE` | The profile saved context values come from, when `--profile` isn't given. Refused in a service-principal mode, which has no saved context |
| `XBOX_AZUREAD__CLIENTID` | `AzureAd:ClientId` |
| `XBOX_AZUREAD__TENANTID` | `AzureAd:TenantId` |
| `XBOX_AZUREAD__SCOPES` | `AzureAd:Scopes` |
| `XBOX_TELEMETRY__ENABLED` | `Telemetry:Enabled` |
| `XBOX_XBOXPARTNER__TIMEOUTSECONDS` | `XboxPartner:TimeoutSeconds`, the request timeout in seconds for commands such as [`game`](commands/game.md). Defaults to `120`; must be a positive number. |
| `XBOX_XBLPCSANDBOX__PATH` | `XblPCSandbox:Path`, the `XblPCSandbox.exe` that the [`pc sandbox`](commands/pc.md) commands run |
| `XBOX_TELEMETRY_OPTOUT` | Telemetry opt-out when set to `1`, `true`, or `yes` |
| `XBOX_AUTHENTICATION__MODE` | The service-principal authentication mode; see below |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Optional telemetry exporter connection string when not configured elsewhere |

Service-principal authentication settings (`XBOX_AUTHENTICATION__*`) are the
exception to this layering: they are read only from environment variables, never from
`appsettings.json`, user settings, or CLI options. `XBOX_AUTHENTICATION__TENANTID` and
`XBOX_AUTHENTICATION__CLIENTID` are shared by every mode that takes them. See
[Automation (service principal) authentication](authentication.md#automation-service-principal-authentication).

## CLI option overrides

CLI options take the highest precedence and override values from every other
source.

| Option | Configuration key or behavior |
|--------|-------------------------------|
| `--config-env` | Loads `appsettings.{env}.json` |
| `--playfab-title-id` | The PlayFab title, a context value; see [Saved context](#saved-context) |
| `--profile` | The profile saved context values come from; see [Saved context](#saved-context) |
| `--client-id` | `AzureAd:ClientId` for user sign-in; refused in service-principal modes |
| `--tenant-id` | `AzureAd:TenantId` for user sign-in; refused in service-principal modes |
| `--scopes` | `AzureAd:Scopes` |
| `--disable-telemetry` | Sets `Telemetry:Enabled=false` for this invocation |

Global output and safety flags (`--output`, `--data-only`, `--confirm`,
`--yes`, `-y`, and `--verbose`) affect command behavior but are not stored as
configuration keys. See [Commands](commands.md) for the full global option list and
[Safety and destructive operations](safety.md) for destructive confirmation behavior.

For example, to target a specific title for a single command:

```bash
xbox --playfab-title-id ABC123 playfab api /Admin/GetTitleData
```

## Saved context

`xbox context set` saves default values, such as the seller and PlayFab title, in
`context.json` in the CLI state directory (`%LOCALAPPDATA%\XboxDevCLI` on Windows,
`~/Library/Application Support/XboxDevCLI` on macOS, and `$XDG_DATA_HOME/XboxDevCLI`, by
default `~/.local/share/XboxDevCLI`, on Linux), separate from the token cache. Entries are keyed
by the signed-in account and the endpoint's host and port, so changing either does not reuse a
value saved for another. Saving needs a writable state directory; reading does not.

Context values resolve from their flag, then their `XBOX_` variable, then the value saved in the
selected profile, so a saved value applies only when nothing else sets it. The profile is
`--profile`, then `XBOX_PROFILE`, then `default`. Run `xbox context get` to see what's saved, and
a command with `--verbose` to see where its values came from. See [Context](commands/context.md).

Passing the title or seller explicitly, or through its environment variable,
avoids saving context and does not require a writable context store. It does
not remove authentication requirements: service calls still need a signed-in,
authorized account. See [Authentication](authentication.md).

## Telemetry configuration

Official release artifacts collect limited anonymous telemetry unless telemetry
is disabled. You can opt out for one invocation:

```bash
xbox --disable-telemetry auth status
```

Or opt out through the environment:

```bash
export XBOX_TELEMETRY_OPTOUT=1
```

```powershell
$env:XBOX_TELEMETRY_OPTOUT = "1"
```

See [Telemetry](telemetry.md) for the complete collection policy and deployment
details.

## Putting it together

Given a title saved with `xbox context set`, or set in `XBOX_PLAYFAB_TITLE_ID`, you can
target another one for a single command without changing either:

```bash
# The saved title is ABC123, but this call targets DEF456
xbox --playfab-title-id DEF456 playfab api /Admin/GetTitleData
```
