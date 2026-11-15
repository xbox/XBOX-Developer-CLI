---
title: Player data
description: Read and write a player's custom data in title or publisher scope.
---

# Player data

Read and write a player's custom data, either for the title or shared across the
publisher's titles.

| Command | Description |
| --- | --- |
| `playfab player-data get` | Read some or all of the player's keys. |
| `playfab player-data set` | Set one or more keys. |
| `playfab player-data delete` | Remove one or more keys. |

`player-data` commands don't accept `--output table`.

## Options

Every `player-data` command takes these:

| Option | Required | Description |
| --- | --- | --- |
| `--playfab-id` | Yes | The player's PlayFab ID (master player account ID). |
| `--visibility` | No | Data tier: `client` (default), `read-only`, or `internal`. |
| `--publisher` | No | Use the player's publisher data instead of their title data. |

| Command | Options |
| --- | --- |
| `player-data get` | `--key` for one key, or `--keys` for comma-separated keys; omit both for every key. `--value-only` with `--key` prints the bare value. `--if-changed-from-version` returns data only if its version differs; it can't be combined with `--value-only`. |
| `player-data set` | Either `--key` and `--value`, or `--data` with a JSON object of keys and values. Object and array values are stored as JSON strings. `--permission` is required, except with `--visibility internal`, where it isn't valid; see [Permission](#permission). |
| `player-data delete` | `--keys` (required), the comma-separated keys to remove. |

## PlayFab API paths

`--visibility` and `--publisher` select the API:

| Scope and visibility | `get` API | `set` and `delete` API |
| --- | --- | --- |
| title, `client` | `/Admin/GetUserData` | `/Admin/UpdateUserData` |
| title, `read-only` | `/Admin/GetUserReadOnlyData` | `/Admin/UpdateUserReadOnlyData` |
| title, `internal` | `/Admin/GetUserInternalData` | `/Admin/UpdateUserInternalData` |
| publisher, `client` | `/Admin/GetUserPublisherData` | `/Admin/UpdateUserPublisherData` |
| publisher, `read-only` | `/Admin/GetUserPublisherReadOnlyData` | `/Admin/UpdateUserPublisherReadOnlyData` |
| publisher, `internal` | `/Admin/GetUserPublisherInternalData` | `/Admin/UpdateUserPublisherInternalData` |

## Permission

A `client` or `read-only` key is either `Private`, readable only by the player, or
`Public`, readable by other players too. PlayFab gives every key that `set` writes the
permission you pass, replacing the one it had, so `set` requires `--permission` for those
tiers. The permission applies to every key in `--data`; to give keys different
permissions, set them in separate commands.

## Examples

Print the value of a player's `level` key:

```bash
xbox --playfab-title-id ABC123 playfab player-data get --playfab-id PLAYER123 --key level --value-only
```

Set `level` in the player's read-only data, so other players can read it too:

```bash
xbox --playfab-title-id ABC123 playfab player-data set --playfab-id PLAYER123 --visibility read-only --key level --value 5 --permission Public
```

Set `rating` in the player's internal data:

```bash
xbox --playfab-title-id ABC123 playfab player-data set --playfab-id PLAYER123 --visibility internal --key rating --value 1500
```

Remove the player's `level` and `legacyFlag` keys:

```bash
xbox --playfab-title-id ABC123 playfab player-data delete --playfab-id PLAYER123 --keys level,legacyFlag
```
