---
title: Player data
description: Read and write a player's custom data in title or publisher scope.
---

# Player data

Read and write a player's custom data in title or publisher scope.

No `player-data` command declares a table view, so `--output table` is refused
on all three. `json` (the default) and `json-compact` are the available
formats. Unlike
[`playfab title-data get`](title-data.md),
`player-data get` has no positional key argument and still requires the explicit
`--value-only` flag to print a bare value.

Common options:

| Option | Required | Description |
| --- | --- | --- |
| `--playfab-id` | Yes | Unique PlayFab assigned ID of the user to operate on. |
| `--visibility` | No | Data tier: `client` (default), `read-only`, or `internal`. |
| `--publisher` | No | Target publisher-scoped data instead of title-scoped data. |

Endpoint selection:

| Scope / visibility | `get` API | `set` / `delete` API |
| --- | --- | --- |
| title, `client` | `/Admin/GetUserData` | `/Admin/UpdateUserData` |
| title, `read-only` | `/Admin/GetUserReadOnlyData` | `/Admin/UpdateUserReadOnlyData` |
| title, `internal` | `/Admin/GetUserInternalData` | `/Admin/UpdateUserInternalData` |
| publisher, `client` | `/Admin/GetUserPublisherData` | `/Admin/UpdateUserPublisherData` |
| publisher, `read-only` | `/Admin/GetUserPublisherReadOnlyData` | `/Admin/UpdateUserPublisherReadOnlyData` |
| publisher, `internal` | `/Admin/GetUserPublisherInternalData` | `/Admin/UpdateUserPublisherInternalData` |

Command-specific options:

| Command | Options |
| --- | --- |
| `player-data get` | `--keys` for comma-separated keys; `--key` for one key; `--value-only` with `--key`; `--if-changed-from-version` for data-version filtering. `--key` and `--keys` are mutually exclusive. `--value-only` cannot be combined with `--if-changed-from-version`. |
| `player-data set` | Either `--key` plus `--value`, or `--data` containing a JSON object. Object and array values are stored as JSON strings. `--permission` is required unless `--visibility` is `internal`, where it isn't valid; see [Permission](#permission). Mutating. |
| `player-data delete` | `--keys` (required), a comma-separated list of keys to remove via the update endpoint's `KeysToRemove`. Mutating; this is not a `Delete*` API hard gate. |

```bash
xbox --playfab-title-id ABC123 playfab player-data get --playfab-id PLAYER123 --key level --value-only
xbox --playfab-title-id ABC123 playfab player-data set --playfab-id PLAYER123 --visibility read-only --key level --value 5 --permission Public
xbox --playfab-title-id ABC123 playfab player-data set --playfab-id PLAYER123 --visibility internal --key rating --value 1500
xbox --playfab-title-id ABC123 playfab player-data delete --playfab-id PLAYER123 --keys level,legacyFlag
```

## Permission

A `client` or `read-only` key is either `Private`, readable only by the player, or
`Public`, readable by other players too. PlayFab gives every key a request writes the
permission named in that request, replacing the one the key had, and makes the keys
`Private` when the request names none. So `player-data set` requires `--permission`
for those tiers rather than letting a value change quietly make a `Public` key
`Private`. `--permission` accepts `Private` or `Public`, applies to every key in
`--data`, and is refused with `--visibility internal`, which only the server can read.
To give keys different permissions, set them in separate commands.
