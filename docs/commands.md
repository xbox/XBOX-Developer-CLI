---
title: Commands
description: The command reference for the CLI, with its global options, output formats, and a page for every command.
---

# Commands

This page is the command reference for the XBOX CLI. It follows the command tree:
this page covers `xbox` itself and the options every command accepts, each command
below has its own page, and each `playfab` service has a page under
`commands/playfab/`.

Run `xbox --help`, or `xbox <command> --help`, to see the same tree in your
terminal.

Help labels Experimental commands whose CLI contract may change or be removed.
See [Experimental commands](cli-command-lifecycle.md).

## Core commands

| Command | Description |
| --- | --- |
| [`auth`](commands/auth.md) | Authenticate with Microsoft Entra ID. |
| [`context`](commands/context.md) | Save default values, such as the seller and PlayFab title, for later commands. |
| [`game`](commands/game.md) | List, get, and create Partner Center games. |
| [`seller`](commands/seller.md) | List the Partner Center sellers available to the signed-in developer. |
| [`sandbox`](commands/sandbox.md) | List, get, and create XBOX sandboxes. |
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

These options are available to all commands.

| Option | Description |
| --- | --- |
| `--playfab-title-id` | The PlayFab title to target. It wins over `XBOX_PLAYFAB_TITLE_ID` and the title saved with [`context set`](commands/context.md), and changes neither. |
| `--profile` | Use the context values saved in this profile. Defaults to `XBOX_PROFILE`, then `default`. See [Profiles](commands/context.md#profiles). |
| `--client-id` | Override `AzureAd:ClientId` for user sign-in. Refused in service-principal modes by commands that sign in. |
| `--tenant-id` | Override `AzureAd:TenantId` for user sign-in. Refused in service-principal modes by commands that sign in. |
| `--scopes` | Override `AzureAd:Scopes`. |
| `--output`, `-o` | Output format: `json` (pretty, the default), `json-compact` for single-line JSON suitable for piping, or `table` on commands that declare a table view. See [Table output](#table-output). |
| `--disable-telemetry` | Disable anonymous product-usage telemetry for this invocation. |
| `--verbose` | Emit diagnostic detail to stderr without affecting machine-readable stdout, including where each [context value](commands/context.md#how-commands-use-saved-values) a command didn't get from its command line came from. |

`playfab` commands add `--data-only` and `--confirm`; see
[PlayFab options](commands/playfab.md#options).

See [Configuration](configuration.md) for how command-line options layer on top of
configuration files and environment variables, and [Context](commands/context.md) for how the
seller, PlayFab title, and XBOX sandbox resolve. See [Telemetry](telemetry.md) for
telemetry behavior and opt-out details.

## Output model

Data commands write JSON to stdout: pretty-printed by default, or single-line with
`--output json-compact` (`-o json-compact`) for piping. PlayFab commands wrap each
result in a response envelope that `--data-only` strips; see
[Response envelope](commands/playfab.md#response-envelope).

`auth status` is the main exception: it prints human-readable text by default,
and switches to JSON only when `--output json` or `--output json-compact` is
provided.

File operations, such as `playfab file upload`, `download`, and `delete`, aren't data
commands. They write nothing to stdout, report what they did on stderr, and signal success
through the exit code. See [Files](commands/playfab/file.md#output).

### Progress

List commands that read their results a page at a time, such as `game list`, show their
progress on one line of stderr while they read, and so does `game get`:
`Reading games... 1646 found, 25 pages, 84s`. `xbox xbl` commands that wait on one request,
such as `xbl configuration get` and `xbl publish start`, show what they're waiting for:
`Publishing from STAGE.0 to RETAIL... 12s`. `xbl achievement-image upload` shows its upload
the same way. The line appears only when stderr is a terminal, and it's erased before the
command writes its result or an error. When stderr is redirected to a file or pipe, nothing is
drawn.

### Exit codes and errors

Successful PlayFab API calls exit `0`. Failed calls exit `1` and write the full
error JSON to stderr, formatted per `--output`; with `--data-only`, stdout is
empty on error. Network and other non-API failures write human-readable
diagnostics to stderr. See [PlayFab error responses](playfab-api.md#error-responses).

Help writes human-readable text to stdout and exits `0`. An unknown command
writes an error and contextual usage to stderr and exits `1`, without writing
help to stdout. See [Help](commands/help.md).

With JSON output requested, `auth status` exits `0` even when nobody is signed
in, reporting `{"signedIn":false}`. Scripts can branch on that payload rather
than treating signed-out state as a process failure. `auth status` instead exits
`1` when the configuration or token cache can't be read, or, in a service-principal
mode, when it can't acquire a token or its settings are invalid. See
[Auth](commands/auth.md#auth-status).
Other command-specific exit codes are documented on each command's page.

### Table output

`-o, --output` accepts a third value, `table`, which renders a plain text table for
reading at a terminal:

| Format | Behavior |
| --- | --- |
| `json` | Pretty-printed JSON. The default for data commands. |
| `json-compact` | Single-line JSON, for piping into `jq` or similar. |
| `table` | Plain text table. Available only on commands that declare a table view. |

`table` is **opt-in per command rather than a global default**. Not every response can
become one: `group member list` nests members inside roles, `group membership opportunity
list` returns two independent arrays, and `playfab api` passes arbitrary JSON through. Which
columns are worth showing is an editorial choice too — inferring them generically means
dropping nested fields to invent columns, which is what makes `az ... --output table`
quietly lossy.

A default is only meaningful if every command can honor it, so rather than let the default
silently vary, a command with no declared view says so:

```console
$ xbox playfab api /Admin/GetTitleData --output table
Output format 'table' is not available for this command, because it does not declare a
table view. Omit --output to get this command's default output.
```

Column widths are counted in terminal cells rather than UTF-16 code units, so columns line
up whatever writing system the values use — a full-width CJK character takes two cells, a
combining mark takes none, and an emoji sequence takes two however many code points deep it
is. It is a close estimate rather than a guarantee: terminals disagree about emoji and about
East Asian Width's Ambiguous class.

`--data-only` has no effect with `--output table`, because a table has no response envelope
to strip.

> [!WARNING]
> Table output is a human convenience and **not a stability contract** — columns may be
> added, removed, or reordered without a breaking-change bump. Scripts and agents should use
> `--output json-compact`.

Each command page lists which of its commands accept `--output table`.

## Safety and destructive operations

The XBOX CLI is often used against production PlayFab titles. Only operations
whose PlayFab operation name starts with `Get`, `List`, or `Search` should be
treated as read-only by convention.

Catastrophic `Delete*` and `Shutdown*` PlayFab operations are hard-gated by the
CLI and fail closed unless confirmed with
`--confirm`, `--yes`, or `-y` or through the interactive prompt. Other mutating
verbs such as `Set*`, `Update*`, `Create*`, `Add*`, `Remove*`, `Publish*`,
`Request*`, `Enable*`, `Rollover*`, `Upload*`, and `Untag*` can still change
production state even when they are not part of the hard gate.

See [Safety and destructive operations](safety.md) before running mutating commands.
For `xbl achievement-image upload`, registration failures are not automatically
retried: an uncertain outcome needs read-only verification before another upload.
See [Achievement-image upload recovery](commands/xbl/achievement-image.md#production-safety-and-upload-recovery).
