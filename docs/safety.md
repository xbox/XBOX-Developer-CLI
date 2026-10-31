---
title: Safety and destructive operations
description: Which operations the CLI treats as read-only or mutating, and how the confirmation gate protects production titles from destructive calls.
---

# Safety and destructive operations

The XBOX Developer CLI is often used against production PlayFab titles. The safety model is
therefore intentionally conservative: read-only operations are the default, and
catastrophic irreversible operations have a fail-closed confirmation gate.

This page describes how the CLI classifies PlayFab API paths, what the hard gate
actually enforces, and what still depends on caller discipline. See
[Commands](commands.md), [Multiplayer Servers](commands/playfab/mps.md), and
[Using the XBOX CLI from agents](cli-for-agents.md) for related workflows.

## Read-only by default

PlayFab API paths end with an operation name, for example
`/Admin/GetTitleData` or `/MultiplayerServer/ListBuildSummariesV2`. The CLI
classifies the operation by that **last path segment**.

Only operations whose name starts with one of these prefixes are treated as
read-only by convention:

| Prefix | Example |
| --- | --- |
| `Get*` | `/Admin/GetTitleData` |
| `List*` | `/MultiplayerServer/ListBuildSummariesV2` |
| `Search*` | Search operations in the PlayFab metadata |

Everything else should be treated as mutating production data or infrastructure,
including:

`Set*`, `Update*`, `Create*`, `Delete*`, `Add*`, `Remove*`, `Publish*`,
`Cancel*`, `Shutdown*`, `Request*`, `Enable*`, `Disable*`, `Start*`, `Stop*`,
`Reset*`, `Revoke*`, `Grant*`, `Increment*`, `Decrement*`, `Move*`, `Link*`,
`Unlink*`, `Import*`, `Export*`, and `Generate*`.

> [!WARNING]
> If the operation name does not start with `Get`, `List`, or `Search`, assume it
> can change production state.

## The catastrophic hard gate

A smaller catastrophic subset is enforced by the CLI itself:

| Prefix | Why it is gated |
| --- | --- |
| `Delete*` | Permanently removes persisted PlayFab data, configuration, assets, builds, or other resources. |
| `Shutdown*` | Force-terminates a running multiplayer server session. |

These operations require explicit confirmation with the global `--confirm`
option. The same option is also available as `--yes` or `-y`.

```bash
# Refused unless confirmed interactively or with --confirm
xbox --playfab-title-id ABC123 playfab mps build delete --body '{ "BuildId": "11111111-2222-3333-4444-555555555555" }'

# Non-interactive confirmation
xbox --playfab-title-id ABC123 playfab mps build delete --body '{ "BuildId": "11111111-2222-3333-4444-555555555555" }' --confirm
```

The same gate protects direct API calls:

```bash
xbox --playfab-title-id ABC123 playfab api /MultiplayerServer/ShutdownMultiplayerServer --body '{ "SessionId": "11111111-2222-3333-4444-555555555555" }' --confirm
```

When `--body-file -` reads the request body from stdin, `Delete*` and `Shutdown*`
operations require `--confirm` even in a terminal. The CLI refuses before
reading input if confirmation is missing; request-body content never answers
a confirmation prompt.

### The gate reads the API operation name, not the CLI verb

This is the most important subtlety on this page. Classification happens against
the **PlayFab operation the command calls**, not the word you typed. A CLI
subcommand named `delete` is therefore *not* automatically gated.

Two commands in this CLI are affected today:

| Command | PlayFab API it calls | Classified as | Hard-gated? |
| --- | --- | --- | --- |
| `playfab title-data delete` | `/Admin/SetTitleData` (or `/Admin/SetTitleInternalData`) | `Set*` | **No** |
| `playfab player-data delete` | `/Admin/UpdateUserData` (and the other `UpdateUser*Data` variants) | `Update*` | **No** |

Both remove data through a `Set`/`Update` operation rather than a `Delete`, but by
different means: `title-data delete` writes a null value for each key, while
`player-data delete` names the keys in the update endpoint's `KeysToRemove` field.
Either way they destroy data, they run without prompting, and **there is no undo**.

Treat the hard gate as covering `Delete*`/`Shutdown*` *API operations* only.
Never rely on a CLI subcommand being named `delete` to mean it will stop and ask
you first.

#### Some commands are classified explicitly, and Help is authoritative

The naming rule is a default, not the last word. It is a heuristic over operation
names, so it is wrong in both directions for a handful of commands — and where it
would be wrong, the command declares its classification instead of deriving one.
**`<command> --help` always reports the classification the CLI actually uses.**

| Command | Rule alone would say | Actually | Why |
| --- | --- | --- | --- |
| `playfab group member is-member` | Mutates production state | **Read-only** | `/Group/IsMember` is a membership check. It just isn't named `Get*`. |
| `playfab mps asset upload` | Read-only | **Mutating** | It calls `GetAssetUploadUrl`, a read, then writes the asset to the URL it gets back. |
| `playfab api` | Depends on the path | **Depends on the path** | The path is an argument, so nothing can be derived until you supply one. |

So an operation name outside `Get*`/`List*`/`Search*` is a reason to *check*, not a
verdict on its own. Read the command's `SAFETY` line rather than pattern-matching
the path. The same classification decides which commands name the context values they
didn't get from their command line; see
[Which title or seller a command acts on](#which-title-or-seller-a-command-acts-on).

One thing an explicit classification **cannot** do is make a command look safer
than its path: a `Delete*` or `Shutdown*` operation renders the destructive
warning whatever else is declared, because that is the part tied to the runtime
`--confirm` gate.

#### A command may re-apply the gate itself

The same blind spot works in reverse: a non-`Delete*` operation can still carry a
destructive payload. `playfab economy inventory operation execute` calls
`/Inventory/ExecuteInventoryOperations`, which the operation-name gate does not
match, but a batch it sends may contain a `Delete` operation that permanently
removes item stacks.

Rather than leave that unguarded, the command inspects the parsed batch and
refuses when it contains a delete unless `--confirm` is supplied:

```console
$ xbox ... playfab economy inventory operation execute --operations-file ops.json
This operations batch contains a Delete operation, which permanently removes item stacks. Re-run with --confirm to proceed.
```

This is a command-level check layered on top of the global gate, not a
replacement for it. If you add a command whose payload can destroy data while
its API operation name does not begin with `Delete`/`Shutdown`, apply the same
pattern.

#### Exporting a player's data emails it to the title's notification address

`playfab privacy export` calls `/Admin/ExportMasterPlayerData`, which the
operation-name rule classifies as mutating, and runs without `--confirm`. It
deletes nothing, and it doesn't return the player's data. When the export is
ready, PlayFab emails a download link to the notification email address
configured for the title. The export holds the player's data from every title of
the publisher they played. Once queued, the export and its email can't be
recalled. The command's `SAFETY` line says so; see
[Privacy](commands/playfab/privacy.md#what-the-export-sends-and-to-whom) for
PlayFab's documentation of what the export holds and how it's delivered.

#### Creating XBOX test accounts asks first and is never retried

`test-account batch create` is not a PlayFab operation, so the operation-name gate does not
apply. The accounts it creates count against the seller's test account limit until
their batch is deleted, so the command applies its own check: it describes
what it will create and asks in a terminal, and without a terminal it refuses
unless `--confirm` (`--yes`, `-y`) is supplied. It asks for the password and
security answer only after that check passes. As everywhere else, `--confirm` is
not agent approval.

The create is not idempotent, so the CLI sends it exactly once and never retries
it, even after a timeout. When it cannot tell whether a batch was created, it exits
with code 3 and prints how to check before creating again; see
[Test account](commands/test-account.md#when-the-outcome-is-unknown). When the batch
was created but the wait ended before its job stopped, it exits with code 4 and prints
the command to resume waiting; do not create again.

#### Updating XBOX test account batches asks first and is never retried

`test-account batch update` is not a PlayFab operation either, so the operation-name gate
does not apply. It can
give every account in the batch access to sandboxes, take that access away, move when
the access expires, and add to or replace the batch's tags. In a terminal the command reads the
batch, shows each change next to the current value, and asks; without a terminal it
refuses unless `--confirm` (`--yes`, `-y`) is supplied. The service refuses a batch
another developer created unless `--allow-not-mine` is passed. As everywhere else,
`--confirm` is not agent approval.

The CLI sends the update exactly once and never retries it. When it cannot tell
whether the update started, it exits with code 3 and prints the `batch get` command
to check before updating again; see
[Test account](commands/test-account.md#updating-a-batch).

#### Deleting XBOX test account batches asks first and is never retried

`test-account batch delete` is not a PlayFab operation either, so the operation-name gate
does not apply. The
delete removes every account in the batch from its sandboxes and deletes their test
account records, and it cannot be undone. In a terminal the command reads the batch,
shows what it will delete, and asks; without a terminal it refuses unless `--confirm`
(`--yes`, `-y`) is supplied. The service refuses a batch another developer created
unless `--allow-not-mine` is passed, and it may still refuse, depending on the
caller's permissions. As everywhere else, `--confirm` is not agent
approval.

The CLI sends the delete exactly once and never retries it. When it cannot tell
whether the delete started, it exits with code 3 and prints the `batch get` command
to check before deleting again; see
[Test account](commands/test-account.md#deleting-a-batch). There is no bulk delete.

#### Publishing XBOX Live configuration to certification, RETAIL, or every sandbox asks first

`xbl publish start` is not a PlayFab operation, so the operation-name gate does not
apply. A publish to RETAIL reaches players, a
publish to CERT or CERT.DEBUG reaches XBOX certification, and a publish to HIDDEN changes
the title-wide configuration every sandbox uses. A publish from another sandbox first
replaces the destination's same-named working documents and its global title storage files.
For these, the command describes the publish, the storage warning included, and asks in a
terminal; without a terminal it refuses unless `--confirm` (`--yes`, `-y`) is supplied.
`--validate-only` never asks. As everywhere else, `--confirm` is not agent approval.

A publish of unchanged configuration starts nothing, and XBOX keeps RETAIL locked for a
product after a migration. `--republish` publishes in full even when nothing changed, and
forces the selected configuration, by default RETAIL's latest working one, onto a locked
RETAIL. A RETAIL publish from another sandbox also clears the lock, but it replaces RETAIL's
working documents with that sandbox's. See [XBOX Live](commands/xbl.md#republishing).

#### Commands that change this PC

`pc sandbox set` calls no remote API, but it changes a machine-wide setting:
it switches the XBOX sandbox for every user and game on this PC and restarts
XBOX services, including Gaming Services. Its `SAFETY` line says so, and it asks
for confirmation before it runs. Outside an interactive terminal it refuses unless
`--confirm` (`--yes`, `-y`) is supplied. Windows also shows a UAC prompt, which the
CLI cannot answer for you. Like `XblPCSandbox.exe`, it switches even when the PC
already uses the requested sandbox, because running it again is the documented fix
for a switch that did not finish.

`pc sandbox launch-apps` closes and reopens the Microsoft Store and XBOX app. It
needs no confirmation or elevation, but its `SAFETY` line says what it interrupts.

## Fail-closed enforcement

The gate is not only a command-line parsing check. It is enforced in the shared
code that sends every PlayFab request.

Before acquiring a token or sending a request, the CLI checks whether the
operation is destructive. If it is and the operation wasn't confirmed, the CLI
refuses to send it.

That matters because every PlayFab command goes through this check, including
`playfab api` and the API-backed `playfab mps` commands. So `Delete*` and
`Shutdown*` calls are refused by default, whichever command sends them.

### An interrupted write reports that its outcome is unknown

The same shared code guards against blind retries. The CLI classifies every
failure once, by how far the call got, and every command reports it from that
classification:

| How far the call got | Example | Reported as |
| --- | --- | --- |
| Never sent | Signing in failed, Ctrl+C before sending, DNS or TLS failure | The error, with no warning |
| Sent, no response | Ctrl+C, timeout, or lost connection after sending | The error, with a warning for writes |
| Answered with HTTP 5xx | PlayFab's 500, a gateway's 502 or 504 | PlayFab's error, with a warning for writes |
| Answered with HTTP 4xx | Bad request, throttling 429 | PlayFab's error, with no warning |
| Answered with HTTP 2xx | Success, even if the response is unusable | Success, or the response error with no warning |

A write is any call other than `Get*`, `List*`, or `Search*`. When a write gets no
response, the command exits with code 1 and says the call may have been applied:

```text
Request failed: /Admin/SetTitleData may have reached PlayFab, but the request was canceled, so its outcome is unknown. Check whether it was applied before retrying.
```

On a 5xx, PlayFab's error JSON is still written to stderr, with an added
`outcomeUnknown` field carrying the same warning. If the body isn't a JSON object, the
warning follows it as text. If an error's body is over `PlayFab:MaxResponseBytes`, or
fails to arrive, the error still names the HTTP status and why the body is missing,
and the status alone decides whether it warns.

Read the affected state before running the command again. Reads never warn, because
repeating them is safe. A dropped connection warns even if it may have failed before
sending, because the HTTP handler can resend a request on a new connection.
## Path normalization defenses

The destructive-operation classifier canonicalizes the API path before checking
the operation name. It trims the path, converts backslashes to `/`, resolves the
path against a placeholder base URI, decodes percent-encoding, and ignores query
or fragment text before inspecting the final segment.

This closes bypasses where a destructive operation is disguised with path tricks,
such as backslashes or percent-encoded characters. The fuzz suite treats this gate
as its highest-priority target. See [Fuzz testing](contributing/fuzzing.md).

The check that decides whether a command is read-only, and so whether it has to name a title
or seller it didn't get from its command line, reads the operation the same way. A query or
fragment can't make a write look like a read: `/Admin/SetTitleData?next=/GetTitleData` is
`SetTitleData`.

Every PlayFab request carries your Entra ID token, so the client sends it only to the
configured PlayFab address. Before signing in, it refuses a path that contains a backslash,
and a path that would resolve to another scheme, host, or port, such as one that names a
host after leading slashes or whitespace. Separate path segments with `/`.

## Which title or seller a command acts on

A command takes its PlayFab title, seller, or XBOX Live sandbox from its flag, then its
environment variable (such as `XBOX_PLAYFAB_TITLE_ID` or `XBOX_SELLER_ID`), then the value saved
with [`xbox context set`](commands/context.md) in the selected profile. Run `xbox context get`
to see what's saved. Saved context is shared by every session of your OS user, and environment
variables never appear on the command line, so:

- Every command that changes something writes each of these values it didn't get from its
  own command line to stderr, with its source, even with `--confirm`. Read-only commands
  do so only with `--verbose`.

  ```text
  Using --playfab-title-id ABC123 from saved context.
  ```

- `/Admin/DeleteTitle` deletes the title itself, so it refuses a title that isn't given with
  `--playfab-title-id` on the same command, even with `--confirm`, and prints the command to
  run instead. A title from `XBOX_PLAYFAB_TITLE_ID` or a profile doesn't count:

  ```text
  $ xbox playfab api /Admin/DeleteTitle --confirm
  Using --playfab-title-id ABC123 from saved context.
  '/Admin/DeleteTitle' acts on playfab-title-id ABC123, which comes from saved context.
  The target of a destructive operation must be named on the command line. To proceed, run:
    xbox --playfab-title-id ABC123 playfab api /Admin/DeleteTitle --confirm
  ```

- [`playfab api-policy set --overwrite`](commands/playfab/api-policy.md) replaces the title's
  whole API access policy, and a wrong policy can lock every player out of the title. So it
  refuses a title from `XBOX_PLAYFAB_TITLE_ID` or a profile the same way, even with
  `--confirm`. Appending, without `--overwrite`, still takes the title from any of them:

  ```text
  $ xbox playfab api-policy set --policy-version 3 --statements-file policy.json --overwrite --confirm
  Using --playfab-title-id ABC123 from saved context.
  '/Admin/UpdatePolicy' with --overwrite acts on playfab-title-id ABC123, which comes from saved context.
  The target of a destructive operation must be named on the command line. To proceed, run:
    xbox --playfab-title-id ABC123 playfab api-policy set --policy-version 3 --statements-file policy.json --overwrite --confirm
  ```

  The command either refusal prints is quoted for PowerShell on Windows and for a POSIX shell
  such as bash or zsh elsewhere, so a `$` or a backtick in a value stays literal when you paste
  it. It leaves out an inline `--body` or `--statements` value, since stderr ends up in logs and
  a request body can hold secrets: `<body>` or `<statements>` stands in for it, and a line
  under the command says to put it back.

- An explicitly blank `--playfab-title-id`, such as an unset shell variable, is a parse
  error rather than a fallback to a saved title.
- A title must be letters and digits wherever it comes from, so one with a line break can't
  add a header to a request. A malformed variable fails the commands that use it, naming it.
- A variable that wins over the profile `--profile` names is reported on stderr, even by a
  read-only command, and a profile your account doesn't have fails.
- Saved values are checked again when they are read, so a hand-edited `context.json` fails
  with the fix instead of reaching a request.
- Saved values are used only with the token of the account they were saved for. If another
  account signs in while a command that uses them runs, say while it waits for confirmation,
  the command fails before it sends anything.

See [Values that don't come from the command line](commands/context.md#values-that-dont-come-from-the-command-line)
on the `context` page for the same rules alongside the rest of the context model.

## What `--confirm` does not prove

`--confirm` is defense-in-depth, not agent verification.

The CLI can require a flag, prompt an interactive human, and fail closed when the
flag is missing. It cannot prove who supplied the flag. An automated agent can
append `--confirm`, so the CLI cannot distinguish "a human approved this exact
operation" from "automation self-asserted confirmation".

Also, the broader read-only convention is not fully CLI-enforced. Operations such
as `Set*`, `Update*`, `Create*`, `Add*`, `Remove*`, `Publish*`, `Cancel*`,
`Request*`, `Enable*`, `Rollover*`, `Upload*`, and `Untag*` can mutate production
state but are not part of the `Delete*` / `Shutdown*` hard gate.

For those operations, safety relies on caller discipline: review the target title,
API path, and full JSON body before running the command.

`xbl achievement-image upload` also registers production images outside
the PlayFab `--confirm` gate; agents need explicit user approval. For uncertain
outcomes, follow the printed read-only recovery command before considering
another upload. See
[XBOX Live achievement images](commands/xbl/achievement-image.md#production-safety-and-upload-recovery).

`xbl publish start` with a `--source-sandbox` different from `--sandbox` also replaces the
destination's global title storage files with the source's and deletes files only the
destination holds. Deleted files can't be recovered. See
[Cross-sandbox publishing replaces global title storage files](commands/xbl.md#cross-sandbox-publishing-replaces-global-title-storage-files).

## Mutations outside PlayFab

Some commands change state outside PlayFab, so the
operation-name rules above do not classify them. Each declares its classification
explicitly, and Help reports it:

| Command | Effect | Confirmation |
| --- | --- | --- |
| [`game create`](commands/game.md) | Creates a Partner Center game. | `--confirm` (`--yes`, `-y`) or a terminal prompt; fails closed otherwise. |
| [`sandbox create`](commands/sandbox.md) | Creates a Shared development sandbox. | `--confirm` (`--yes`, `-y`) or a terminal prompt; fails closed otherwise. |
| [`test-account batch create`](commands/test-account.md) | Creates XBOX test accounts. | `--confirm` (`--yes`, `-y`) or a terminal prompt; fails closed otherwise. See [above](#creating-xbox-test-accounts-asks-first-and-is-never-retried). |
| [`xbl publish start`](commands/xbl.md#publish) | Publishes XBOX Live configuration. | To RETAIL, CERT, CERT.DEBUG, or HIDDEN, or from another sandbox: `--confirm` (`--yes`, `-y`) or a terminal prompt; fails closed otherwise. See [above](#publishing-xbox-live-configuration-to-certification-retail-or-every-sandbox-asks-first). |

`game create` and `sandbox create` cannot be undone from the CLI; a test account batch can be
removed with `test-account batch delete`. None retries its submission. When the outcome of a
submission is uncertain, check the result (`game job get`, `game list`, `sandbox get`, or the
command `test-account batch create` prints) instead of submitting again. `sandbox create` and
`test-account batch create` share exit codes: 3 when the outcome is unknown, 4 when the create
was accepted but the wait ended. As with every mutation, an agent needs explicit user approval
first; `--confirm` does not provide it.

## If you are an AI agent

Default to read-only calls, and get the user's approval before anything that
writes.

Do not treat `--confirm` as permission you can infer for yourself. It is a
technical backstop for catastrophic operations, not proof of human authorization.

Saved context is shared by every session of that OS user, so another session can change
it under you. Pass explicit flags for anything that changes production.

[Using the XBOX CLI from agents](cli-for-agents.md) has the full guidance.

## Practical checklist

Before running any PlayFab command:

- Confirm the target title (`xbox context get` shows the saved one) and use a
  placeholder such as `ABC123` in examples and docs.
- Classify the operation by the last path segment.
- Prefer `Get*`, `List*`, or `Search*` APIs when investigating.
- For `Delete*` or `Shutdown*`, require `--confirm` and explicit human approval.
- For any other mutating verb, require explicit human approval even if the CLI
  does not hard-gate it.
- Use `--output json-compact` and `--data-only` when automation needs compact,
  parseable output.
