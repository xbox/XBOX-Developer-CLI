---
title: Safety and destructive operations
description: Which XBOX CLI commands only read, which ask before they change something, which do something their names don't tell you, and what to do when a change's outcome is unknown.
---

# Safety and destructive operations

The XBOX CLI acts with your permissions, often on production titles and sellers. This page covers
which commands only read, which ask before they change something, the few that do something
their names don't tell you, and what to do when a change's outcome is unknown. For what an AI
agent must ask before a change, see [Using the XBOX CLI from agents](cli-for-agents.md).

A command takes its title, seller, or sandbox from a flag, an `XBOX_` environment variable, or a
value saved with `xbox context set`. A command that changes something names on stderr each
value you didn't pass on its command line, so check it before a production change. See
[Context](commands/context.md).

## What's read-only

A PlayFab operation is read-only only if its name starts with `Get`, `List`, or `Search`, such
as `/Admin/GetTitleData` or `/MultiplayerServer/ListBuildSummariesV2`. Every other operation
changes something. This matters most for `xbox playfab api <api-path>`, where you type the
operation's path, not a command name.

A command's Help has a `SAFETY` section that says whether it reads or changes something.

## What asks first

Most commands that change something run without asking. These ask first in a terminal, and
without one, they refuse unless you pass `--confirm` (`--yes`, `-y`):

| Command | When it needs confirmation |
| --- | --- |
| Any PlayFab `Delete*` or `Shutdown*` operation, through any command, including `playfab api` | Always |
| `playfab economy inventory operation execute` | When the batch contains a `Delete` operation. It refuses without `--confirm`, even in a terminal. |
| `playfab api-policy set --overwrite` | Always. It refuses without `--confirm`, even in a terminal. |
| [`audience create`, `audience update`](commands/audience.md) | Always |
| [`game create`](commands/game.md) | Always |
| [`playtest create`, `update`, `publish`, `delete`](commands/playtest.md) | Always |
| [`sandbox create`](commands/sandbox.md) | Always |
| [`test-account batch create`, `update`, `delete`](commands/test-account.md) | Always |
| [`xbl config publish`](commands/xbl.md#config-publish) | To RETAIL, CERT, CERT.DEBUG, or HIDDEN, or from another sandbox. Never with `--validate-only`. |
| [`xbl config save`, `xbl config publish`](commands/xbl.md#title-targeting) by `--title-id` | When other titles share the configuration. `--allow-shared-configuration` skips the question; `--confirm` doesn't. |
| [`pc sandbox set`](commands/pc.md) | Always |

## What a command's name doesn't tell you

- If [`playfab service-principal set`](commands/playfab/service-principal.md) fails, the service
  principal might have no access in the studio until a `set` succeeds, which can stop a
  pipeline that signs in as it. Pass `--expected-version` so `set` fails instead of overwriting
  someone else's change.
- [`playfab privacy export`](commands/playfab/privacy.md) emails the player's data, from every
  title of the publisher they played, to the title's notification email address. The export
  and its email can't be recalled.
- [`xbl config publish`](commands/xbl.md#cross-sandbox-publishing-replaces-global-title-storage-files)
  from another sandbox replaces the destination's global title storage files with the
  source's, and deletes files only the destination has. The deleted files can't be recovered.
- `xbl config publish --sandbox-id HIDDEN` publishes the title-wide configuration, which every
  sandbox uses, RETAIL included.

## An interrupted write reports that its outcome is unknown

If a change is interrupted after the CLI sends it, such as by Ctrl+C, a timeout, or a lost
connection, or the service answers with an HTTP 5xx error, the change might have been applied.
The command says its outcome is unknown, as in this PlayFab error:

```text
Request failed: /Admin/SetTitleData may have reached PlayFab, but the request was canceled, so its outcome is unknown. Check whether it was applied before retrying.
```

| Commands | Exit code when the outcome is unknown |
| --- | --- |
| `audience create` and `update`, `playtest create`, `update`, `publish`, and `delete`, `sandbox create`, `test-account batch create`, `update`, and `delete`, and `xbl config save` and `publish` | `3` |
| Other commands, including every `playfab` command | `1`, except `game create`, which exits `130` when you press Ctrl+C after it sends the request. When PlayFab answered with error JSON, the JSON on stderr also has an `outcomeUnknown` field. |

Read the affected state before running the change again. Commands outside `playfab` print a
read-only command to check with. The CLI never retries a change on its own.

Some commands that wait for a job exit with `4` when the request was accepted but the wait
ended. Don't run them again; see the command's page for how to keep waiting.
