---
title: PlayFab
description: Inspect and manage the PlayFab services of one title, and the options and response envelope that every playfab command shares.
---

# PlayFab

`xbox playfab` inspects and manages the PlayFab services of one PlayFab title. Each service
has its own page.

```text
xbox playfab <command> [flags]
```

Every `playfab` command needs a PlayFab title ID, except the studio commands:
`service-principal`, and `api` with a `/Studio/` path. Pass `--playfab-title-id`, or save
a default with `xbox context set playfab-title-id <id>`; see [Context](context.md).

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
| [`experiment`](playfab/experiment.md) | Read a title's A/B experiments, scorecards, treatment assignments, and exclusion groups (Experimental). |
| [`player`](playfab/player.md) | Look up player accounts (Experimental). |
| [`privacy`](playfab/privacy.md) | Export a player's data, and list the titles they've played (Experimental). |
| [`service-principal`](playfab/service-principal.md) | Grant, inspect, and revoke a Microsoft Entra ID service principal's access to a studio and its titles (Experimental). |

## Options

Every `playfab` command accepts these in addition to the
[global options](../commands.md#global-options).

| Option | Description |
| --- | --- |
| `--data-only` | Strip the PlayFab `{code,status,data}` response envelope and write only the `data` field. |
| `--confirm`, `--yes`, `-y` | Confirm a `Delete*` or `Shutdown*` PlayFab API operation. Required outside an interactive terminal. See [Safety and destructive operations](../safety.md). |

## Response envelope

By default, commands write PlayFab's full response envelope:

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

Add `--data-only` to write only the `data` payload, and `--output json-compact` for
single-line JSON:

```bash
xbox --playfab-title-id ABC123 --data-only --output json-compact playfab api /Admin/GetTitleData
```

`file upload`, `file download`, and `file delete` don't return the envelope. They write
nothing to stdout, and report what they did on stderr. See [Files](playfab/file.md#output).
