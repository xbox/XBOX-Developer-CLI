---
title: PlayFab API integration
description: Find and call PlayFab APIs from the terminal with your Microsoft Entra ID developer credentials instead of a title secret key.
---

# PlayFab API integration

The XBOX CLI can call any PlayFab API that accepts a title secret key or a title
entity token, using your Microsoft Entra ID developer credentials instead. APIs that accept
only player or game-server entity tokens aren't authorized this way.

You need the **Admin** permission on the PlayFab title. See
[How PlayFab authentication works](authentication.md#how-playfab-authentication-works).

## Setup

Sign in, save your PlayFab title ID as the default, and make a read-only request.
Replace `ABC123` with your title ID.

```bash
xbox auth login
xbox context set playfab-title-id ABC123
xbox playfab api /Admin/GetTitleData
```

To use another title for one command, pass `--playfab-title-id`. See
[Context](commands/context.md).

## Finding an API

PlayFab documents every operation, its request and response schemas, which fields are
required, and which credentials it accepts:

- The [PlayFab REST API reference](https://learn.microsoft.com/rest/api/playfab/) is
  written for people.
- The [PlayFab Swagger specs](https://github.com/PlayFab/API_Specs/tree/master/Swagger/PlayFab)
  are machine-readable. The files are named for PlayFab's API groups, not for the path's
  first segment, so search the files' `paths` for the operation. For example, `/Catalog`
  and `/Inventory` are in `Economy.swagger.json`, and `/Match`, `/Lobby`, and
  `/MultiplayerServer` are in `Multiplayer.swagger.json`.
- Each operation's `security` field names the credential it accepts: `SecretKey` (a title
  secret key), `EntityToken`, `SessionTicket` (a player session), or `None`. Some
  operations also list their allowed entity token types in their description.

The API path is `/<Prefix>/<Operation>`, such as `/Admin/GetTitleData`, and the request
body is the operation's JSON request object.

## Examples

These examples call the API with [`playfab api`](commands/playfab/api.md), which has the
full option reference. `Delete*` and `Shutdown*` operations ask first in a terminal, and need
`--confirm` elsewhere; see [Safety and destructive operations](safety.md).

### Get title data

```bash
xbox --playfab-title-id ABC123 playfab api /Admin/GetTitleData
```

```json
{
  "code": 200,
  "status": "OK",
  "data": {
    "Data": {
      "SettingA": "the value",
      "SettingB": "another value"
    }
  }
}
```

### Get one title data key

```bash
xbox --playfab-title-id ABC123 playfab api /Admin/GetTitleData --body '{"Keys":["MyKey"]}'
```

### Set title data

Pass the request body inline with `--body`:

```bash
xbox --playfab-title-id ABC123 playfab api /Admin/SetTitleData --body '{ "Key": "MyKey", "Value": "my-value" }'
```

```json
{
  "code": 200,
  "status": "OK",
  "data": {}
}
```

Or read it from a file with `--body-file`, here in PowerShell:

```powershell
'{ "Key": "MyKey", "Value": "my-value" }' > data.json
xbox --playfab-title-id ABC123 playfab api /Admin/SetTitleData --body-file data.json
```

### Get only the data, on one line

`--data-only` drops the response envelope, and `--output json-compact` writes single-line
JSON for piping:

```bash
xbox --playfab-title-id ABC123 --data-only --output json-compact playfab api /Admin/GetTitleData
```

```json
{"Data":{"MyKey":"my-value"}}
```

## Error responses

A failed call exits with code `1`:

- When PlayFab returns an error, the command writes PlayFab's error JSON to stderr,
  formatted per `--output`.
- Other failures, such as network errors, write a plain-text message to stderr.
- With `--data-only`, stdout is empty on an error.
