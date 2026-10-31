---
title: Files
description: List, download, upload, and delete entity files through the PlayFab File API.
---

# Files

## Overview

`playfab file` manages entity files through the PlayFab File API.

```bash
xbox --playfab-title-id ABC123 playfab file <command>
```

Every file command requires the target entity. For player files, use the
player's title player account ID as `--entity-id` and `title_player_account` as
`--entity-type`, not their master player ID;
[`xbox playfab player account get`](player.md) returns it as
`UserInfo.TitleInfo.TitlePlayerAccount.Id`.

| Command | PlayFab API path(s) | Description |
| --- | --- | --- |
| `playfab file list` | `/File/GetFiles` | List file metadata for an entity. |
| `playfab file download` | `/File/GetFiles` plus blob download | Download one file from an entity profile. |
| `playfab file upload` | `/File/InitiateFileUploads`, blob upload, `/File/FinalizeFileUploads`, and `/File/AbortFileUploads` if the upload fails | Upload one local file to an entity profile. |
| `playfab file delete` | `/File/DeleteFiles` | Delete one or more entity files. **Hard-gated by `--confirm` / `--yes` / `-y`.** |

For destructive-operation behavior, see
[Safety and destructive operations](../../safety.md).

## Shared required options

| Option | Description |
| --- | --- |
| `--entity-id <entity-id>` | Entity ID, such as a player's title player account ID. |
| `--entity-type <entity-type>` | Entity type, such as `title_player_account`, `title`, or `character`. |

File commands also inherit global PlayFab flags such as `--playfab-title-id`,
`--verbose`, and `--disable-telemetry`. `--output` sets the format of the metadata that
`playfab file list` returns. `--data-only` has no effect on it, because it returns only the
metadata already. The other three commands return no data, so neither option changes what
they write on success; see [Output](#output).

## Output

`playfab file list` is a data command. It writes the files' metadata to stdout as JSON:
pretty-printed by default, single-line with `--output json-compact`, or as a table with
`--output table`.

`upload`, `download`, and `delete` are file operations, like `cp` and `rm`, not data
commands. They return no JSON and write nothing to stdout. They report what they did on
stderr instead:

| Command | stderr on success |
| --- | --- |
| `playfab file upload` | `Uploading source file: <path>` when it starts, then `Uploaded '<name>' (<bytes> bytes).` |
| `playfab file download` | `Downloaded '<name>' to '<path>' (<bytes> bytes).` |
| `playfab file delete` | `Deleted file: '<name>'`, or `Deleted files: '<name>', '<name>'` for several |

These messages are for people and may change between releases, so a script should check the
exit code: 0 when the operation succeeded, and nonzero when it failed. A failure writes its
error to stderr and nothing to stdout. A PlayFab error is written as its JSON envelope,
which `--output json-compact` puts on a single line. Otherwise, `--output` and `--data-only`
don't change what these commands write, and `--output table` is refused.

```console
$ xbox --playfab-title-id ABC123 playfab file upload --entity-id TITLEPLAYER123 --entity-type title_player_account --file save.json
Uploading source file: D:\NodArena\save.json
Uploaded 'save.json' (2048 bytes).
```

When the file to download isn't on the entity, the error suggests the `file list` command
that shows the entity's files:

```console
$ xbox --playfab-title-id ABC123 playfab file download --entity-id TITLEPLAYER123 --entity-type title_player_account --name save.json
Error: File 'save.json' not found on this entity. To see its files, run:
  xbox --playfab-title-id ABC123 playfab file list --entity-id TITLEPLAYER123 --entity-type title_player_account
```

## `playfab file list`

List files associated with an entity.

```bash
xbox --playfab-title-id ABC123 playfab file list --entity-id TITLEPLAYER123 --entity-type title_player_account
```

| Required input | Description |
| --- | --- |
| `--entity-id <entity-id>` | Entity ID. |
| `--entity-type <entity-type>` | Entity type. |

This is the one file command that supports `--output table`, showing
`FILE NAME`, `LAST MODIFIED`, and `CHECKSUM`. Columns may change between
releases, so use `json` or `json-compact` for scripts.

```bash
xbox --playfab-title-id ABC123 playfab file list --entity-id TITLEPLAYER123 --entity-type title_player_account --output table
```

## `playfab file download`

Download a file from an entity's profile.

```bash
xbox --playfab-title-id ABC123 playfab file download --entity-id TITLEPLAYER123 --entity-type title_player_account --name data.json --dest data.json
```

| Argument / option | Required | Description |
| --- | --- | --- |
| `--entity-id <entity-id>` | Yes | Entity ID. |
| `--entity-type <entity-type>` | Yes | Entity type. |
| `--name <name>` | Yes | Name of the file to download. |
| `--dest <dest>` | No | Local destination file path. Defaults to the PlayFab file name in the current directory; provide this when the PlayFab name is not valid locally or you want a different path. Writing to stdout with `--dest -` isn't implemented yet, and is refused. |

The command is read-only against the title, but it does write locally: it
replaces an existing file at the destination path.

## `playfab file upload`

Upload a local file to an entity's profile.

```bash
xbox --playfab-title-id ABC123 playfab file upload --entity-id TITLEPLAYER123 --entity-type title_player_account --file data.json --name data.json
```

| Argument / option | Required | Description |
| --- | --- | --- |
| `--entity-id <entity-id>` | Yes | Entity ID. |
| `--entity-type <entity-type>` | Yes | Entity type. |
| `--file <file>` | Yes | Path to the local file to upload. Reading from stdin with `--file -` isn't implemented yet, and is refused; to upload a local file named `-`, pass `./-`. |
| `--name <name>` | No | Remote file name. Defaults to the local file name. |

Uploading changes the entity profile by adding or replacing the named file.
Review production-safety expectations before running it against a live title.

### If an upload fails

An upload takes three steps: `/File/InitiateFileUploads` starts it, the CLI
sends the bytes to the storage URL PlayFab returns, and
`/File/FinalizeFileUploads` makes the new file live. Between the first and last
steps, PlayFab holds the upload pending, and a pending upload blocks every later
upload to the entity's files, including the game's own, which fail with
`EntityFileOperationPending`.

So if the upload fails after it starts, whether the transfer fails or times
out, you press Ctrl-C, or the finalize fails, the CLI cancels the pending upload
with `/File/AbortFileUploads`, and says on stderr whether that worked. The
command still exits with a non-zero code, and the entity's files are unchanged
when the cancel succeeds. After Ctrl-C, the command waits up to 15 seconds for
the cancel to finish.

The cancel names the profile version `/File/InitiateFileUploads` returned, so
PlayFab refuses it if anything has changed the entity's files since. That
matters when the CLI can't tell whether the finalize applied, because its
request timed out, was canceled, or got a server error: the cancel can't undo a
finalize that did apply. In that case, the CLI tells you how to check, with
`playfab file list`.

If the CLI can't cancel the pending upload, or can't tell whether the cancel
worked, it says the file may stay locked, and prints the command that cancels
it. On macOS and Linux, that's a command such as:

```bash
printf '%s\n' '{"Entity":{"Id":"TITLEPLAYER123","Type":"title_player_account"},"FileNames":["data.json"]}' | xbox --playfab-title-id ABC123 playfab api /File/AbortFileUploads --body-file -
```

On Windows, it's written for PowerShell:

```powershell
echo '{"Entity":{"Id":"TITLEPLAYER123","Type":"title_player_account"},"FileNames":["data.json"]}' | xbox --playfab-title-id ABC123 playfab api /File/AbortFileUploads --body-file -
```

The command pipes the request body in, rather than passing it with `--body`, so
it works in bash, zsh, and every version of PowerShell. Run it only if later
uploads to the entity's files fail with `EntityFileOperationPending`. It names
no profile version, so it cancels whatever upload of that file is pending,
including one the game started.

The CLI doesn't cancel anything when `/File/InitiateFileUploads` itself fails.
If PlayFab may have started the upload anyway, because the request timed out,
was canceled, or got a server error, the CLI says so and prints the same
command. Without the profile version from Initiate's response, the CLI can't
cancel only its own upload.

## `playfab file delete`

Delete files from an entity's profile.

```bash
xbox --playfab-title-id ABC123 playfab file delete data.json --entity-id TITLEPLAYER123 --entity-type title_player_account --confirm
```

| Argument / option | Required | Description |
| --- | --- | --- |
| `<names...>` | Yes | One or more file names to delete. |
| `--entity-id <entity-id>` | Yes | Entity ID. |
| `--entity-type <entity-type>` | Yes | Entity type. |
| `--confirm` / `--yes` / `-y` | Required outside an interactive terminal | Confirms the destructive, irreversible operation. |

`playfab file delete` calls `/File/DeleteFiles`. It is destructive and
irreversible, so the CLI requires confirmation or an interactive terminal prompt.

## End-to-end example: inspect and download a player file

First list available files for the player entity, then download one named file to
a local path.

```bash
xbox --playfab-title-id ABC123 playfab file list --entity-id TITLEPLAYER123 --entity-type title_player_account --output json-compact
xbox --playfab-title-id ABC123 playfab file download --entity-id TITLEPLAYER123 --entity-type title_player_account --name data.json --dest data.json
```
