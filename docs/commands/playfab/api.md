---
title: API
description: Call any PlayFab API endpoint directly by its path, with a JSON request body.
---

# API

`playfab api` calls any PlayFab API endpoint directly by its path. Like
`gh api <endpoint>`, it takes the path itself rather than a subcommand, sends the
request body you give it, and passes PlayFab's response through as JSON.

```bash
xbox playfab api <api-path> [flags]
```

The command needs a title: `--playfab-title-id`, `XBOX_PLAYFAB_TITLE_ID`, or the
title saved with [`xbox context set`](../context.md).

For production-safety guidance before calling mutating APIs, see
[Safety and destructive operations](../../safety.md). For deeper background on
direct endpoint calls, see [PlayFab API integration](../../playfab-api.md).

## Arguments and options

| Argument / option | Required | Description |
| --- | --- | --- |
| `<api-path>` | Yes | PlayFab API path, `/<Prefix>/<Operation>`, for example `/Admin/GetTitleData` or `/Catalog/SearchItems`. It must start with `/`; any other value is refused before a request is sent. In Git Bash, see [Running from Git Bash on Windows](#running-from-git-bash-on-windows). |
| `--body <body>` | No | The operation's JSON request object, inline. Defaults to `{}` if neither `--body` nor `--body-file` is provided. |
| `--body-file <body-file>` | No | Path to a file containing the JSON request object, or `-` to read UTF-8 from stdin until EOF. |
| `--data-only` | No | Output only the PlayFab response's `data` field. |
| `--output` / `-o` | No | Format output as `json` (default) or `json-compact`. `table` is not available for this command. |
| `--verbose` | No | Write API diagnostics to stderr without dumping request-body contents. |

`--body` and `--body-file` are mutually exclusive.

An explicitly supplied body is sent without JSON validation or reformatting,
including empty or whitespace-only content. Only omitting both body options
defaults to `{}`.

## Examples

```bash
xbox --playfab-title-id ABC123 playfab api /Catalog/GetCatalogConfig
xbox --playfab-title-id ABC123 playfab api /Match/ListMatchmakingQueues --data-only --output json-compact
xbox --playfab-title-id ABC123 playfab api /Catalog/SearchItems --body-file search.json
```

### Read the request body from stdin

```bash
printf '%s' '{"Keys":["MyKey"]}' |
  xbox --playfab-title-id ABC123 playfab api /Admin/GetTitleData --body-file -
```

Stdin supplies one complete request body, not one request per line. You can
also enter the body in a terminal and end input with EOF. Reading stops if the
input exceeds `PlayFab:MaxRequestBodyBytes` (1 MiB by default), or if you cancel
with Ctrl-C. An input failure sends no API request.

For `Delete*` and `Shutdown*` operations, `--body-file -` requires `--confirm`,
even in a terminal: stdin is reserved for the request body, not confirmation.
See [Safety and destructive operations](../../safety.md).

### End-to-end example: read selected title data

This example reads a single title-data key and returns only the PlayFab payload
as compact JSON.

```bash
xbox --playfab-title-id ABC123 playfab api /Admin/GetTitleData --body '{"Keys":["MyKey"]}' --data-only --output json-compact
```

## Running from Git Bash on Windows

Git Bash, like other MSYS shells such as MSYS2, converts an argument that starts
with `/` into a Windows path before it starts a Windows program. The API path
reaches the CLI as a path under Git's install directory, so the command is
refused with this error, followed by the command's Help:

```console
$ xbox playfab api /Catalog/GetCatalogConfig
The API path must start with '/', such as /Admin/GetTitleData; got 'C:/Program Files/Git/Catalog/GetCatalogConfig'.
That looks like /Catalog/GetCatalogConfig after Git Bash converted it to a Windows path. Turn the conversion off for the command, for example:
  MSYS_NO_PATHCONV=1 xbox playfab api /Catalog/GetCatalogConfig
```

Set `MSYS_NO_PATHCONV=1` to keep the path as you typed it, for one command or
for the rest of the session:

```bash
MSYS_NO_PATHCONV=1 xbox --playfab-title-id ABC123 playfab api /Catalog/GetCatalogConfig
export MSYS_NO_PATHCONV=1
```

PowerShell and Command Prompt don't convert arguments, so the path needs nothing
extra there.

Leaving off the leading `/` doesn't help, because the path is then refused for
not starting with `/`. Neither does a leading `//`: Git Bash leaves it alone,
but the CLI refuses it as a URL that could name another host, before a request
is sent.

With the conversion off, Git Bash doesn't convert any other argument either,
such as `--body-file /c/requests/search.json`. Give the body file as a relative
path or a Windows path, such as `C:/requests/search.json`.

## Finding an API

PlayFab documents its own API, and that documentation is where to find an
operation, its request and response schemas, which fields are required, and
which credentials it accepts:

- The [PlayFab REST API reference](https://learn.microsoft.com/rest/api/playfab/)
  describes every operation, for people.
- The [PlayFab Swagger specs](https://github.com/PlayFab/API_Specs/tree/master/Swagger/PlayFab)
  hold the same API in machine-readable form, for scripts and agents. The files
  are named for PlayFab's API groups, not for the first segment of the path, so
  find an operation by searching the files' `paths`. For example, `/Catalog` and
  `/Inventory` are in `Economy.swagger.json`, and `/Match`, `/Lobby`, and
  `/MultiplayerServer` are in `Multiplayer.swagger.json`.
- Each operation's `security` field names the credential it accepts:
  `SecretKey` (a title secret key), `EntityToken`, `SessionTicket` (a player
  session), or `None` (no credential, as for player login calls). Some
  operations also list their allowed entity token types at the end of their
  description.

The API path is `/<Prefix>/<Operation>`, such as `/Catalog/SearchItems`, and the
request body is the operation's JSON request object. The prefix doesn't name the
spec file the operation is in.

The CLI calls every endpoint with your Entra ID credentials, in place of a title
secret key or title entity token. Operations that accept only player or
game-server entity tokens aren't authorized this way.

## Output

The endpoint's PlayFab response, passed through as JSON in the
`{code,status,data}` envelope. `--data-only` prints only its `data` field. On an
error the command writes the error JSON to stderr and exits with code 1.

`playfab api` doesn't accept `--output table`. It passes arbitrary JSON through,
so there are no columns to declare:

```console
$ xbox playfab api /Admin/GetTitleData --output table
Output format 'table' is not available for this command, because it does not declare a
table view. Omit --output to get this command's default output.
```

## Safety

The effect depends on the API path. Operations whose names start with `Get`,
`List`, or `Search` are read-only; every other operation changes production
title state. `Delete*` and `Shutdown*` operations also require `--confirm`
(`--yes`, `-y`). See [Safety and destructive operations](../../safety.md).
