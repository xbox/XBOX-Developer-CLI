---
title: Playtest
description: List, get, diff, create, update, publish, and delete the playtests of an XBOX game.
---

# Playtest

A playtest lets a chosen group of players try a game before release. The `playtest` commands read
a game's playtests in Partner Center, and create, update, publish, and delete them. `create`,
`update`, `publish`, and `delete` ask before they make a change.

`create`, `update`, `publish`, and `delete` are Experimental: their options and output might change
or be removed. See [Experimental commands](../cli-command-lifecycle.md).

## Commands

| Command | Description |
| --- | --- |
| `playtest list` | List the playtests of a game. |
| `playtest get <playtest-id>` | Get one playtest: its details, packages, images, audiences, and `etag`. |
| `playtest diff <playtest-id>` | Compare a playtest with its last published version, or with another playtest. |
| `playtest create` | Create a private draft playtest (Experimental). |
| `playtest update <playtest-id>` | Change a playtest. A published playtest returns to draft (Experimental). |
| `playtest publish <playtest-id>` | Publish a playtest, which offers it to its audiences from its start date, and wait until it's scheduled or live (Experimental). |
| `playtest delete <playtest-id>` | Delete a playtest, and wait until it's deleted (Experimental). |

## Arguments and options

| Argument or option | Applies to | Required | Description |
| --- | --- | --- | --- |
| `<playtest-id>` | `get`, `diff`, `update`, `publish`, `delete` | Yes | A `playtestId` from `list`, such as `11111111-2222-3333-4444-555555555555`. |
| `--store-id <id>` | all | One of the two | The game's Store ID, such as `9N0000000000`. |
| `--xbox-title-id <id>` | all | One of the two | The game's XBOX title ID, not its PlayFab title ID, such as `1234ABCD`. If it matches no game or several, the error lists the Store IDs to pass instead. |
| `--compare-to <playtest-id>` | `diff` | No | Another playtest to compare with, instead of the last published version. |
| `--name <name>` | `create`, `update` | `create` | The playtest's name, shown to players. Can't be blank. |
| `--description <text>` | `create`, `update` | No | Short description. On `update`, `""` clears it. |
| `--long-description <text>` | `create`, `update` | No | Long description. On `update`, `""` clears it. |
| `--start <date>` | `create`, `update` | `create` | When the playtest starts. See [Dates](#dates). |
| `--end <date>` | `create`, `update` | `create` | When the playtest ends. Must be after `--start`. |
| `--audience-id <id>` | `create`, `update` | No | An audience that can join, from [`audience list`](audience.md). Repeat for more. See [Choosing audiences](#choosing-audiences). |
| `--pc-package-id <id>` | `create`, `update` | No | The PC package players get. See [Packages](#packages). |
| `--console-package-id <id>` | `create`, `update` | No | The console package players get. See [Packages](#packages). |
| `--idempotency-key <key>` | `create` | No | 1 to 50 printable ASCII characters, instead of a new GUID. See [Creating a playtest](#creating-a-playtest). |
| `--if-match <etag>` | `update` | No | Update only if the playtest still has this `etag` from `playtest get`. See [Updating a playtest](#updating-a-playtest). |
| `--no-wait` | `publish`, `delete` | No | Return as soon as the publish or delete is queued. Can't be combined with `--timeout`. |
| `--timeout <duration>` | `publish`, `delete` | No | How long to wait, such as `90s`, `10m`, or `2h`. Defaults to `5m`; at most `7d`. |
| `--confirm` (`--yes`, `-y`) | `create`, `update`, `publish`, `delete` | No | Skip the confirmation prompt. Without it and without a terminal, the command refuses to make the change. |
| `--seller-id <id>` | all | If not saved | Partner Center seller ID. See [Context](context.md). |
| `--base-url <url>` | all | No | Base URL for requests, overriding the `XboxPartner:BaseUrl` configuration key. |
| `--output <format>` | all | No | `json` (default), `json-compact`, or `table`. `create`, `update`, `publish`, and `delete` refuse `table`. |

## Examples

List playtests, get one, and compare two:

```bash
xbox playtest list --store-id 9N0000000000
xbox playtest list --xbox-title-id 1234ABCD --output table
xbox playtest get 11111111-2222-3333-4444-555555555555 --store-id 9N0000000000
xbox playtest diff 11111111-2222-3333-4444-555555555555 --store-id 9N0000000000 --compare-to 22222222-3333-4444-5555-666666666666 --output table
```

Create a playtest for an audience:

```bash
xbox playtest create --store-id 9N0000000000 --name "Nod Arena alpha" --start 2026-11-01 --end 2026-11-15 --audience-id 1152921504606846976
```

Change a playtest's end date:

```bash
xbox playtest update 11111111-2222-3333-4444-555555555555 --store-id 9N0000000000 --end 2026-11-30
```

Publish a playtest:

```bash
xbox playtest publish 11111111-2222-3333-4444-555555555555 --store-id 9N0000000000
```

Delete a playtest:

```bash
xbox playtest delete 11111111-2222-3333-4444-555555555555 --store-id 9N0000000000
```

## Reading playtests

`playtest list` writes a `playtests` array. Its table has `PLAYTEST ID`, `NAME`, `STATUS`, `TYPE`,
`START`, and `END` columns. An excerpt:

```json
{ "playtests": [ { "playtestId": "11111111-2222-3333-4444-555555555555", "name": "Alpha", "status": "Live", "type": "Private" } ] }
```

`playtest get` writes the playtest as the service returns it. `etag` is the version to pass to
`update --if-match`, and `audiences.audienceIds` lists the audiences that can join. Its table has
`FIELD` and `VALUE` rows.

`playtest diff` writes `hasDiffData`, `current`, and `compared`. When `hasDiffData` is `false`,
there's nothing to compare, and `compared` is left out. Its table shows only the fields that
differ, in `FIELD`, `CURRENT`, and `COMPARED` columns. Images aren't compared.

## Creating a playtest

```bash
xbox playtest create --store-id 9N0000000000 --name "Nod Arena alpha" --start 2026-11-01 --end 2026-11-15
```

`playtest create` makes a private draft playtest and writes it as `playtest get` does. Running it
again creates another playtest; remove one you don't want with
[`playtest delete`](#deleting-a-playtest).

Each create sends an idempotency key, a new GUID unless you pass `--idempotency-key`. A create that
repeats a key returns the playtest that key created, and ignores any options that differ.

When the outcome is unknown, such as after a timeout, the command exits `3` and prints a command
that retries with the same key:

```bash
xbox playtest create --store-id 9N0000000000 --name "Nod Arena alpha" --start 2026-11-01 --end 2026-11-15 --idempotency-key 11111111-2222-3333-4444-555555555555
```

The retry returns the playtest if the first request created it, and creates it otherwise. When an
option holds a line break, only the key is printed; run the same command with
`--idempotency-key` and that key. If the retry is refused, such as because the end date has passed,
check `playtest list` before you create the playtest with a new key.

## Packages

A playtest can have one PC package and one console package. Choose a package that's ready for a
playtest from [`xbox package list`](package.md#listing-packages), and pass its `id` to the option
for its platform:

```bash
xbox playtest create --store-id 9N0000000000 --name "Nod Arena alpha" --start 2026-11-01 --end 2026-11-15 --pc-package-id 11111111-2222-3333-4444-555555555555
```

On `update`, a package option you leave out keeps its package, a new ID replaces it, and `""`
removes it:

```bash
xbox playtest update 11111111-2222-3333-4444-555555555555 --store-id 9N0000000000 --pc-package-id 22222222-3333-4444-5555-666666666666 --console-package-id ""
```

## Updating a playtest

> [!WARNING]
> Any update returns a published playtest to draft, as editing it in Partner Center does. Publish
> it again with `playtest publish` to make it available to players.

```bash
xbox playtest update 11111111-2222-3333-4444-555555555555 --store-id 9N0000000000 --end 2026-11-30
```

`playtest update` changes only the options you pass, and needs at least one. It writes the updated
playtest as `playtest get` does. Game art configured in Partner Center is kept; other art linked to
the playtest is replaced with a placeholder.

To avoid overwriting someone else's change, pass the `etag` from `playtest get` in `--if-match`.
The double quotes around it are optional, and `*`, weak (`W/`) etags, and lists of etags are
refused:

```bash
xbox playtest update 11111111-2222-3333-4444-555555555555 --store-id 9N0000000000 --end 2026-11-30 --if-match 0x8DD1234ABCD5678
```

When the outcome is unknown, the command exits `3`. Check the playtest with `playtest get` before
you retry.

## Choosing audiences

An audience is a group of players, such as testers, that the seller manages in Partner Center.
Pass an `id` from [`audience list`](audience.md) to `--audience-id`, and repeat it for more:

```bash
xbox playtest create --store-id 9N0000000000 --name "Nod Arena alpha" --start 2026-11-01 --end 2026-11-15 --audience-id 1152921504606846976 --audience-id 1152921504606846977
```

Without `--audience-id`, `create` makes a playtest with no audiences, and `update` keeps them. On
`update`, the IDs you pass replace all of the playtest's audiences. To remove every audience, pass
`""` alone:

```bash
xbox playtest update 11111111-2222-3333-4444-555555555555 --store-id 9N0000000000 --audience-id ""
```

`""` can't be combined with audience IDs, and `create` refuses it. A playtest needs at least one
audience to be published.

## Publishing a playtest

If the game uses XBOX services, set up their configuration and embargo before you publish. See
[Use XBOX services in a playtest](../xbox-services-in-a-playtest.md).

```bash
xbox playtest publish 11111111-2222-3333-4444-555555555555 --store-id 9N0000000000
```

Publishing offers the playtest to its audiences from its start date. The command publishes the
version it reads first; if the playtest changes in between, the publish fails with `412` and
changes nothing.

Before it asks you to confirm, the command checks the game's XBOX services setup, such as whether
its embargo date is after the playtest's end date, and lists anything to review on stderr. These
checks only advise; `--confirm` publishes without asking.

The service then runs its own checks. When one blocks the publish, nothing is queued, and the
command lists each check on stderr and exits `1`. Fix the errors and publish again. A playtest
blocked for missing age ratings needs the age-rating questionnaire completed in Partner Center.

Once the publish is queued, the command waits until the playtest is `Scheduled`, `Live`, or
`PublishFailed`, and writes it as `playtest get` does. Ctrl+C stops waiting, but the publish
continues. With `--no-wait`, the command writes `publishJobId` and any `warnings` as soon as the
publish is queued; follow it with `playtest get`.

| Exit code | Meaning |
| --- | --- |
| `0` | The playtest is `Scheduled` or `Live`. With `--no-wait`, the publish was queued. |
| `1` | Not published, such as because a check blocked it or the playtest changed. |
| `2` | The publish failed: the playtest is `PublishFailed`. |
| `3` | The request was sent, but its outcome is unknown. Check the playtest with `playtest get` before you publish again. |
| `4` | The publish was queued, but the wait ended before it finished. Check it with `playtest get`. |

## Deleting a playtest

```bash
xbox playtest delete 11111111-2222-3333-4444-555555555555 --store-id 9N0000000000
```

`playtest delete` deletes the version of the playtest it reads first. It waits until the playtest
is `Deleted` or gone, then writes the service's response, with `status`. Ctrl+C stops waiting, but
the delete continues. With `--no-wait`, it writes the response as soon as the delete is queued.
After a `412`, the delete might already be queued, so wait a minute and check with `playtest get`
before you delete again.

| Exit code | Meaning |
| --- | --- |
| `0` | Deleted. With `--no-wait`, the delete was queued. |
| `1` | Not deleted, such as because the playtest changed or its status doesn't allow it. |
| `3` | The request was sent, but its outcome is unknown. Check the playtest with `playtest get` before you delete again. |
| `4` | The delete was queued, but the playtest wasn't `Deleted` before the wait ended. Check it with `playtest get`. |

## Dates

`--start` and `--end` accept ISO 8601 dates and times, such as `2026-11-01T09:00:00-07:00` or
`2026-11-01T16:00:00Z`. A date without a time means midnight UTC, and a time without an offset is
UTC. When an update passes only one date, the service checks it against the playtest's other date.

## Errors

Failures go to stderr and exit `1`, unless the exit-code tables above say otherwise. `create` and
`update` exit `3` when their outcome is unknown. A failure the service returns includes its HTTP
status, and `--verbose` adds the service's response, whose `code` field is shown here:

| Status | `code` | Meaning |
| --- | --- | --- |
| 400 | `invalid_playtest_request` | `create` or `update` sent an invalid request, such as an end that isn't after the start. |
| 400 | `playtest_audience_not_found` | An `--audience-id` isn't one of the seller's audiences. The message shows the `audience list` command to check. |
| 400 | `playtest_package_not_found` | The game has no package with that ID. |
| 400 | `playtest_package_ambiguous` | The package ID matches more than one package set and none of them is on the game's Main branch, or more than one market group and none of them is `default`. |
| 400 | `playtest_package_platform_mismatch` | A package doesn't match its option's platform, such as a console package in `--pc-package-id`. Check its platform with `xbox package list`. |
| 400 | `playtest_request_rejected` | Partner Center rejected the request, such as a `create` or `update` with an end date in the past or a package that isn't available for playtests yet, or a `publish` of a deleted playtest. |
| 403 | `audience_permission_denied` | You passed `--audience-id` without Partner Center's customer groups Read permission for the seller. Nothing changed. Ask a manager of the Partner Center account for access. |
| 403 | `package_permission_denied` | On `list`, `get`, and `diff`, you don't have Packages Read permission on the game, which the Developer role includes. On the other commands, you don't have permission to change the game's playtests. Ask a manager of the Partner Center account for access. |
| 403 | `playtest_not_enabled` | Playtests aren't enabled for the game and seller, or the service couldn't check right now. If they should be enabled, try again later. |
| 404 | `playtest_not_found` | The game has no playtest with that ID or `--compare-to` ID. The message shows the `playtest list` command to check. |
| 404 | `game_not_found` | The seller has no game with that Store ID. Check `--store-id` and `--seller-id`. |
| 409 | `playtest_not_editable` | The playtest's status doesn't allow `update` right now, such as while it's being published. Check it with `playtest get`. |
| 409 | `playtest_ended` | The playtest has ended, and `update` didn't pass `--end`. Pass a future `--end` with the change. |
| 409 | `playtest_not_publishable` | The playtest's status doesn't allow `publish`, or it changed since it was read. Check it with `playtest get`. |
| 409 | `playtest_not_deletable` | The playtest's status doesn't allow `delete`. Check it with `playtest get`. |
| 412 | `playtest_precondition_failed` | The playtest changed since it was read. For `update`, run `playtest get`, check that your change still applies, and retry with the new `etag`. For `publish` and `delete`, check the playtest and retry. |
| 422 | `playtest_publish_blocked` | The service's publish checks failed, so nothing was queued. The command lists each check. |

For other failures, the command shows the service's message. A 429 means you sent too many
requests; wait as long as the message says before you try again.
