---
title: Games
description: List a seller's Partner Center games, look one up, create a game, and check on its creation job.
---

# Games

Partner Center game operations: list a seller's games, look one up by Store ID, XBOX
title ID, or name keyword, create a game, and inspect the resulting creation job.

Like [Seller](seller.md), these commands sit at the top
level rather than under `playfab`, because they don't call a PlayFab API.
They do not read `--playfab-title-id`.

## Commands

| Command | Description |
| --- | --- |
| `game list` | List every game for the seller, reading every page. |
| `game get` | Get games by Store ID, XBOX title ID, or case-insensitive name keyword. |
| `game create` | Create one game, then wait for its creation job. **Mutates production state.** |
| `game job get` | Read the current state of a game-creation job. |

`game list`, `game get`, and `game job get` are read-only. `game create` creates
production state that cannot be undone from the CLI — see
[Safety and destructive operations](../safety.md).

## Seller scope

Every game command runs against one Partner Center seller, resolved in this order:

1. `--seller-id <id>` on the command.
2. The `XBOX_SELLER_ID` environment variable.
3. The seller saved by [`xbox context set seller-id <id>`](context.md), in the selected
   profile.

With none of these, the command fails with
`No seller-id is set. Pass --seller-id, set XBOX_SELLER_ID, or run 'xbox context set seller-id <value>'.`
Signed out, it asks you to sign in instead.

## Arguments

| Argument | Command | Description |
| --- | --- | --- |
| `[title-id]` | `get` | Optional one-to-eight-digit hexadecimal XBOX title ID, with an optional `0x` prefix. It need not be zero-padded, so `42` means `0x00000042`. A non-hexadecimal value fails with `title-id must be one to eight hexadecimal digits, with an optional 0x prefix.` |
| `<job-id>` | `job get` | Required game-creation job ID, as returned by `game create`. This is not a game, Store, XBOX title, or PlayFab title ID. |

## Options

| Option | Commands | Description |
| --- | --- | --- |
| `--seller-id <id>` | all | Partner Center seller ID, overriding `XBOX_SELLER_ID` and the seller saved by `context set`. |
| `--base-url <url>` | all | Base URL for requests, overriding the `XboxPartner:BaseUrl` configuration key. |
| `--store-id <id>` | `get` | Microsoft Store ID of the game to retrieve. Usable alone, or with the positional title ID to disambiguate. |
| `--keyword <text>` | `get` | Case-insensitive substring search over game names. Cannot be combined with `title-id` or `--store-id`. |
| `-d`, `--details` | `list`, `get` | Show a multiline human-readable view. Cannot be combined with `--output`. |
| `--name <name>` | `create` | Required primary game name. |
| `--supported-device <device>` | `create` | Required supported device family: `xbox` or `desktop`. Repeat the option for both. |
| `--sandbox-id <id>` | `create` | Optional primary development sandbox ID. |
| `--confirm`, `--yes`, `-y` | `create` | Confirm production game creation without prompting. |
| `--output <format>` | all | `json` (default) or `json-compact` everywhere; `table` on `list` and `get` only. |

`--data-only` is accepted as a global option but does nothing here. The response
carries no PlayFab response envelope, so there is no wrapper to remove.

Setting `--base-url` to anything other than the shipped default writes a warning to stderr,
because every request carries a Microsoft Entra access token. For the same reason, the URL
must use HTTPS; plain HTTP is accepted only for a loopback address.

### Selector rules for `game get`

At least one of `title-id`, `--store-id`, or `--keyword` is required; omitting all three
fails with `At least one of title-id, --store-id, or --keyword is required.` `--keyword`
is exclusive: combining it with either of the others fails with `--keyword cannot be
combined with title-id or --store-id.` A single title ID can identify multiple games, so
`--store-id` can be supplied alongside it to narrow the result.

## Output

JSON is the default: one `games` collection, with each game exactly as returned,
including `productId`, `storeId`, `xboxTitleId`, `name`,
`SCID`, `primaryDevelopmentSandbox`, `deviceFamilies`, `packageIdentityName`, `packageIdentityPublisher`,
`packageFamilyName`, and `msaAppId`.

`xboxTitleId` is a string containing eight uppercase hexadecimal digits, zero-padded
without a `0x` prefix (for example, `"1234ABCD"`). `SCID` (exactly that casing) is
`00000000-0000-0000-0000-0000{titleIdHex}`, for example
`"00000000-0000-0000-0000-00001234ABCD"`. Both are null when the title ID is unavailable
or invalid. `primaryDevelopmentSandbox` is a nullable string projected from Partner
Center's `xboxLive.primaryDevelopmentSandbox`, and is null when absent.

`deviceFamilies` is a nullable string array projected from Partner Center's
`deviceFamilies.selections[].deviceFamily`, for example `["xbox", "desktop"]`.
Returned names and order are preserved without normalization or filtering. Absent
selections produce null; empty selections produce `[]`.

The same game summary fields appear in creation and job-polling responses. The job
envelope is unchanged, and `game` remains omitted until product detail is available.
JSON output preserves these null and omitted fields as returned by the service.

### Table output

`list` and `get` declare a table view with these columns. MSA stands for
Microsoft account.

| Column |
| --- |
| `TITLE ID` |
| `STORE IDS` |
| `PRODUCT NAME` |
| `MSA APP ID` |
| `PACKAGE FAMILY NAME` |

Rows are grouped by XBOX title ID, so every related Store ID, product name, package
family name, and MSA app ID appears on the same row. Long grouped values are truncated to
keep rows bounded.

`create` and `job get` do not declare a table view. Asking for one fails with `Output
format 'table' is not available for this command, because it does not declare a table
view.`, because the job object's shape is controlled by the backend.

Table output is a human convenience, not a stability contract: columns may change without
a breaking-change bump. Use `--output json-compact` for anything parsed by a script.

### Details view

`-d`/`--details` is a separate human-readable view that prints one indented block per
game instead of a row, including SCID and primary development sandbox. Missing or null
values display as `-`. It cannot be combined with `--output`; doing so fails with
`--details cannot be combined with --output.`

Each title ID is followed by the matching games' Store IDs, package identity
names and publishers, package family names, and MSA app IDs. Multiple games
under one title are numbered; a single game is not. Use JSON or this details
view when a table truncates grouped values or you need the full association
between a game and its identifiers.

For both human-readable views, the result count and query duration are written to stderr,
so stdout holds only the table or detail blocks. For example, stderr may report
`42 results found in 1.234s`.

## Listing and lookup

```bash
xbox game list --seller-id 1234567
xbox game list --seller-id 1234567 --output table
xbox game list --seller-id 1234567 --details

xbox game get --store-id 9N0000000000 --seller-id 1234567
xbox game get 1234ABCD --seller-id 1234567
xbox game get 42 --store-id 9N0000000000 --seller-id 1234567
xbox game get --keyword headless --seller-id 1234567
xbox game get 1234ABCD --details --seller-id 1234567
```

`game list` and `game get` read the catalog 100 games at a time, following every page, and
print one complete list. A large catalog can take a few minutes to read, so the CLI shows
its [progress](../commands.md#progress) on stderr while it reads. Partner Center can't search by
title ID, Store ID, or name, but the
service reads a game by its Store ID or title ID directly, so a lookup usually
takes one request. When it can't, and for `--keyword`, the CLI reads every page,
except that a Store ID lookup stops at its first match. These
commands deliberately have no `--limit`, `--all`, or `--continuation-token` options: the
table groups a title's games across the whole catalog, and you can page the output with
`less` or `more`. If a request fails, the CLI prints no partial results, doesn't retry, and
writes the `x-ms-correlation-id` shared by every page of the listing to stderr. Each
request times out after two minutes by default; change it with the
`XboxPartner:TimeoutSeconds` configuration value.

Authentication and other execution failures in `game list` and `game get` print an
error message to stderr without a stack trace and exit with code 1, leaving stdout empty.

## Creating a game

`game create` writes to production and cannot be undone from the CLI. It requires
`--confirm` (`--yes`, `-y`) or a confirmation prompt at a terminal, and fails closed
otherwise. Agents additionally need explicit user approval — see
[Safety and destructive operations](../safety.md).

```bash
xbox game create --name "Nod Arena" \
  --supported-device xbox --supported-device desktop \
  --sandbox-id TESTSB.42 --seller-id 1234567 --confirm
```

When the seller doesn't come from `--seller-id`, the command names it and where it came from
on stderr before it submits, even with `--confirm`:

```text
Using --seller-id 1234567 from saved context.
```

After submitting, the command waits up to 120 seconds for the creation job, polling every
5 seconds unless the service supplies a `Retry-After` header, and never more than once a
second. The job object is written to stdout as JSON; submission progress, the job
ID, the correlation ID, and recovery commands go to stderr. Recovery commands are quoted for
PowerShell on Windows and for a POSIX shell such as bash or zsh elsewhere, so they can be
pasted as they are.

Submission is never retried. A wait timeout does not cancel the job — check it with
`game job get` instead of submitting again. If no job ID is available, or the job ID the
service returned contains characters that can't be printed safely, inspect `game list`
before trying again. **Do not resubmit an uncertain creation.**

## Checking a creation job

```bash
xbox game job get JOB_ID --seller-id 1234567 --output json-compact
```

`job get` is read-only and never resubmits a creation, so it is safe to poll.

### Exit codes

| Exit code | Meaning |
| --- | --- |
| `0` | The job is running or succeeded. A `create` wait timeout also exits `0` with the accepted job. |
| `2` | The job reached a terminal failure. |
| `1` | The request, polling, or the command itself failed. |

A succeeded job can still report errors, so inspect the `errors` field on the returned
job object.

## End-to-end example

Save a seller once, create a game, then follow its job to completion:

```bash
xbox context set seller-id 1234567

xbox game create --name "Nod Arena" \
  --supported-device xbox --supported-device desktop \
  --sandbox-id TESTSB.42 --confirm

# If the wait timed out, poll the job ID printed to stderr instead of re-creating.
xbox game job get JOB_ID --output json-compact

# Confirm the game is in the catalog and note its XBOX title ID.
xbox game get --keyword "Nod Arena" --output table
```

See [Commands](../commands.md) for the global options that apply across
the CLI, [Seller](seller.md) for listing sellers, [Context](context.md) for saving the
default seller, and [Configuration](../configuration.md) for how to set
`XboxPartner:TimeoutSeconds`.
