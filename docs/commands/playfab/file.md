---
title: Files
description: List, download, upload, and delete entity files through the PlayFab File API.
---

# Files

`playfab file` manages the files stored on a PlayFab entity, such as a player.

```text
xbox --playfab-title-id ABC123 playfab file <command>
```

Every file command requires `--entity-id` and `--entity-type`. For a player, pass their
title player account ID with `--entity-type title_player_account`, not their master player
account ID; [`playfab player account get`](player.md) returns it as
`UserInfo.TitleInfo.TitlePlayerAccount.Id`.

| Command | PlayFab API path | Description |
| --- | --- | --- |
| `playfab file list` | `/File/GetFiles` | List an entity's files. |
| `playfab file download` | `/File/GetFiles` | Download one file. |
| `playfab file upload` | `/File/InitiateFileUploads`, `/File/FinalizeFileUploads`, and `/File/AbortFileUploads` if the upload fails | Upload one local file, adding or replacing it. |
| `playfab file delete` | `/File/DeleteFiles` | Delete one or more files. Asks first; see [Safety and destructive operations](../../safety.md). |

## Output

`playfab file list` writes the files' metadata to stdout as JSON, or as a table with
`--output table`, which shows `FILE NAME`, `LAST MODIFIED`, and `CHECKSUM`.

`upload`, `download`, and `delete` are file operations, like `cp` and `rm`. They write
nothing to stdout, and report what they did on stderr:

| Command | stderr on success |
| --- | --- |
| `playfab file upload` | `Uploading source file: <path>` when it starts, then `Uploaded '<name>' (<bytes> bytes).` |
| `playfab file download` | `Downloaded '<name>' to '<path>' (<bytes> bytes).` |
| `playfab file delete` | `Deleted file: '<name>'`, or `Deleted files: '<name>', '<name>'` for several |

These messages might change between releases, so scripts should check the exit code: `0`
when the operation succeeded, and nonzero when it failed, with the error on stderr. These
three commands don't accept `--output table`, and `--data-only` doesn't change what they
write.

## `playfab file list`

```bash
xbox --playfab-title-id ABC123 playfab file list --entity-id TITLEPLAYER123 --entity-type title_player_account
xbox --playfab-title-id ABC123 playfab file list --entity-id TITLEPLAYER123 --entity-type title_player_account --output table
```

## `playfab file download`

| Option | Required | Description |
| --- | --- | --- |
| `--entity-id <entity-id>` | Yes | Entity ID. |
| `--entity-type <entity-type>` | Yes | Entity type. |
| `--name <name>` | Yes | Name of the file to download. |
| `--dest <dest>` | No | Local path to write. Defaults to the file's name in the current directory. Pass it when that name isn't valid locally. `-` for stdout isn't supported. |

`download` replaces a local file that's already at the destination path.

```bash
xbox --playfab-title-id ABC123 playfab file download --entity-id TITLEPLAYER123 --entity-type title_player_account --name data.json --dest data.json
```

When the entity has no file with that name, the error shows the `file list` command to run:

```console
$ xbox --playfab-title-id ABC123 playfab file download --entity-id TITLEPLAYER123 --entity-type title_player_account --name save.json
Error: File 'save.json' not found on this entity. To see its files, run:
  xbox --playfab-title-id ABC123 playfab file list --entity-id TITLEPLAYER123 --entity-type title_player_account
```

## `playfab file upload`

| Option | Required | Description |
| --- | --- | --- |
| `--entity-id <entity-id>` | Yes | Entity ID. |
| `--entity-type <entity-type>` | Yes | Entity type. |
| `--file <file>` | Yes | Path to the local file to upload. `-` for stdin isn't supported. |
| `--name <name>` | No | Name to store the file under. Defaults to the local file's name. |

```console
$ xbox --playfab-title-id ABC123 playfab file upload --entity-id TITLEPLAYER123 --entity-type title_player_account --file save.json
Uploading source file: D:\NodArena\save.json
Uploaded 'save.json' (2048 bytes).
```

### If an upload fails

While an upload is in progress, PlayFab blocks every other upload to the entity's files,
including the game's own, which fail with `EntityFileOperationPending`. So when an upload
fails partway, or you press Ctrl+C, the CLI cancels it and says on stderr whether that
worked. When the cancel works, the entity's files are unchanged.

When the CLI can't cancel the upload, or can't tell whether the upload or the cancel applied,
it says so. It prints the command that cancels the pending upload and, when the file might
have changed, the `file list` command to check. Run the cancel command only if later uploads
fail with `EntityFileOperationPending`, because it also cancels an upload of that file that
the game started.

## `playfab file delete`

| Argument / option | Required | Description |
| --- | --- | --- |
| `<names...>` | Yes | One or more file names to delete. |
| `--entity-id <entity-id>` | Yes | Entity ID. |
| `--entity-type <entity-type>` | Yes | Entity type. |
| `--confirm`, `--yes`, `-y` | Outside an interactive terminal | Confirm the deletion. |

```bash
xbox --playfab-title-id ABC123 playfab file delete data.json --entity-id TITLEPLAYER123 --entity-type title_player_account --confirm
```
