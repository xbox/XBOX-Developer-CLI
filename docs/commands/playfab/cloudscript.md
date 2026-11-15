---
title: CloudScript
description: Manage Classic CloudScript JavaScript revisions and the Azure Functions registrations CloudScript uses.
---

# CloudScript

The `playfab cloudscript` command family is Experimental. Its CLI interface might change
or be removed based on feedback.

Manage Classic CloudScript, the JavaScript that PlayFab hosts, with `classic`, and the Azure
Functions that CloudScript calls, with `function`. None of these commands ask before they
run.

## `playfab cloudscript classic`

| Command | PlayFab API path | Options |
| --- | --- | --- |
| `playfab cloudscript classic revision get` | `/Admin/GetCloudScriptRevision` | Optional `--version` and `--revision`; both default to the latest. |
| `playfab cloudscript classic version list` | `/Admin/GetCloudScriptVersions` | None. |
| `playfab cloudscript classic revision create` | `/Admin/UpdateCloudScript` | `--file` (required), a JavaScript file to upload as a new revision. `--publish` also makes the new revision live. |
| `playfab cloudscript classic revision publish` | `/Admin/SetPublishedRevision` | `--version` and `--revision` (both required). |

## `playfab cloudscript function`

`function` registers existing Azure Functions with PlayFab, so CloudScript can call them. It
doesn't create or change anything in Azure.

| Command | PlayFab API path | Options |
| --- | --- | --- |
| `playfab cloudscript function list` | `/CloudScript/ListFunctions`, or with `--type`, `/CloudScript/ListHttpFunctions`, `/CloudScript/ListQueuedFunctions`, or `/CloudScript/ListEventHubFunctions` | Optional `--type`: `http`, `queued`, or `eventhub`. Omit it to list every registration. |
| `playfab cloudscript function register` | `/CloudScript/RegisterHttpFunction` | `--type http`, `--name`, and `--url` (all required); optional `--key-file`. `http` is the only type you can register. |
| `playfab cloudscript function unregister` | `/CloudScript/UnregisterFunction` | `--name` (required). |

### Function keys

An HTTP-triggered Azure Function with function-level authorization, Azure's default, needs
its function key. Pass the key in a file rather than in the URL, so it stays out of shell
history and process listings:

| Option | Description |
| --- | --- |
| `--url <url>` | The function's URL, without its key. A URL with a `code` query parameter is refused. |
| `--key-file <path>` | A file holding the function key, as the Azure portal shows it, or `-` to read it from piped stdin. Surrounding whitespace is trimmed. Omit it for a function that needs no key. |

The CLI adds the key to the URL as `code`, and registers the same URL that the Azure
portal's **Get function URL** shows.

## Table output

These commands accept `--output table`; the others don't. See
[Table output](../../commands.md#table-output).

| Command | Columns |
| --- | --- |
| `playfab cloudscript classic version list` | `VERSION`, `PUBLISHED REVISION`, `LATEST REVISION` |
| `playfab cloudscript function list` | `FUNCTION NAME` |

To see a registration's URL and other details, use JSON output.

```bash
xbox --playfab-title-id ABC123 playfab cloudscript classic version list --output table
```

```text
VERSION  PUBLISHED REVISION  LATEST REVISION
-------  ------------------  ---------------
1        3                   5
2        1                   1
```

## Examples

Check the published version, and read a revision:

```bash
xbox --playfab-title-id ABC123 playfab cloudscript classic version list
xbox --playfab-title-id ABC123 playfab cloudscript classic revision get --version 1 --revision 2
```

Upload `main.js` as a new revision:

```bash
xbox --playfab-title-id ABC123 playfab cloudscript classic revision create --file main.js
```

`create` returns the new revision's `Version` and `Revision`. After you review the revision,
publish it with those numbers:

```bash
xbox --playfab-title-id ABC123 playfab cloudscript classic revision publish --version 1 --revision 3
```

List HTTP function registrations:

```bash
xbox --playfab-title-id ABC123 playfab cloudscript function list --type http
```

Register a function whose key is in `function-key.txt`:

```bash
xbox --playfab-title-id ABC123 playfab cloudscript function register --type http --name MatchWebhook --url https://nodpublishers.azurewebsites.net/api/MatchWebhook --key-file ./function-key.txt
```

To read the key from stdin instead, such as from a secret store:

```bash
get-function-key | xbox --playfab-title-id ABC123 playfab cloudscript function register --type http --name MatchWebhook --url https://nodpublishers.azurewebsites.net/api/MatchWebhook --key-file -
```

Register a function that needs no key:

```bash
xbox --playfab-title-id ABC123 playfab cloudscript function register --type http --name PublicStatus --url https://nodpublishers.azurewebsites.net/api/PublicStatus
```

Unregister `MatchWebhook`:

```bash
xbox --playfab-title-id ABC123 playfab cloudscript function unregister --name MatchWebhook
```
