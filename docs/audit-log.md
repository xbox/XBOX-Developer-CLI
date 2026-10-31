---
title: Audit logging
description: Keep an opt-in, local, metadata-only record of the PlayFab operations the CLI performs.
---

# Audit logging

The XBOX Developer CLI can keep a local record of the PlayFab operations it performed on your
behalf. It is **off by default**, stays **on your machine**, and records
**metadata only** — never request bodies, responses, or tokens.

The audit log exists to answer *"which operations ran against which title, and
when"* after the fact. That question matters more than usual here: the CLI is
designed to be driven by AI agents, and an agent that accesses a live title should
leave a trail its operator can inspect.

## Enabling it

Set either of the following:

```bash
# Configuration key, in appsettings.json
"AuditLog": { "Enabled": true }

# Or an environment variable
export XBOX_AUDITLOG__ENABLED=true      # bash/zsh
$env:XBOX_AUDITLOG__ENABLED = "true"    # PowerShell
```

`AuditLog:Enabled` is the only switch. The logger is deliberately configured in
code rather than from configuration, so there is no minimum-level or provider
setting that could silently disable the trail while appearing to leave it on.

## Where the log lives

Entries are appended to the local application-data directory:

| Platform | Path |
|----------|------|
| Windows  | `%LOCALAPPDATA%\XboxDevCLI\audit<date>.jsonl` |
| Linux    | `$XDG_DATA_HOME/XboxDevCLI/audit<date>.jsonl`, defaulting to `~/.local/share` |
| macOS    | `~/Library/Application Support/XboxDevCLI/audit<date>.jsonl` |

The location comes from .NET's `SpecialFolder.LocalApplicationData`, so it follows
the platform convention rather than being configurable.

Files roll **daily** and the most recent **31** are retained, so older audit
files are pruned automatically.

Rolling is by date rather than by size on purpose. Several `xbox` processes
can run at once, so the file is opened in shared mode — and in that mode each
process tracks the file size in its own memory, which makes a size-triggered roll
race between them ([serilog-sinks-file#40](https://github.com/serilog/serilog-sinks-file/issues/40)).
No size ceiling is applied because Serilog would silently discard entries after
reaching it when size-based rolling is disabled. The current day's file can
therefore grow until the next daily roll; high-volume API workloads should
monitor local disk usage or archive the log externally.

## What is recorded

One line of [compact JSON](https://clef-json.org/) per API invocation:

```json
{"@t":"2026-08-25T16:59:47.3966336Z","@mt":"PlayFab invocation {ResolvedApiPath} {TitleId} {ExitStatus}","ResolvedApiPath":"/Admin/GetTitleData","TitleId":"ABC123","ExitStatus":0}
```

| Field | Meaning |
|-------|---------|
| `@t` | UTC timestamp of the invocation |
| `ResolvedApiPath` | Normalized PlayFab path, with any query string removed |
| `TitleId` | The PlayFab title the call targeted |
| `ExitStatus` | `0` when the call succeeded, `1` when it failed |

Read-only and mutating operations are recorded alike. Classification remains the
responsibility of the CLI command and safety layers rather than the audit logger.

### What is never recorded

Request bodies, response payloads, access tokens, and your identity. The record
is deliberately thin: it tells you *that* `/Admin/GetTitleData` ran against title
`ABC123` and succeeded, not what data was returned. If you need that data, it
belongs in your own change-management system, not in a local log file.

## Relationship to telemetry

The audit log is **not** part of [telemetry](telemetry.md), and the two do not
share a sink. Their requirements are opposites:

| | Telemetry | Audit log |
|---|---|---|
| Destination | Microsoft, over the network | Your machine only |
| Identity | Anonymous — [never records title IDs](telemetry.md) | Attributable — the title ID is the point |
| Default | On, opt **out** | Off, opt **in** |

Routing audit records through the telemetry pipeline would either send title IDs
to Microsoft, breaking the commitment in `telemetry.md`, or strip them and leave
a log that cannot answer which title was changed.

## Failure behavior

Auditing never changes the outcome of an API call: if the log cannot be written,
the command still runs and still returns its normal result. The same holds when
flushing and closing the log at the end of a command fails. The failure is
reported on stderr rather than swallowed, because a silently missing record
leaves you unable to distinguish *"nothing was changed"* from *"the record was
lost"*.

## Known gap

A destructive operation refused by the interactive confirmation prompt is **not**
recorded, because it never reaches the API client. A destructive operation
refused by the client's own fail-closed guard **is** recorded, with
`ExitStatus: 1`.
