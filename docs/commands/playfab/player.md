---
title: Player accounts
description: Look up a player's account information from their PlayFab ID.
---

# Player accounts

The `playfab player` command family is Experimental. Its CLI interface may change
or be removed based on feedback.

Read a player's account information.

`player account get` declares no table view, so `--output table` is refused.
`json` (the default) and `json-compact` are the available formats.

| Command | PlayFab API path | Description |
| --- | --- | --- |
| `playfab player account get` | `/Admin/GetUserAccountInfo` | Get a player's account from their PlayFab ID. |

| Command | Options |
| --- | --- |
| `player account get` | `--playfab-id` (required). No email, username, display-name, or platform-ID lookup options are exposed. |

```bash
xbox --playfab-title-id ABC123 playfab player account get --playfab-id PLAYER123
```

## Which ID to pass

`--playfab-id` wants the **master player account ID**.

PlayFab's entity model also gives every player a **title player account ID**. It looks identical —
16 hexadecimal characters — and Game Manager shows both, but it is a different value, and
`GetUserAccountInfo` rejects it:

```console
$ xbox ... playfab player account get --playfab-id TITLEPLAYER123
{"code":400,"status":"BadRequest","error":"AccountNotFound","errorCode":1001,"errorMessage":"User not found"}
```

If a lookup fails for an ID you are confident exists, this is the most likely reason. The legacy
PlayFab ID returned by the login APIs *is* the master player account ID, so anything labelled
"PlayFab ID" will work.

## What comes back

The response is the player's full account record, which includes every linked platform account:

```bash
xbox --playfab-title-id ABC123 --data-only \
  playfab player account get --playfab-id PLAYER123
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

> [!WARNING]
> The response can contain **personally identifying information**, including the player's email
> address. PlayFab's own guidance is not to store it locally. Take care where you pipe this output.

## Identifiers that are not accepted

`GetUserAccountInfo` also matches on **email**, **PlayFab username**, and **title display name**.
None is exposed, because each is unreliable in a different way:

- **Email and username exist only on accounts registered with PlayFab credentials**
  (`RegisterPlayFabUser` / `AddUsernamePassword`). A player who signed in through XBOX, Steam or
  PSN has neither, so on most titles these would fail for every player — an option that looks
  supported but cannot work.
- **A title display name is not guaranteed unique.** Titles can enable *Allow non-unique player
  display names*, so the lookup can return *a* player rather than *the* player. A confidently
  wrong answer is worse than no answer.
- **Email is PII**, as the warning above notes. Accepting it as a *search key* is a different
  privacy posture from looking up an ID you already hold, and this CLI is designed to be driven by
  automation.

If you have one of these, find the player in Game Manager to get their PlayFab ID first.

## Looking a player up by a platform ID

Not supported yet — there is no `--xbox-id`, `--steam-id` or `--psn-id`.

Note the direction, because the gap is smaller than it first appears: going *from* a PlayFab ID
*to* a platform ID already works, since the response above carries every linked account. Only the
reverse is missing.

The reverse lookups are limited, and not in a form this CLI can use today:

- XBOX has `/Profile/GetTitlePlayersFromXboxLiveIDs`, which is in the bundled metadata.
- Steam, PSN, Nintendo, Twitch and Facebook reverse lookups exist only in the
  PlayFab `Server` group. That group is excluded from code generation, so those paths are not part of the CLI's
  surface.
- Profile reverse lookups for Steam, PSN, Nintendo, or a generic provider ID are not in the
  bundled metadata.

Supporting them has a pitfall: these lookups resolve to a *title* player account ID, which must
be translated to the *master* ID before `GetUserAccountInfo` will accept it.
