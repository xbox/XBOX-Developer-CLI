---
title: Commands
description: The command reference for the CLI, with its global options, output formats, and a page for every command.
---

# Commands

This page is the command reference for the XBOX CLI. It covers `xbox` itself: the
global options, output formats, and exit codes every command shares. Each command below has its
own page, and each `playfab` service has a page under `commands/playfab/`. Run
`xbox <command> --help` to see the same reference in your terminal.

Help labels Experimental commands whose CLI contract might change or be removed.
See [Experimental commands](cli-command-lifecycle.md).

## Core commands

| Command | Description |
| --- | --- |
| [`auth`](commands/auth.md) | Authenticate with Microsoft Entra ID. |
| [`context`](commands/context.md) | Save default values, such as the seller and PlayFab title, for later commands. |
| [`game`](commands/game.md) | List, get, and create Partner Center games. |
| [`seller`](commands/seller.md) | List the Partner Center sellers available to the signed-in developer. |
| [`sandbox`](commands/sandbox.md) | List, get, and create XBOX sandboxes. |
| [`audience`](commands/audience.md) | List, get, create, and update a seller's audiences (Experimental). |
| [`package`](commands/package.md) | List and get game packages (Experimental). |
| [`playtest`](commands/playtest.md) | List, get, diff, create, update, publish, and delete the playtests of a game. |
| [`pc`](commands/pc.md) | Get or switch the XBOX sandbox this Windows PC uses (Experimental). |
| [`test-account`](commands/test-account.md) | Create, update, and delete XBOX test account batches, follow their creation jobs, read batches, and look up an account's batch and whether its access expired. |
| [`playfab`](commands/playfab.md) | Inspect and manage PlayFab services. |
| [`xbl`](commands/xbl.md) | Manage XBOX services configuration (Experimental). |

## Support commands

| Command | Description |
| --- | --- |
| [`info`](commands/info.md) | Show version and getting-started links. |
| [`upgrade`](commands/upgrade.md) | Check for a newer CLI release and show manual update instructions. |
| [`feedback`](commands/feedback.md) | Open a bug-report draft with CLI environment information. |
| [`help`](commands/help.md) | Show help for any command. |

## Global options

Every command accepts these options. Some only affect certain commands, as the table says;
elsewhere they're accepted and ignored.

| Option | Applies to | Description |
| --- | --- | --- |
| `--playfab-title-id <id>` | `playfab` commands | The PlayFab title to target. Wins over `XBOX_PLAYFAB_TITLE_ID` and the saved title, and changes neither. See [Context](commands/context.md). |
| `--profile <name>` | Commands that use saved values | Use the values saved in this profile. Defaults to `XBOX_PROFILE`, then `default`. See [Profiles](commands/context.md#profiles). |
| `--client-id <id>` | User sign-in | Override `AzureAd:ClientId`. Refused when signing in as a service principal. |
| `--tenant-id <id>` | User sign-in | Override `AzureAd:TenantId`. Refused when signing in as a service principal. |
| `--scopes <scopes>` | Sign-in | Override `AzureAd:Scopes`. |
| `--output <format>`, `-o` | Data commands | `json` (the default), `json-compact`, or `table` on commands that support it. See [Output formats](#output-formats). |
| `--data-only` | `playfab` commands | Write only the `data` field of the PlayFab response. See [Response envelope](commands/playfab.md#response-envelope). |
| `--confirm`, `--yes`, `-y` | `playfab` commands, and commands whose page lists it | Skip the confirmation prompt of a command that asks first. See [Safety](safety.md). |
| `--verbose` | All commands | Write diagnostic detail to stderr, including where each [saved value](commands/context.md#how-commands-use-saved-values) a command used came from. |
| `--disable-telemetry` | All commands | Turn off anonymous product-usage telemetry for this run. See [Telemetry](telemetry.md). |

Options override settings and environment variables; see [Configuration](configuration.md).

## Output formats

Data commands write their result to stdout and errors and diagnostics to stderr.

| Format | Output |
| --- | --- |
| `json` | Pretty-printed JSON. The default. |
| `json-compact` | Single-line JSON, for piping into `jq` or a script. |
| `table` | A plain text table, on commands whose page says they accept it. |

PlayFab commands wrap each result in a response envelope that `--data-only` removes; see
[Response envelope](commands/playfab.md#response-envelope). `--data-only` has no effect on
other commands or with `--output table`.

A few commands write text for people instead of JSON, and their pages say so. `auth status`
writes text unless you pass `--output json` or `--output json-compact`; see
[Auth](commands/auth.md#auth-status). File operations, such as `playfab file upload`, write
nothing to stdout and report what they did on stderr; see [Files](commands/playfab/file.md#output).

### Table output

Tables are for reading at a terminal. Their columns might change in any release, so scripts
and agents should use `--output json-compact`. A command without a table view refuses
`table`:

```console
$ xbox playfab api /Admin/GetTitleData --output table
Output format 'table' is not available for this command, because it does not declare a
table view. Omit --output to get this command's default output.
```

### Progress

Commands that read many pages, such as `game list`, or wait on a request, such as
`xbl config publish`, show their progress on one line of stderr:
`Reading games... 1646 found, 25 pages, 84s`. The line appears only when stderr is a terminal,
and it's erased before the result.

## Exit codes

These exit codes are shared by every command. Commands that change something can add their
own, such as `3` when a change's outcome is unknown, and their pages list them.

| Exit code | Meaning |
| --- | --- |
| `0` | The command succeeded. |
| `1` | The command failed. The error is on stderr. |
| `130` | You pressed Ctrl+C while the command was reading or waiting. |

A failed PlayFab call writes the error JSON to stderr in the `--output` format; see
[PlayFab error responses](playfab-api.md#error-responses). An unknown command writes an error
and usage to stderr; see [Help](commands/help.md).

If you press Ctrl+C after a command sent a change, it says on stderr whether the change might
have been made. It exits with the code its page documents, or `1` for a PlayFab command. Check
before you run it again; see
[An interrupted write reports that its outcome is unknown](safety.md#an-interrupted-write-reports-that-its-outcome-is-unknown).

## Safety

Many commands change production titles, sellers, and games, and only PlayFab operations whose
names start with `Get`, `List`, or `Search` are read-only. Read
[Safety and destructive operations](safety.md) before you run commands that change something.
