---
title: CloudScript
description: Manage legacy Cloud Script JavaScript revisions and the Azure Functions registered with Cloud Script.
---

# CloudScript

The `playfab cloudscript` command family is Experimental. Its CLI interface may change
or be removed based on feedback.

Manage legacy PlayFab-hosted JavaScript Cloud Script and Azure Functions
registrations used by Cloud Script. The two subgroups are `classic` and
`function`.

Mutating commands change production title state and require explicit operator
review. None of the CloudScript commands are backed by `Delete*` or `Shutdown*`
PlayFab operations, so they are not hard-gated by `--confirm`; see
[Safety and destructive operations](../../safety.md).

## `playfab cloudscript classic`

`classic` manages PlayFab-hosted JavaScript Cloud Script revisions and versions.
Revision commands live under an extra `revision` noun, and version listing lives
under `version list`.

| Command | PlayFab API path | Options | Safety |
| --- | --- | --- | --- |
| `playfab cloudscript classic revision get` | `/Admin/GetCloudScriptRevision` | Optional `--version`, `--revision`; both default to latest. | Read-only. |
| `playfab cloudscript classic version list` | `/Admin/GetCloudScriptVersions` | No command-specific options. | Read-only. |
| `playfab cloudscript classic revision create` | `/Admin/UpdateCloudScript` | `--file` required; optional `--publish` immediately publishes the uploaded revision. | Mutating; not hard-gated. |
| `playfab cloudscript classic revision publish` | `/Admin/SetPublishedRevision` | `--version` and `--revision` required. | Mutating; not hard-gated. |

`revision create` creates a new Cloud Script revision from one local JavaScript
file. `--publish` turns that upload into a publish action, so review it like any
other production write.

## `playfab cloudscript function`

`function` manages Azure Functions registrations in PlayFab. Registration records
point PlayFab at existing Azure Function endpoints; the CLI does not create,
modify, or delete Azure resources.

| Command | PlayFab API path | Options | Safety |
| --- | --- | --- | --- |
| `playfab cloudscript function list` | `/CloudScript/ListFunctions`, `/CloudScript/ListHttpFunctions`, `/CloudScript/ListQueuedFunctions`, `/CloudScript/ListEventHubFunctions` | Optional `--type` filter: `http`, `queued`, or `eventhub`; omit for all registrations. | Read-only. |
| `playfab cloudscript function register` | `/CloudScript/RegisterHttpFunction` | `--type`, `--name`, and `--url` required; optional `--key-file`. The only supported registration `--type` is `http`. | Mutating; not hard-gated. |
| `playfab cloudscript function unregister` | `/CloudScript/UnregisterFunction` | `--name` required. | Mutating; not hard-gated. |

The list command selects a narrower PlayFab API path when `--type` is supplied;
omitting `--type` calls the all-functions list API.

### Function keys

An HTTP-triggered Azure Function with function-level authorization, Azure's
default, needs its function key, which Azure reads from the URL's `code` query
parameter. `register` doesn't take the key in `--url`, because an option's value
is visible in shell history and process listings. Pass `--url` without `code`,
and the key with `--key-file`:

| Option | Meaning |
| --- | --- |
| `--url <url>` | The function's URL, without its key. A `--url` with a `code` query parameter is refused, and the error doesn't repeat the URL. |
| `--key-file <path>` | A file holding the function key, as the Azure portal shows it, or `-` to read it from stdin. Stdin must be piped: `-` is refused when stdin is a terminal, because the key would show on screen as it's typed. Surrounding whitespace, such as a trailing newline, is trimmed. Omit it for a function that needs no key, such as one with anonymous authorization. |

The CLI adds the key to the URL as `code` and sends PlayFab the complete URL,
the same one the Azure portal's **Get function URL** shows.

## Table output

`--output table` is opt-in per command. Two `cloudscript` commands declare a
table view; every other `cloudscript` command refuses `table` rather than
quietly returning JSON:

```console
$ xbox --playfab-title-id ABC123 playfab cloudscript classic revision get --output table
Output format 'table' is not available for this command, because it does not declare a
table view. Omit --output to get this command's default output.
```

Each command's own Help lists its columns under a `TABLE OUTPUT` heading. The
columns are a human convenience and **not a stability contract** — use `json` or
`json-compact` for scripts and agents.

| Command | Columns |
| --- | --- |
| `playfab cloudscript classic version list` | `VERSION`, `PUBLISHED REVISION`, `LATEST REVISION` |
| `playfab cloudscript function list` | `FUNCTION NAME` |

`function list` renders a single column because the command dispatches to four
different PlayFab list APIs depending on `--type`, and the function name is the
only field all four responses share. Any other column would render as dashes for
three of the four types, so use `json` to read trigger-specific fields such as a
registration's URL.

The remaining commands deliberately have no view:

| Command | Why |
| --- | --- |
| `playfab cloudscript classic revision get` | A single record whose payload is a `Files` array carrying the Cloud Script source. A flat table would drop the file contents, which are the point of the command. |
| `playfab cloudscript classic revision create` | Returns an acknowledgement — the new version and revision numbers — not a collection. |
| `playfab cloudscript classic revision publish` | Returns an empty acknowledgement, so there is nothing to tabulate. |
| `playfab cloudscript function register` | Returns an empty acknowledgement, so there is nothing to tabulate. |
| `playfab cloudscript function unregister` | Returns an empty acknowledgement, so there is nothing to tabulate. |

`--data-only` has no effect with `--output table`: a table has no response
envelope to strip. A collection view prints `No results.` when the collection is
empty, rather than headers with no rows.

```bash
xbox --playfab-title-id ABC123 playfab cloudscript classic version list --output table
```

```text
VERSION  PUBLISHED REVISION  LATEST REVISION
-------  ------------------  ---------------
1        3                   5
2        1                   1
```

See [Commands](../../commands.md) for the general output rules.

## Examples

Inspect the current published Cloud Script version, upload a new revision, then
publish a specific revision after review:

```bash
xbox --playfab-title-id ABC123 playfab cloudscript classic version list --data-only --output json-compact
xbox --playfab-title-id ABC123 playfab cloudscript classic revision get --version 1 --revision 2
xbox --playfab-title-id ABC123 playfab cloudscript classic revision create --file main.js
xbox --playfab-title-id ABC123 playfab cloudscript classic revision publish --version 1 --revision 3
```

List HTTP function registrations, register an HTTP-triggered Azure Function
whose key is in `function-key.txt`, and register one that needs no key:

```bash
xbox --playfab-title-id ABC123 playfab cloudscript function list --type http
xbox --playfab-title-id ABC123 playfab cloudscript function register --type http --name MatchWebhook --url https://nodpublishers.azurewebsites.net/api/MatchWebhook --key-file ./function-key.txt
xbox --playfab-title-id ABC123 playfab cloudscript function register --type http --name PublicStatus --url https://nodpublishers.azurewebsites.net/api/PublicStatus
xbox --playfab-title-id ABC123 playfab cloudscript function unregister --name MatchWebhook
```

Read the key from stdin instead, for example from a secret store:

```bash
get-function-key | xbox --playfab-title-id ABC123 playfab cloudscript function register --type http --name MatchWebhook --url https://nodpublishers.azurewebsites.net/api/MatchWebhook --key-file -
```
