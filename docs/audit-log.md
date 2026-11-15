---
title: Audit logging
description: Keep an opt-in, local, metadata-only record of the PlayFab operations the CLI performs.
---

# Audit logging

The XBOX CLI can keep a local record of which PlayFab operations it ran, against
which title, and when. Use it to see afterward what you, or an agent running the CLI for you,
did. The log is off by default, stays on your machine, and holds no request bodies, responses,
tokens, or identity.

The log records PlayFab calls only. Commands that work with Partner Center and XBOX services,
such as `xbox xbl` and `xbox game`, aren't recorded.

## Turn on the audit log

Set `XBOX_AUDITLOG__ENABLED` to `true`. In bash or zsh:

```bash
export XBOX_AUDITLOG__ENABLED=true
```

In PowerShell:

```powershell
$env:XBOX_AUDITLOG__ENABLED = "true"
```

## Where the log lives

The CLI writes one file a day and keeps the most recent 31:

| Platform | Path |
|----------|------|
| Windows  | `%LOCALAPPDATA%\XboxDevCLI\audit<date>.jsonl` |
| Linux    | `$XDG_DATA_HOME/XboxDevCLI/audit<date>.jsonl`, defaulting to `~/.local/share` |
| macOS    | `~/Library/Application Support/XboxDevCLI/audit<date>.jsonl` |

The files have no size limit, so if you make many API calls a day, watch your disk space.

## What is recorded

Each PlayFab call adds one line of [compact JSON](https://clef-json.org/), whether the call
reads or changes something:

```json
{"@t":"2026-08-25T16:59:47.3966336Z","@mt":"PlayFab invocation {ResolvedApiPath} {TitleId} {ExitStatus}","ResolvedApiPath":"/Admin/GetTitleData","TitleId":"ABC123","ExitStatus":0}
```

| Field | Meaning |
|-------|---------|
| `@t` | UTC time of the call |
| `ResolvedApiPath` | The PlayFab API path, without a query string |
| `TitleId` | The PlayFab title the call targeted, or `null` for a studio API (`/Studio/*`) |
| `ExitStatus` | `0` when the call succeeded, `1` when it failed |

A destructive operation that you decline at the confirmation prompt isn't recorded, because
it never calls PlayFab.

## When the log can't be written

The command still runs and returns the same result, and the CLI prints a warning on stderr so
you know the record is missing.

## Audit log and telemetry

The audit log isn't part of [telemetry](telemetry.md):

| | Telemetry | Audit log |
|---|---|---|
| Destination | Microsoft | Your machine only |
| PlayFab title IDs | Never recorded | Recorded |
| Default | On; opt out | Off; opt in |
