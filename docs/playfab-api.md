---
title: PlayFab API integration
description: Find and call PlayFab APIs from the terminal with your Entra ID developer credentials instead of a title secret key.
---

# PlayFab API integration

The XBOX Developer CLI can call any PlayFab API that accepts title secret key or
title entity token authentication, using Entra ID developer credentials instead.
Operations that accept only player or game-server entity tokens aren't
authorized this way.

## Requirements

The signed-in developer must have the **Admin** permission on the target PlayFab
title.

## Setup

1. Sign in: `xbox auth login`
2. Set your title ID via one of, highest precedence first:
   - the `--playfab-title-id` option,
   - the `XBOX_PLAYFAB_TITLE_ID` environment variable, or
   - a default saved with [`xbox context set playfab-title-id <id>`](commands/context.md).

See [Context](commands/context.md) for how these sources layer together.

Replace `ABC123` below with your PlayFab title ID. Saving this default affects
other CLI sessions using the same signed-in account and profile.

Sign in, save the title, inspect the saved value, and make a read-only request:

```bash
xbox auth login
xbox context set playfab-title-id ABC123
xbox context get
xbox playfab api /Admin/GetTitleData
```

The response uses the JSON envelope shown in [Get title data](#get-title-data).
The server rejects calls when the signed-in developer lacks Admin permission;
saving a title ID does not grant access.

## Finding an API

PlayFab documents every operation, its request and response schemas, which
fields are required, and which credentials it accepts:

- The [PlayFab REST API reference](https://learn.microsoft.com/rest/api/playfab/)
  is written for people.
- The [PlayFab Swagger specs](https://github.com/PlayFab/API_Specs/tree/master/Swagger/PlayFab)
  are machine-readable. Each file lists its operations by path. The files are
  named for PlayFab's API groups, not for the path's first segment, so search
  the files' `paths` for the operation: `/Catalog` and `/Inventory` are in
  `Economy.swagger.json`, and `/Match`, `/Lobby`, and `/MultiplayerServer` are
  in `Multiplayer.swagger.json`.
- Each operation's `security` field names the credential it accepts:
  `SecretKey` (a title secret key), `EntityToken`, `SessionTicket` (a player
  session), or `None` (no credential, as for player login calls). Some
  operations also list their allowed entity token types in their description.

The API path is `/<Prefix>/<Operation>`, such as `/Admin/GetTitleData`, and the
request body is the operation's JSON request object. The prefix doesn't name the
spec file the operation is in.

## `playfab api`

Call any PlayFab API endpoint by its path. The path is a positional argument.
`--body` accepts inline JSON, and `--body-file` reads the JSON request body from
a file or, with `--body-file -`, from UTF-8 stdin until EOF. If neither is
supplied, the request body defaults to `{}`. An explicitly supplied body is
sent without JSON validation or reformatting, even when empty or whitespace-only.

> [!NOTE]
> `--body` and `--body-file` are mutually exclusive — specify at most one.

### Read request JSON from stdin

```bash
printf '%s' '{"Keys":["MyKey"]}' |
  xbox --playfab-title-id ABC123 playfab api /Admin/GetTitleData --body-file -
```

This sends one request with the entire input body. For terminal input, end
the body with EOF. The configured request-size limit still applies, and
Ctrl-C cancels reading. With stdin reserved for the body, destructive
operations require `--confirm` rather than an interactive confirmation.

### Get title data

```bash
xbox --playfab-title-id ABC123 playfab api /Admin/GetTitleData
```

Example output:

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

### Set title data with inline request JSON

```bash
xbox --playfab-title-id ABC123 playfab api /Admin/SetTitleData --body '{ "Key": "MyKey", "Value": "my-value" }'
```

Example output:

```json
{
  "code": 200,
  "status": "OK",
  "data": {}
}
```

> [!WARNING]
> `SetTitleData` changes production title configuration. Before using mutating
> PlayFab APIs, read [Safety and destructive operations](safety.md).

### Set title data with request JSON from a file

```powershell
"{ ""Key"": ""MyKey"", ""Value"": ""my-value"" }" > data.json
.\xbox --playfab-title-id ABC123 playfab api /Admin/SetTitleData --body-file .\data.json
```

Example output:

```json
{
  "code": 200,
  "status": "OK",
  "data": {}
}
```

### Get a specific title data key

```bash
xbox --playfab-title-id ABC123 playfab api /Admin/GetTitleData --body '{"Keys":["MyKey"]}'
```

Example response:

```json
{
  "code": 200,
  "status": "OK",
  "data": {
    "Data": {
      "MyKey": "my-value"
    }
  }
}
```

## Shaping the output

### Extract only the `data` field (`--data-only`)

Use `--data-only` to unwrap the PlayFab response envelope and receive only the
`data` payload:

```bash
xbox --playfab-title-id ABC123 --data-only playfab api /Admin/GetTitleData
```

Example output:

```json
{
  "Data": {
    "MyKey": "my-value"
  }
}
```

### Compact single-line output for piping (`--output json-compact`)

Use `--output json-compact` for single-line JSON output, suitable for piping
into other tools. The default is `--output json`, which pretty-prints JSON.

```bash
xbox --playfab-title-id ABC123 --output json-compact playfab api /Admin/GetTitleData
```

Example output:

```json
{"code":200,"status":"OK","data":{"Data":{"MyKey":"my-value"}}}
```

### Combine `--data-only` and `--output json-compact`

```bash
xbox --playfab-title-id ABC123 --data-only --output json-compact playfab api /Admin/GetTitleData
```

Example output:

```json
{"Data":{"MyKey":"my-value"}}
```

## Error responses

- On error (exit code `1`), the full error JSON is written to **stderr**,
  formatted per `--output`.
- With `--data-only`, nothing is written to stdout on error; the full error JSON
  still goes to stderr.
- Non-JSON errors (network failures, etc.) continue to produce human-readable
  stderr messages.

## Destructive operations

Catastrophic irreversible PlayFab operations, such as `Delete*` endpoints and
`/MultiplayerServer/ShutdownMultiplayerServer`, require explicit confirmation.
See [Safety and destructive operations](safety.md) for the safety model and command
examples.

## Authentication flow

The CLI sends an Entra ID access token using the Bearer scheme
in the `Authorization` header. No
`X-SecretKey` or `X-EntityToken` headers are used. The PlayFab server validates
the token and checks that the developer has Admin permission on the target title.

See [Authentication](authentication.md) for more details.
