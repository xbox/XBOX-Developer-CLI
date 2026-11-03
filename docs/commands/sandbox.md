---
title: Sandbox
description: List, get, and create the XBOX sandboxes available to the signed-in developer.
---

# Sandbox

Sandboxes are isolated environments in a Partner Center account. They isolate Store products,
such as listings and installs, as well as XBOX services. Global sandboxes such as `RETAIL`
are managed by Microsoft; development sandboxes such as `ABCDEF.1` belong to your account. See
[Sandboxes](https://learn.microsoft.com/gaming/game-publishing/concepts/sandboxes) in the Game
Publishing Guide.

These commands sit at the top level rather than under `xbl`, because a sandbox belongs to
the Partner Center account rather than to XBOX services configuration. They don't call a PlayFab
API.

## Commands

| Command | Description |
| --- | --- |
| `sandbox list` | List the sandboxes Partner Center knows about for the seller, including global sandboxes, following every page. Read-only. |
| `sandbox get <sandbox-id>` | Get one sandbox, including global sandboxes such as `RETAIL` and Isolated sandboxes created in Partner Center: its ID, type, owning seller, and whether it is global. Read-only. |
| `sandbox create <sandbox-id>` | Create a Shared development sandbox, then wait for its creation job. **Mutates production state.** |

## Arguments and options

| Argument or option | Applies to | Required | Description |
| --- | --- | --- | --- |
| `<sandbox-id>` | `get`, `create` | Yes | Sandbox ID, such as `ABCDEF.1`. `get` also accepts global IDs such as `RETAIL`. See [Sandbox IDs](#sandbox-ids). |
| `--seller-id <id>` | all | No | Partner Center seller ID. Takes precedence over `XBOX_SELLER_ID` and the seller saved by `context set`. |
| `--base-url <url>` | all | No | Base URL for requests, overriding the `XboxPartner:BaseUrl` configuration key. |
| `--confirm`, `--yes`, `-y` | `create` | No | Confirm creation without an interactive prompt. |
| `--output <format>` | all | No | `json` (default) or `json-compact`. `table` is accepted by `list` and `get` only. |

Every command sends the seller in the `X-DeveloperAccountId` header, like the
[`game`](game.md) commands, and requires one. The seller resolves from `--seller-id`, then
`XBOX_SELLER_ID`, then the seller saved by [`xbox context set seller-id <id>`](context.md) in
the selected profile.

`--data-only` is accepted as a global option but has no effect here: the response
carries no PlayFab response envelope.

Pass a `sandboxId` returned by `list` to `get` or, exactly as listed, to other commands such
as the [`xbl`](xbl.md) commands' `--sandbox-id` option.

### Sandbox IDs

`get` and `create` validate the ID before signing in, using the same rules as the service.
IDs are at most 64 characters, are case-insensitive, and are normalized to upper case, so
`abcdef.1` refers to `ABCDEF.1`. Only ASCII letters and digits are accepted.

- `get` accepts letters or digits with an optional period-separated suffix, so global
  sandboxes such as `RETAIL`, `READONLY`, and `CERT.DEBUG` can be read, as can `.58`
  sandboxes and `UNDEFINED.N`.
- `create` requires 4 to 60 letters or digits, a period, and a number from `0` to `999`
  without a leading zero, so at most 64 characters in total. It also rejects:
  - the suffix `.58`, which is reserved: Partner Center always provisions `*.58` as an
    Isolated sandbox, so it can't be created as Shared;
  - the name `UNDEFINED`, in any case;
  - the global sandboxes `RETAIL`, `CERT`, `CERT.DEBUG`, `READONLY`, and `MSFT.1`.

## Getting a sandbox

`sandbox get` writes one JSON object:

```json
{ "sandboxId": "ABCDEF.58", "type": "Isolated", "sellerId": "1234567", "global": false }
```

- `type` is `Shared`, `Isolated`, or `Unknown`, for a permission model the CLI doesn't
  recognize. `sandbox create` only makes Shared sandboxes, but `get` also shows Isolated ones,
  such as those created in Partner Center.
- `sellerId` is the owning seller, or `null` for a global sandbox.
- `global` is `true` for global sandboxes such as `RETAIL`, which XBOX owns.

## Listing sandboxes

`sandbox list` writes a JSON array with one object per sandbox, in the same shape `get`
returns:

```json
[
  { "sandboxId": "RETAIL", "type": "Unknown", "sellerId": null, "global": true },
  { "sandboxId": "ABCDEF.1", "type": "Shared", "sellerId": "1234567", "global": false }
]
```

The list is returned in pages; the command follows every `continuationToken` itself,
with one correlation ID for the whole listing. It fails rather than loop if a token
repeats or the list doesn't end within 1,000 pages.

The list now comes from Partner Center rather than from XBOX services. It shows the same
sandboxes as the Sandboxes page in Partner Center. This changes it in three ways for scripts
written against earlier versions:

- Each entry is an object rather than a sandbox name string. Read `sandboxId` for the name.
- A seller is required. Earlier versions used a developer's only seller when
  none was given; now pass `--seller-id`, set `XBOX_SELLER_ID`, or run
  `xbox context set seller-id <id>` once.
- Errors are human-readable text on stderr rather than JSON; branch on the exit code. See
  [Errors](#errors).

## Table output

`sandbox list --output table` renders `SANDBOX`, `TYPE`, and `GLOBAL` columns.
`sandbox get --output table` renders one row with `SANDBOX ID`, `TYPE`, and `SELLER ID`
columns. `sandbox create` does not accept `--output table`. Table output is a human
convenience, not a stability contract; use `--output json-compact` for anything parsed by a
script.

## Creating a sandbox

`sandbox create` writes to production and cannot be undone from the CLI. It requires
`--confirm` (`--yes`, `-y`) or a confirmation prompt at a terminal, and fails closed
otherwise. Agents additionally need explicit user approval — see
[Safety and destructive operations](../safety.md).

```bash
xbox sandbox create ABCDEF.1 --seller-id 1234567 --confirm
```

The command submits the request once, then waits up to 120 seconds for the creation job,
polling every 5 seconds unless the service supplies a `Retry-After` header, and never more
than once a second. When the job succeeds, it reads the sandbox back, retrying once if the new
sandbox is not yet readable, and writes it to stdout as JSON, in the same shape as
`sandbox get`. When the job fails, it writes the job object, including its `errors`, to
stdout, as `game create` does. Submission progress, the job ID, the correlation ID, a one-line
outcome, and recovery commands go to stderr.

Submission is never retried. If the request is interrupted, times out, or the response is
a server error or a redirect, the outcome is unknown: the command exits `3`, and you should check with
`sandbox get` instead of submitting again. The same applies if the request is accepted
but the response has a job ID that isn't a GUID, because the job can't be followed, or a job that is
already complete without a `succeeded` or `failed` result. A client error such
as `400` (invalid ID), `403`, or `409` (the sandbox already exists) is a definite rejection and
exits `1`. So does a failure before the request is sent, such as no cached sign-in or an invalid
`--base-url`: nothing was submitted, so fix the problem and run the command again.

There is no command to follow a creation job. If the wait ends before the sandbox is confirmed
(the job is still running after 120 seconds, the wait is canceled, polling fails, the job
completes without a `succeeded` or `failed` result, or the new sandbox can't be read back), the
command writes nothing to stdout, says so on stderr, and exits `4`. Check for the sandbox with
`sandbox get <sandbox-id>` rather than submitting again. Only a job whose result is `failed`
exits `2`.

### Exit codes

These match [`test-account batch create`](test-account.md).

| Exit code | Meaning |
| --- | --- |
| `0` | The sandbox was created and read back. |
| `1` | The sandbox was not created: validation or confirmation failed, the request failed before it was sent, or the request was rejected. |
| `2` | The creation job completed with the result `failed`. The job object, with its errors, is written to stdout. |
| `3` | The request was sent but its outcome is unknown. Check with `sandbox get`; don't resubmit. |
| `4` | The job was accepted but the wait ended before the sandbox was confirmed. Nothing is written to stdout. Check with `sandbox get`; don't resubmit. |

## Errors

A failed request exits 1; `sandbox create` also uses the [exit codes](#exit-codes) above.
Errors go to stderr as human-readable text, not JSON, whatever `--output` is: the
correlation ID, a line of guidance for the HTTP status, and the error `detail` when the
response includes one. `--verbose` adds the response body. Scripts should branch on the exit
code, never on the wording of stderr.

## Examples

```bash
xbox sandbox list

xbox sandbox list --output table

xbox sandbox list --seller-id 1234567 --output json-compact

xbox sandbox get ABCDEF.1 --seller-id 1234567

xbox sandbox get ABCDEF.1 --output table

xbox sandbox get RETAIL

xbox sandbox create ABCDEF.2 --seller-id 1234567 --confirm --output json-compact
```

See [Commands](../commands.md) for the global options that apply across the CLI.
