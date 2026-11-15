---
title: API
description: Call any PlayFab API endpoint directly by its path, with a JSON request body.
---

# API

`playfab api` calls any PlayFab API endpoint by its path, sends the request body you pass,
and writes PlayFab's response as JSON. To find an operation and its request body, see
[Finding an API](../../playfab-api.md#finding-an-api).

```text
xbox playfab api <api-path> [flags]
```

The command needs a PlayFab title, except for studio APIs (`/Studio/*`), which aren't
scoped to one. See [Context](../context.md).

What the command changes depends on the path. Operations whose names start with `Get`,
`List`, or `Search` only read. `Delete*` and `Shutdown*` operations ask first in a
terminal, and need `--confirm` (`--yes`, `-y`) elsewhere. See
[Safety and destructive operations](../../safety.md).

## Arguments and options

| Argument / option | Required | Description |
| --- | --- | --- |
| `<api-path>` | Yes | PlayFab API path, `/<Prefix>/<Operation>`, such as `/Admin/GetTitleData` or `/Catalog/SearchItems`. It must start with `/`. In Git Bash, see [Running from Git Bash on Windows](#running-from-git-bash-on-windows). |
| `--body <body>` | No | The operation's JSON request object, inline. Defaults to `{}` when neither `--body` nor `--body-file` is passed. |
| `--body-file <body-file>` | No | Path to a file holding the JSON request object, or `-` to read it from stdin. |
| `--data-only` | No | Write only the `data` field of PlayFab's response. |
| `--output`, `-o` | No | `json` (default) or `json-compact`. |
| `--verbose` | No | Write diagnostics to stderr, without the request body. |

`--body` and `--body-file` can't be combined. The CLI sends the body as you pass it,
without validating it.

## Examples

```bash
xbox --playfab-title-id ABC123 playfab api /Catalog/GetCatalogConfig
xbox --playfab-title-id ABC123 playfab api /Match/ListMatchmakingQueues --data-only --output json-compact
xbox --playfab-title-id ABC123 playfab api /Catalog/SearchItems --body-file search.json
xbox --playfab-title-id ABC123 playfab api /Admin/GetTitleData --body '{"Keys":["MyKey"]}' --data-only --output json-compact
```

### Read the request body from stdin

```bash
printf '%s' '{"Keys":["MyKey"]}' |
  xbox --playfab-title-id ABC123 playfab api /Admin/GetTitleData --body-file -
```

Stdin supplies one request body, read to the end of input, not one request per line.
With `--body-file -`, `Delete*` and `Shutdown*` operations need `--confirm` even in a
terminal, because stdin can't answer the prompt.

## Running from Git Bash on Windows

Git Bash, like other MSYS shells, converts an argument that starts with `/` into a Windows
path before it starts a Windows program. The CLI refuses the converted path and says how to
fix it:

```console
$ xbox playfab api /Catalog/GetCatalogConfig
The API path must start with '/', such as /Admin/GetTitleData; got 'C:/Program Files/Git/Catalog/GetCatalogConfig'.
That looks like /Catalog/GetCatalogConfig after Git Bash converted it to a Windows path. Turn the conversion off for the command, for example:
  MSYS_NO_PATHCONV=1 xbox playfab api /Catalog/GetCatalogConfig
```

Set `MSYS_NO_PATHCONV=1` for one command:

```bash
MSYS_NO_PATHCONV=1 xbox --playfab-title-id ABC123 playfab api /Catalog/GetCatalogConfig
```

Or export it for the rest of the session:

```bash
export MSYS_NO_PATHCONV=1
```

With the conversion off, Git Bash doesn't convert other paths either, so pass
`--body-file` a relative path or a Windows path, such as `C:/requests/search.json`, not
`/c/requests/search.json`.

## Output

PlayFab's response, in its `{code,status,data}` envelope; see
[Response envelope](../playfab.md#response-envelope). When the call fails, the command
writes the error to stderr and exits with code `1`; see
[Error responses](../../playfab-api.md#error-responses).

`playfab api` doesn't accept `--output table`.
