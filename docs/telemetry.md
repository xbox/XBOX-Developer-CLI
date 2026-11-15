---
title: Telemetry
description: What anonymous product-usage telemetry the CLI collects, and how to turn it off.
---

# Telemetry

The XBOX CLI collects limited, anonymous usage telemetry to help Microsoft understand which
commands are used, how reliable they are, and how long they take. You can
[turn it off](#opt-out). Telemetry never changes a command's output or exit code.

Microsoft handles this data as described in the
[Microsoft privacy statement](https://go.microsoft.com/fwlink/?LinkID=824704). The repository's
[privacy notice](/PRIVACY.md) covers data collection by the software.

## What is collected

Each run of `xbox` creates at most one event, `xbox.cli.command_invocation`, with these fields
and nothing else:

| Field | Holds |
|-------|-------|
| `xbox.cli.schema_version` | The event's schema version, `3` |
| Time | When the command ran, rounded down to the hour |
| `service.version` | The CLI version |
| `os.type`, `os.version` | The operating-system family, such as `windows`, and its major version only |
| `host.arch` | The processor architecture: `amd64`, `arm64`, or `unknown` |
| `process.runtime.name`, `process.runtime.version` | The .NET runtime and its version |
| `xbox.cli.command` | The command's path in the command tree, joined with hyphens, such as `playfab-economy-inventory-transaction-list`; `root` for `xbox` alone; `unknown` when no command was recognized |
| `xbox.cli.auth_mode` | How the CLI signed in: `user`, a service-principal mode such as `githuboidc`, or `invalid`; never the tenant, application, or identity |
| `xbox.cli.outcome` | `success`, `failure`, or `cancelled` |
| `process.exit.code` | The exit code |
| `error.type` | Why a command failed, from a fixed list: `usage`, `context`, `auth`, `not_confirmed`, `transport`, `service_rejected`, `service_error`, `local_tool`, `io`, `cancelled`, or `_OTHER`. Not sent on success. |
| `http.response.status_code` | The status of the last service response, when there was one |
| `xbox.cli.duration_ms` | How long the command took, in milliseconds |
| `xbox.cli.options` | The names of the options supplied, without dashes, sorted, such as `output,verbose`; never their values |
| `xbox.cli.context_keys` | Which context values the command had, whether from an option, an environment variable, or saved context: `playfab-title-id`, `sandbox-id`, `seller-id`, or `title-id`; never the values |
| `xbox.cli.ci` | `true` when a well-known CI variable, such as `CI` or `TF_BUILD`, is set; only whether it's set is read |

A command or option name the CLI doesn't define is sent as `unknown` or left out, so a value
typed in the wrong place is never sent.

### Approximate location

The CLI never sends your location. However, Application Insights derives an approximate
location (country, region or state, and city) from the IP address that sends the event, and
stores it on every event. The IP address itself isn't kept; it's stored as `0.0.0.0`. The
location is used only to understand adoption across markets. [Opting out](#opt-out) stops all
telemetry, including this location.

## What isn't collected

The CLI doesn't collect argument or option values, request or response bodies, PlayFab title
IDs, seller IDs, sandbox IDs, usernames, tenant or account IDs, access tokens, file paths,
configured secrets, error messages, or the machine's name.

## When the event is sent

When a command succeeds, the CLI saves the event on disk, and a later run sends it, so the
command doesn't wait on the network. When a command fails, the CLI waits at most 300
milliseconds to send its event and any saved ones. In CI, where the CLI might not run again, it
sends the event before it exits instead of saving it. That can add up to about 5 seconds when
the endpoint is slow, and the event is dropped if it isn't sent in that time.

## Version header

Separately from telemetry, every request the CLI sends with an access token carries a
`User-Agent: XboxDevCLI/<version>` header, such as `XboxDevCLI/0.1.0.9`, so those services can
tell which CLI versions call them. It holds nothing else, and opting out of telemetry doesn't
remove it.

## First-run disclosure

The first time the CLI collects telemetry on a machine, it writes this notice to stderr, once:

```text
TELEMETRY
  XBOX CLI collects anonymous usage telemetry.
  Learn more or opt out: https://go.xbox/cli/telemetry
```

## Opt out

### For one command

Pass `--disable-telemetry`:

```powershell
xbox --disable-telemetry auth status
```

### For the current shell session

Set `XBOX_TELEMETRY_OPTOUT` in PowerShell:

```powershell
$env:XBOX_TELEMETRY_OPTOUT = "1"
```

On bash or zsh:

```bash
export XBOX_TELEMETRY_OPTOUT=1
```

Values `1`, `true`, and `yes` turn telemetry off, whatever else is configured. Setting
`XBOX_TELEMETRY__ENABLED` to `false` turns it off too.

### For every new terminal

Set the variable for your account. On Windows, run this, then reopen your terminal, and your
IDE if you use its integrated terminal:

```powershell
[Environment]::SetEnvironmentVariable("XBOX_TELEMETRY_OPTOUT", "1", "User")
```

On Linux and macOS, add the `export` line to your shell's startup file, such as `~/.zshrc` or
`~/.bashrc`, the same way [Installation](installation.md) adds the CLI to `PATH`.

## Where telemetry goes

Release builds send telemetry to a Microsoft-owned Azure Monitor Application Insights resource,
which you can't change. The CLI ignores `APPLICATIONINSIGHTS_CONNECTION_STRING`, so a host that
sets it for its own application doesn't redirect the CLI's telemetry.
