---
title: Configuration
description: How the CLI layers settings from its built-in defaults, environment variables, and command-line options.
---

# Configuration

The XBOX Developer CLI needs no configuration file. Its settings come from defaults built
into the executable, which you can override for a shell with environment variables or for
a single command with options.

## Configuration sources and precedence

Settings come from three sources — **later sources override earlier ones**:

1. Built-in defaults, compiled into the executable. See [Settings](#settings).
2. Environment variables: `XBOX_` and the key's path. See [Environment variables](#environment-variables).
3. CLI option overrides. See [CLI option overrides](#cli-option-overrides).

The CLI doesn't read a settings file. A release that's installed or updated brings its own
defaults, so there's nothing to copy forward between installations.

The seller, PlayFab title, and XBOX sandbox aren't configuration. They're context values,
resolved from their flag, then `XBOX_` plus the option's name, such as `XBOX_SELLER_ID`, then the
value saved with [`xbox context set`](commands/context.md). Settings don't set them, and saved
context doesn't change settings. See [Saved context](#saved-context).

## Settings

| Key | Environment variable | Option | Built-in default |
|-----|----------------------|--------|------------------|
| `AzureAd:ClientId` | `XBOX_AZUREAD__CLIENTID` | `--client-id` | The CLI's own Microsoft Entra application |
| `AzureAd:TenantId` | `XBOX_AZUREAD__TENANTID` | `--tenant-id` | `common` |
| `AzureAd:Scopes` | `XBOX_AZUREAD__SCOPES` | `--scopes` | Built in |
| `PlayFab:BaseUrlTemplate` | `XBOX_PLAYFAB__BASEURLTEMPLATE` | None | Built in |
| `PlayFab:MaxRequestBodyBytes` | `XBOX_PLAYFAB__MAXREQUESTBODYBYTES` | None | `1048576` (1 MiB) |
| `PlayFab:MaxResponseBytes` | `XBOX_PLAYFAB__MAXRESPONSEBYTES` | None | `4194304` (4 MiB) |
| `XboxPartner:BaseUrl` | `XBOX_XBOXPARTNER__BASEURL` | `--base-url`, on the commands that accept it | Built in |
| `XboxPartner:TimeoutSeconds` | `XBOX_XBOXPARTNER__TIMEOUTSECONDS` | None | `120` |
| `XblPCSandbox:Path` | `XBOX_XBLPCSANDBOX__PATH` | None | Not set |
| `AuditLog:Enabled` | `XBOX_AUDITLOG__ENABLED` | None | `false` |
| `Telemetry:Enabled` | `XBOX_TELEMETRY__ENABLED` | `--disable-telemetry` sets `false` | `true` |
| `Telemetry:DependencyTracingEnabled` | `XBOX_TELEMETRY__DEPENDENCYTRACINGENABLED` | None | `true` |
| `Telemetry:DependencyTraceSamplingRatio` | `XBOX_TELEMETRY__DEPENDENCYTRACESAMPLINGRATIO` | None | `0.1` |

- `AzureAd:*` configure user sign-in; see [Authentication](authentication.md#overriding-entra-id-settings).
- `XboxPartner:TimeoutSeconds` is the request timeout in seconds for commands such as
  [`game`](commands/game.md), and must be a positive number.
- `XblPCSandbox:Path` is the `XblPCSandbox.exe` that the [`pc sandbox`](commands/pc.md)
  commands run. When it isn't set, the CLI looks in its own directory and then on `PATH`.
- `AuditLog:Enabled` turns on the [audit log](audit-log.md).
- `Telemetry:*` are described in [Telemetry](telemetry.md).

Changing `PlayFab:BaseUrlTemplate` or `XboxPartner:BaseUrl` from its built-in default makes
every command that sends a token to that endpoint write a warning to stderr first, so a
changed endpoint can't go unnoticed.

## Environment variables

The CLI's own variables all start with `XBOX_`. A setting's or context value's variable is
`XBOX_` and its key, with `__` (two underscores) in place of each `:` and `_` for a hyphen:
`Telemetry:Enabled` is `XBOX_TELEMETRY__ENABLED`, and the context key `seller-id` is
`XBOX_SELLER_ID`. Write names in capitals, as shown: settings variables are matched regardless
of case, but on Linux and macOS the others, such as context values, `XBOX_PROFILE`, and
`XBOX_TELEMETRY_OPTOUT`, are recognized only in capitals. `XBOX_PROFILE`
picks the profile saved values come from. A variable applies to the shell that sets it and
every command that shell, an orchestrator, or an agent starts:

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

Every setting in [Settings](#settings) has a variable. Other useful environment variables
include:

| Environment variable | Configuration key or behavior |
|----------------------|-------------------------------|
| `XBOX_PLAYFAB_TITLE_ID` | The PlayFab title, a context value |
| `XBOX_SELLER_ID` | The Partner Center seller, a context value |
| `XBOX_SANDBOX_ID` | The XBOX sandbox, a context value |
| `XBOX_PROFILE` | The profile saved context values come from, when `--profile` isn't given. Refused in a service-principal mode, which has no saved context |
| `XBOX_TELEMETRY_OPTOUT` | Telemetry opt-out when set to `1`, `true`, or `yes` |
| `XBOX_AUTHENTICATION__MODE` | The service-principal authentication mode; see below |

Service-principal authentication settings (`XBOX_AUTHENTICATION__*`) are the
exception to this layering: they are read only from environment variables, never from
the built-in defaults or CLI options. `XBOX_AUTHENTICATION__TENANTID` and
`XBOX_AUTHENTICATION__CLIENTID` are shared by every mode that takes them. See
[Automation (service principal) authentication](authentication.md#automation-service-principal-authentication).

## CLI option overrides

CLI options take the highest precedence and override values from every other
source.

| Option | Configuration key or behavior |
|--------|-------------------------------|
| `--playfab-title-id` | The PlayFab title, a context value; see [Saved context](#saved-context) |
| `--profile` | The profile saved context values come from; see [Saved context](#saved-context) |
| `--client-id` | `AzureAd:ClientId` for user sign-in; refused in service-principal modes |
| `--tenant-id` | `AzureAd:TenantId` for user sign-in; refused in service-principal modes |
| `--scopes` | `AzureAd:Scopes` |
| `--base-url` | `XboxPartner:BaseUrl`, on the commands that accept it, such as [`game`](commands/game.md) and [`seller`](commands/seller.md) |
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

## Upgrading from a release that read appsettings.json

Earlier releases read `appsettings.json`, and `appsettings.{environment}.json` with
`--config-env`, from beside the executable. Releases now ship only the executable,
`LICENSE`, and `NOTICE.md`, and don't read those files.

- If you changed a value in one of those files, set its `XBOX_` variable instead. For
  example, `"AzureAd": { "ClientId": "..." }` becomes `XBOX_AZUREAD__CLIENTID`. Values you
  didn't change need nothing; they're built in.
- `--config-env` is retired. Passing it fails with:

  ```text
  --config-env is no longer supported because settings files are no longer read. Set XBOX_ environment variables instead, such as XBOX_AZUREAD__CLIENTID.
  ```

- While an `appsettings*.json` file is still beside the executable, each command writes this
  note to stderr once, except with `--output json` or `json-compact`. Delete the file to hide it:

  ```text
  Note: '<file>' is no longer read. Settings come from the built-in defaults, XBOX_ environment variables, and command options. Delete the file to hide this note.
  ```

- If you set `XBOX_PLAYFAB__REQUESTHEADERS__0__VALUE` because an earlier error message
  suggested it, remove it. PlayFab commands fail until you do, with an error that names it.
- `APPLICATIONINSIGHTS_CONNECTION_STRING` is no longer read. Release builds send telemetry
  only to their built-in destination; see [Telemetry](telemetry.md) to opt out.

Saved context, profiles, and the sign-in cache are unaffected.
