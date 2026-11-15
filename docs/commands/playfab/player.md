---
title: Player accounts
description: Look up a player's account information from their PlayFab ID.
---

# Player accounts

The `playfab player` command family is Experimental. Its CLI interface might change
or be removed based on feedback.

Read a player's account information.

| Command | PlayFab API path | Description |
| --- | --- | --- |
| `playfab player account get` | `/Admin/GetUserAccountInfo` | Get a player's account from their PlayFab ID. |

`player account get` requires `--playfab-id`, and doesn't accept `--output table`.

```bash
xbox --playfab-title-id ABC123 playfab player account get --playfab-id PLAYER123
```

## Which ID to pass

`--playfab-id` takes the player's **master player account ID**, the PlayFab ID that
PlayFab's player sign-in APIs return.

PlayFab also gives every player a **title player account ID**. It looks the same, 16
hexadecimal characters, and Game Manager shows both, but `GetUserAccountInfo` rejects it:

```console
$ xbox --playfab-title-id ABC123 playfab player account get --playfab-id TITLEPLAYER123
{"code":400,"status":"BadRequest","error":"AccountNotFound","errorCode":1001,"errorMessage":"User not found"}
```

If a lookup fails for a player you know exists, check that you passed the master player
account ID.

The command can't look a player up by email, username, display name, or a platform ID such
as an XBOX user ID. Find the player in Game Manager to get their PlayFab ID first.

## What comes back

The response is the player's full account record, including every linked platform account
and the title player account ID that other commands, such as
[`playfab file`](file.md), take:

```bash
xbox --playfab-title-id ABC123 --data-only playfab player account get --playfab-id PLAYER123
```

```json
{
  "UserInfo": {
    "PlayFabId": "PLAYER123",
    "Created": "2026-08-27T16:03:09.477Z",
    "TitleInfo": {
      "Origination": "CustomId",
      "DisplayName": "Hero",
      "isBanned": false,
      "TitlePlayerAccount": { "Id": "TITLEPLAYER123", "Type": "title_player_account" }
    },
    "SteamInfo": { "SteamId": "76561197960287930", "SteamCountry": "US" },
    "PsnInfo": { "PsnAccountId": "1234567890", "PsnOnlineId": "player1" },
    "PrivateInfo": { "Email": "player@example.com" }
  }
}
```

The record can include personal information, such as the player's email address. PlayFab
recommends not storing it locally, so take care where you save or pipe the output.
