---
title: Privacy
description: Queue an export of a master player account's data, and list the titles a player has played.
---

# Privacy

The `playfab privacy` command family is Experimental. Its CLI interface might change
or be removed based on feedback.

Export a player's data, and list the titles they've played.

| Command | PlayFab API path | Description |
| --- | --- | --- |
| `playfab privacy export` | `/Admin/ExportMasterPlayerData` | Queue an export of the player's data, which PlayFab emails to the title's notification address. |
| `playfab privacy played-titles` | `/Admin/GetPlayedTitleList` | List the titles the player has played. |

Both commands require `--playfab-id`, the player's PlayFab ID (master player account ID).
`privacy` commands don't accept `--output table`.

List the titles a player has played:

```bash
xbox --playfab-title-id ABC123 playfab privacy played-titles --playfab-id PLAYER123
```

## What the export sends, and to whom

> [!WARNING]
> `privacy export` doesn't return the player's data. It runs without asking, and when the
> export is ready, PlayFab emails a download link to the notification email address
> configured for the title. Once queued, the export and its email can't be recalled.

The command returns only a `JobReceiptId`, and there's no command to check the job. The
export covers the master player account, so it holds the player's data from every title of
the publisher they played, not only the title you named. `privacy played-titles` lists
those titles.

PlayFab's [Export player data](https://learn.microsoft.com/gaming/playfab/data-analytics/privacy-compliance/gdpr-exporting-player-data)
describes what the export holds, every way PlayFab delivers the download link, and how long
the link lasts. Its
[Deleting and exporting player data](https://learn.microsoft.com/gaming/playfab/data-analytics/privacy-compliance/playfab-gdpr-deleting-and-exporting-player-data)
covers finding all of a player's accounts and verifying the request before you export.

After you verify the request, queue the export:

```bash
xbox --playfab-title-id ABC123 playfab privacy export --playfab-id PLAYER123
```
