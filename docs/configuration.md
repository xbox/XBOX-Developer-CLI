---
title: Configuration
description: How the CLI layers settings from its built-in defaults, environment variables, and command-line options.
---

# Configuration

The XBOX CLI needs no configuration file. Its settings come from defaults built
into the executable, which you can override for a shell with environment variables or for
a single command with options.

## Configuration sources and precedence

Settings come from three sources, and later sources override earlier ones:

1. Built-in defaults, compiled into the executable. See [Settings](#settings).
2. Environment variables. See [Environment variables](#environment-variables).
3. Command-line options. See [CLI option overrides](#cli-option-overrides).

A new release brings its own defaults, so there's no settings file to carry forward.

The seller, PlayFab title, XBOX title, and sandbox aren't settings; see
[Context](commands/context.md) for where a command gets them.

## Settings

| Key | Environment variable | Option | Built-in default |
|-----|----------------------|--------|------------------|
| `AzureAd:ClientId` | `XBOX_AZUREAD__CLIENTID` | `--client-id` | The CLI's own Microsoft Entra ID application |
| `AzureAd:TenantId` | `XBOX_AZUREAD__TENANTID` | `--tenant-id` | `common` |
| `AzureAd:Scopes` | `XBOX_AZUREAD__SCOPES` | `--scopes` | Built in |
| `PlayFab:BaseUrlTemplate` | `XBOX_PLAYFAB__BASEURLTEMPLATE` | None | Built in |
| `PlayFab:StudioBaseUrl` | `XBOX_PLAYFAB__STUDIOBASEURL` | None | Built in |
| `PlayFab:MaxRequestBodyBytes` | `XBOX_PLAYFAB__MAXREQUESTBODYBYTES` | None | `1048576` (1 MiB) |
| `PlayFab:MaxResponseBytes` | `XBOX_PLAYFAB__MAXRESPONSEBYTES` | None | `4194304` (4 MiB) |
| `XboxPartner:BaseUrl` | `XBOX_XBOXPARTNER__BASEURL` | `--base-url`, on the commands that accept it | Built in |
| `XboxPartner:TimeoutSeconds` | `XBOX_XBOXPARTNER__TIMEOUTSECONDS` | None | `120` |
| `XblPCSandbox:Path` | `XBOX_XBLPCSANDBOX__PATH` | None | Not set |
| `AuditLog:Enabled` | `XBOX_AUDITLOG__ENABLED` | None | `false` |
| `Telemetry:Enabled` | `XBOX_TELEMETRY__ENABLED` | `--disable-telemetry` sets `false` | `true` |

- `AzureAd:*` configure user sign-in; see [Authentication](authentication.md#overriding-entra-id-settings).
- `PlayFab:StudioBaseUrl` is the PlayFab endpoint for studio APIs (`/Studio/*`), such as
  those [`playfab service-principal`](commands/playfab/service-principal.md) calls.
- `XboxPartner:TimeoutSeconds` is the request timeout in seconds for commands such as
  [`game`](commands/game.md), and must be a positive number.
- `XblPCSandbox:Path` is the `XblPCSandbox.exe` that the [`pc sandbox`](commands/pc.md)
  commands run. When it isn't set, the CLI looks in its own directory and then on `PATH`.
- `AuditLog:Enabled` turns on the [audit log](audit-log.md).
- `Telemetry:*` are described in [Telemetry](telemetry.md).

When `PlayFab:BaseUrlTemplate`, `PlayFab:StudioBaseUrl`, or `XboxPartner:BaseUrl` isn't the
built-in default, every command that sends a token to that endpoint first writes a warning to
stderr.

## Environment variables

A setting's variable is `XBOX_` and its key, with `__` (two underscores) in place of each `:`,
so `Telemetry:Enabled` is `XBOX_TELEMETRY__ENABLED`. Write names in capitals. A variable applies
to the shell that sets it and every command that shell starts:

```bash
# Linux / macOS
export XBOX_XBOXPARTNER__TIMEOUTSECONDS=300
xbox game list
```

```powershell
# Windows PowerShell
$env:XBOX_XBOXPARTNER__TIMEOUTSECONDS = "300"
xbox game list
```

Every setting in [Settings](#settings) has a variable. The CLI also reads these:

| Environment variable | Purpose |
|----------------------|---------|
| `XBOX_PLAYFAB_TITLE_ID`, `XBOX_SELLER_ID`, `XBOX_SANDBOX_ID`, `XBOX_TITLE_ID` | Context values; see [Context](commands/context.md) |
| `XBOX_PROFILE` | The [profile](commands/context.md#profiles) saved values come from, when `--profile` isn't passed |
| `XBOX_TELEMETRY_OPTOUT` | Turns telemetry off when set to `1`, `true`, or `yes` |
| `XBOX_AUTHENTICATION__MODE` and other `XBOX_AUTHENTICATION__` variables | Service-principal sign-in |

`XBOX_AUTHENTICATION__` settings are read only from environment variables, never from built-in
defaults or options. See
[Automation (service principal) authentication](authentication.md#automation-service-principal-authentication).

## CLI option overrides

Options override every other source, for one command.

| Option | Configuration key or behavior |
|--------|-------------------------------|
| `--client-id` | `AzureAd:ClientId` for user sign-in; refused in service-principal modes |
| `--tenant-id` | `AzureAd:TenantId` for user sign-in; refused in service-principal modes |
| `--scopes` | `AzureAd:Scopes` |
| `--base-url` | `XboxPartner:BaseUrl`, on the commands that accept it, such as [`game`](commands/game.md) and [`seller`](commands/seller.md) |
| `--disable-telemetry` | Sets `Telemetry:Enabled=false` for this command |

See [Commands](commands.md) for the other global options.

## Telemetry configuration

Official releases collect limited anonymous telemetry unless you turn it off. To turn it off
for one command:

```bash
xbox --disable-telemetry auth status
```

To turn it off for a shell:

```bash
export XBOX_TELEMETRY_OPTOUT=1
```

```powershell
$env:XBOX_TELEMETRY_OPTOUT = "1"
```

See [Telemetry](telemetry.md) for what's collected.

## Upgrading from a release that read appsettings.json

Earlier releases read `appsettings.json` from beside the executable. If you changed a value in
it, set its `XBOX_` variable instead: for example, `"AzureAd": { "ClientId": "..." }` becomes
`XBOX_AZUREAD__CLIENTID`. You might see these messages until you finish:

- `--config-env` fails with this error. Remove the option, and set `XBOX_` variables instead.

  ```text
  --config-env is no longer supported because settings files are no longer read. Set XBOX_ environment variables instead, such as XBOX_AZUREAD__CLIENTID.
  ```

- While an `appsettings*.json` file is beside the executable, commands write this note to
  stderr. Delete the file.

  ```text
  Note: '<file>' is no longer read. Settings come from the built-in defaults, XBOX_ environment variables, and command options. Delete the file to hide this note.
  ```

- PlayFab commands fail with an error that names `XBOX_PLAYFAB__REQUESTHEADERS__0__VALUE` if an
  earlier error message led you to set it. Remove the variable.
