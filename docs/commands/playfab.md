---
title: PlayFab
description: Inspect and manage the PlayFab services of one title, and the options and response envelope that every playfab command shares.
---

# PlayFab

`xbox playfab` inspects and manages the PlayFab services of one title. Each service
has its own page.

```bash
xbox playfab <command> [flags]
```

## Commands

| Command | Description |
| --- | --- |
| [`api`](playfab/api.md) | Call any PlayFab API endpoint directly. |
| [`mps`](playfab/mps.md) | Manage Multiplayer Servers (MPS) (Experimental). |
| [`file`](playfab/file.md) | Manage entity files. |
| [`title-data`](playfab/title-data.md) | Manage title data key/value pairs. |
| [`api-policy`](playfab/api-policy.md) | Manage the title's API access policy. |
| [`cloudscript`](playfab/cloudscript.md) | Manage scripts and function registrations (Experimental). |
| [`group`](playfab/group.md) | Manage groups and membership (Experimental). |
| [`player-data`](playfab/player-data.md) | Manage title- or publisher-scoped player data. |
| [`economy`](playfab/economy.md) | Manage Economy v2 inventory. |
| [`experiment`](playfab/experiment.md) | Inspect a title's A/B experiments, scorecards, treatment assignments, and exclusion groups (read-only) (Experimental). |
| [`player`](playfab/player.md) | Look up player accounts (Experimental). |
| [`privacy`](playfab/privacy.md) | Master-player-account privacy operations (data export, played-title list) (Experimental). |

## Target title

Every `playfab` command calls PlayFab and needs a title ID. Pass `--playfab-title-id`, set
`XBOX_PLAYFAB_TITLE_ID` for the session, or save a default with
[`xbox context set playfab-title-id <id>`](context.md).

## Options

Every `playfab` command accepts these in addition to the
[global options](../commands.md#global-options).

| Option | Description |
| --- | --- |
| `--data-only` | Strip the PlayFab `{code,status,data}` response envelope and emit only the `data` field. |
| `--confirm`, `--yes`, `-y` | Confirm a destructive, irreversible operation, such as a `Delete*` or `Shutdown*` PlayFab API. Required outside an interactive terminal. See [Safety and destructive operations](../safety.md). |

## Response envelope

By default, API-backed commands emit the full PlayFab response envelope:

```json
{
  "code": 200,
  "status": "OK",
  "data": {
    "Data": {
      "SettingA": "the value"
    }
  }
}
```

Use `--data-only` to emit only the `data` payload, and `--output json-compact`
(or `-o json-compact`) to produce single-line JSON:

```bash
xbox --playfab-title-id ABC123 --data-only --output json-compact playfab api /Admin/GetTitleData
```

`file upload`, `file download`, and `file delete` don't return the envelope. They're file
operations, not data commands: they write nothing to stdout and report what they did on
stderr. See [Files](playfab/file.md#output).
