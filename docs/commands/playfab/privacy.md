---
title: Privacy
description: Queue an export of a master player account's data, and list the titles a player has played.
---

# Privacy

The `playfab privacy` command family is Experimental. Its CLI interface may change
or be removed based on feedback.

Master-player-account privacy operations.

No `privacy` command declares a table view, so `--output table` is refused on both.
`json` (the default) and `json-compact` are the available formats.

> [!WARNING]
> `privacy export` doesn't return the player's data. When the export is ready,
> PlayFab emails a download link to the notification email address configured
> for the title. The export holds the player's data from every title of the
> publisher they played. Once queued, the export and its email can't be
> recalled. See
> [What the export sends, and to whom](#what-the-export-sends-and-to-whom).
>
> Its operation, `ExportMasterPlayerData`, does not begin with `Get`, `List`, or
> `Search`, so it is classified as **mutating** rather than read-only. It is not
> covered by the `--confirm` gate, which applies only to `Delete*` and `Shutdown*`
> operations. See [Safety and destructive operations](../../safety.md) before
> running it.

| Command | PlayFab API path | Description |
| --- | --- | --- |
| `playfab privacy export` | `/Admin/ExportMasterPlayerData` | Queue a full export of a master player account's data. Mutating. |
| `playfab privacy played-titles` | `/Admin/GetPlayedTitleList` | List the titles a player has played. Read-only. |

| Command | Options |
| --- | --- |
| `privacy export` | `--playfab-id` (required). |
| `privacy played-titles` | `--playfab-id` (required). |

```bash
xbox --playfab-title-id ABC123 playfab privacy export --playfab-id PLAYER123
xbox --playfab-title-id ABC123 playfab privacy played-titles --playfab-id PLAYER123
```

`export` only *queues* the job and returns a `JobReceiptId`. There is no `--wait`
option because the PlayFab API surface exposes no job-status endpoint to poll.

## What the export sends, and to whom

The command returns only a `JobReceiptId`, not the player's data. When the
export is ready, PlayFab emails a link to download it to the notification email
address configured for the title.

The export covers the master player account, so it holds the player's data from
every title of the publisher they played, not only the title you named.
`privacy played-titles` lists those titles. Once queued, the export and its
email can't be recalled.

PlayFab's [Export player data](https://learn.microsoft.com/gaming/playfab/data-analytics/privacy-compliance/gdpr-exporting-player-data)
describes what the export holds, every way PlayFab delivers the download link,
and how long the link lasts. Its
[Deleting and exporting player data](https://learn.microsoft.com/gaming/playfab/data-analytics/privacy-compliance/playfab-gdpr-deleting-and-exporting-player-data)
covers finding all of a player's accounts and verifying the request before you
export.
