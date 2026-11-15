---
title: Games
description: List a seller's Partner Center games, look one up, create a game, and check on its creation job.
---

# Games

The `game` commands list a seller's Partner Center games, look one up by Store ID, XBOX title ID,
or name, create a game, and check on its creation job.

Each command acts on one seller, from `--seller-id` or your saved seller; see
[Context](context.md). If you belong to several sellers, see
[this known issue](../known-issues.md#xbox-game-commands-ignore---seller-id): these commands can
use another seller.

## Commands

| Command | Description |
| --- | --- |
| `game list` | List every game for the seller. |
| `game get` | Get games by Store ID, XBOX title ID, or name keyword. |
| `game create` | Create a game, then wait for its creation job. |
| `game job get` | Read the current state of a game-creation job. |

## Arguments

| Argument | Command | Description |
| --- | --- | --- |
| `[title-id]` | `get` | An XBOX title ID: one to eight hexadecimal digits, with an optional `0x` prefix, such as `1234ABCD`. `42` means `0x00000042`. |
| `<job-id>` | `job get` | The creation job ID that `game create` prints. |

## Options

| Option | Commands | Description |
| --- | --- | --- |
| `--seller-id <id>` | all | Partner Center seller ID. |
| `--base-url <url>` | all | Base URL for requests, overriding the `XboxPartner:BaseUrl` configuration key. |
| `--store-id <id>` | `get` | Microsoft Store ID of the game, such as `9N0000000000`. Can be combined with `title-id` to narrow the result. |
| `--keyword <text>` | `get` | Find games whose name contains this text, ignoring case. Can't be combined with `title-id` or `--store-id`. |
| `-d`, `--details` | `list`, `get` | Show one block of text per game. Can't be combined with `--output`. |
| `--name <name>` | `create` | Required. The game's name. |
| `--supported-device <device>` | `create` | Required. `xbox` or `desktop`; repeat the option for both. |
| `--sandbox-id <id>` | `create` | The game's development sandbox. It must be an existing Shared sandbox; [`sandbox get`](sandbox.md) shows a sandbox's type. |
| `--confirm`, `--yes`, `-y` | `create` | Create the game without the confirmation prompt. |
| `--output <format>` | all | `json` (default) or `json-compact`; `table` on `list` and `get` only. |

`game get` needs at least one of `title-id`, `--store-id`, or `--keyword`. The positional value is
always an XBOX title ID; when it looks like a Store ID, the error suggests the same command with
`--store-id`:

```text
title-id must be one to eight hexadecimal digits, with an optional 0x prefix. 9N0000000000 looks like a Store ID. Did you mean:
  xbox game get --store-id 9N0000000000 --seller-id 1234567
```

## Listing and lookup

```bash
xbox game list --output table
xbox game get --store-id 9N0000000000
xbox game get 1234ABCD --details
xbox game get --keyword "nod arena" --output table
```

`game list` returns the seller's whole catalog, and `game get` with `--keyword` searches all of
it. A large catalog can take a few minutes to read, and the command shows its
[progress](../commands.md#progress) on stderr. There's no paging option; page the output with
`less` or `more`. If a request fails, the command prints no partial results, and writes the
correlation ID to stderr. Each request times out after two minutes; to change it, set
`XboxPartner:TimeoutSeconds` (see [Configuration](../configuration.md)).

## Output

JSON output is one `games` array. Each game has these fields:

| Field | Description |
| --- | --- |
| `productId` | The Partner Center product ID. |
| `storeId` | The Microsoft Store ID. |
| `xboxTitleId` | The XBOX title ID: eight uppercase hexadecimal digits, without `0x`, such as `"1234ABCD"`. `null` when the game has no valid title ID. |
| `SCID` | The service configuration identifier (SCID), such as `"00000000-0000-0000-0000-00001234ABCD"`. The key is upper case. `null` when `xboxTitleId` is. |
| `name` | The game's name. |
| `primaryDevelopmentSandbox` | The game's development sandbox, or `null`. |
| `deviceFamilies` | The supported devices, such as `["xbox", "desktop"]`. `null` or `[]` when none are listed. |
| `packageIdentityName`, `packageIdentityPublisher`, `packageFamilyName` | The game's package identity. |
| `msaAppId` | The Microsoft account (MSA) app ID. |

`game create` and `game job get` write the creation job. Its `game` field, with the same fields,
appears once the game exists.

### Table output

`list` and `get` accept `--output table`, with one row per XBOX title ID and these columns:
`TITLE ID`, `STORE IDS`, `PRODUCT NAME`, `MSA APP ID`, and `PACKAGE FAMILY NAME`. A title's
Store IDs and other values share its row, and long values are truncated. `create` and `job get`
refuse `--output table`.

### Details view

`--details` prints one block per title ID, with every game under it and all of its identifiers,
including the SCID and development sandbox, without truncating. Missing values show as `-`. With
`--details` or `--output table`, the result count and duration go to stderr.

## Creating a game

```bash
xbox game create --name "Nod Arena" \
  --supported-device xbox --supported-device desktop \
  --sandbox-id TESTSB.42 --seller-id 1234567 --confirm
```

`game create` asks before it creates the game; without a terminal, it needs `--confirm`. The CLI
can't delete a game. If you belong to several sellers, check
[the known issue](../known-issues.md#xbox-game-commands-ignore---seller-id) first: the game can be
created in another seller.

The command sends the request once, waits up to two minutes for the creation job, and writes the
job to stdout. The job ID, correlation ID, and commands to check on the job go to stderr. If the
wait ends first, the job keeps running: check it with `game job get`, and don't create the game
again.

If the request fails, stderr says whether a game might have been created:

- **No game was created** when the request wasn't sent, or the service refused it, such as with a
  `400` for a sandbox the game can't use. Fix the problem and run the command again.
- **The outcome is unknown** after a server error, a timeout, or a lost connection. Check
  `game list` with the command printed on stderr before you try again.

## Checking a creation job

```bash
xbox game job get 11111111-2222-3333-4444-555555555555 --output json-compact
```

`job get` only reads, so you can run it as often as you like. A job that succeeded can still
report problems in its `errors` field.

## Exit codes

| Exit code | Meaning |
| --- | --- |
| `0` | The job succeeded or is still running. `create` also exits `0` when its wait ends, so `0` doesn't guarantee the game exists. |
| `1` | The command failed. For `create`, stderr says whether a game might have been created. |
| `2` | The job ended without creating the game. See its `errors` field. |
| `130` | You pressed Ctrl+C while the command was reading, or while `create` waited for an answer to its request. For `create`, stderr says the outcome is unknown. |

Don't decide whether to run `create` again from its exit code. If stderr says no game was
created, fix the error and run it again; otherwise check with the `game job get` or `game list`
command it printed.

## End-to-end example

Save a seller once, then create a game, which uses it:

```bash
xbox context set seller-id 1234567

xbox game create --name "Nod Arena" \
  --supported-device xbox --supported-device desktop \
  --sandbox-id TESTSB.42 --confirm
```

If the wait ends before the job does, check the job instead of creating the game again. Use the job
ID that `game create` printed on stderr:

```bash
xbox game job get 11111111-2222-3333-4444-555555555555 --output json-compact
```

Once the job succeeds, find the game:

```bash
xbox game get --keyword "Nod Arena" --output table
```
