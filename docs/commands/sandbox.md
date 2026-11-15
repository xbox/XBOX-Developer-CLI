---
title: Sandbox
description: List, get, and create the XBOX sandboxes available to the signed-in developer.
---

# Sandbox

An XBOX sandbox is an isolated environment in a Partner Center account, for Microsoft Store
products and XBOX services. Global sandboxes such as `RETAIL` are managed by Microsoft;
development sandboxes such as `ABCDEF.1` belong to your account. See
[Sandboxes](https://learn.microsoft.com/gaming/game-publishing/concepts/sandboxes) in the Game
Publishing Guide. To switch the sandbox a Windows PC uses, see [PC](pc.md).

Each command acts on one seller, from `--seller-id` or your saved seller; see
[Context](context.md). If that isn't the seller you last selected in Partner Center, see
[this known issue](../known-issues.md#xbox-sandbox-commands-show-only-global-sandboxes).

## Commands

| Command | Description |
| --- | --- |
| `sandbox list` | List the seller's sandboxes and the global sandboxes, as Partner Center's Sandboxes page shows them. |
| `sandbox get <sandbox-id>` | Get one sandbox, including a global or Isolated one: its ID, type, owning seller, and whether it's global. |
| `sandbox create <sandbox-id>` | Create a Shared development sandbox, then wait for its creation job. |

## Arguments and options

| Argument or option | Applies to | Required | Description |
| --- | --- | --- | --- |
| `<sandbox-id>` | `get`, `create` | Yes | Sandbox ID, such as `ABCDEF.1`. Case doesn't matter. See [Sandbox IDs](#sandbox-ids). |
| `--seller-id <id>` | all | If not saved | Partner Center seller ID. |
| `--base-url <url>` | all | No | Base URL for requests, overriding the `XboxPartner:BaseUrl` configuration key. |
| `--confirm`, `--yes`, `-y` | `create` | No | Create the sandbox without the confirmation prompt. |
| `--output <format>` | all | No | `json` (default) or `json-compact`; `table` on `list` and `get` only. |

Pass a `sandboxId` from `list` to other commands, such as the [`xbl`](xbl.md) commands'
`--sandbox-id` option.

### Sandbox IDs

A sandbox ID is letters and digits with an optional period-separated suffix, up to 64
characters. The CLI converts it to upper case.

- `get` accepts any sandbox ID, including global ones such as `RETAIL` and `CERT.DEBUG`.
- `create` needs 4 to 60 letters or digits, a period, and a number from `0` to `999` without a
  leading zero, such as `ABCDEF.1`. The suffix `.58` is reserved for Isolated sandboxes, and
  `UNDEFINED` and the global sandbox names can't be used.

## Output

`sandbox get` writes one sandbox:

```json
{ "sandboxId": "ABCDEF.58", "type": "Isolated", "sellerId": "1234567", "global": false }
```

| Field | Description |
| --- | --- |
| `sandboxId` | The sandbox ID. |
| `type` | `Shared`, `Isolated`, or `Unknown`. `create` makes only Shared sandboxes. |
| `sellerId` | The owning seller, or `null` for a global sandbox. |
| `global` | `true` for a global sandbox, such as `RETAIL`. |

`sandbox list` writes a `sandboxes` array of the same objects:

```json
{
  "sandboxes": [
    { "sandboxId": "RETAIL", "type": "Shared", "sellerId": null, "global": true },
    { "sandboxId": "ABCDEF.1", "type": "Shared", "sellerId": "1234567", "global": false }
  ]
}
```

`list --output table` has `SANDBOX`, `TYPE`, and `GLOBAL` columns, and `get --output table` has
`SANDBOX ID`, `TYPE`, and `SELLER ID`. `create` refuses `--output table`.

## Creating a sandbox

```bash
xbox sandbox create ABCDEF.1 --seller-id 1234567 --confirm
```

`sandbox create` asks before it creates the sandbox; without a terminal, it needs `--confirm`.
The CLI can't delete a sandbox. If you belong to several sellers, check
[the known issue](../known-issues.md#xbox-sandbox-commands-show-only-global-sandboxes) first: the
sandbox can be created in another seller.

The command sends the request once and waits up to two minutes for the creation job. When the
job succeeds, it writes the new sandbox in the same shape as `sandbox get`. The job ID,
correlation ID, and what to check next go to stderr. When the exit code is `3` or `4`, check with
`sandbox get <sandbox-id>`, and don't create the sandbox again.

| Exit code | Meaning |
| --- | --- |
| `0` | The sandbox was created. |
| `1` | The sandbox wasn't created: the input or confirmation was refused, the request wasn't sent, or the service rejected it, such as with `409` when the sandbox exists. Fix the problem and run the command again. |
| `2` | The creation job failed. The job, with its `errors`, is written to stdout. |
| `3` | The request was sent, but its outcome is unknown. Check with `sandbox get`. |
| `4` | The job was accepted, but the wait ended before the sandbox was confirmed. Nothing is written to stdout. Check with `sandbox get`. |

## Errors

A failure writes text to stderr, whatever `--output` is: the correlation ID, guidance for the
HTTP status, and the service's error detail when there is one. `--verbose` adds the response
body. Scripts should branch on the exit code, not the wording.

## Examples

List sandboxes, and get two of them:

```bash
xbox sandbox list --output table
xbox sandbox get ABCDEF.1
xbox sandbox get RETAIL
```

Create a sandbox without the confirmation prompt:

```bash
xbox sandbox create ABCDEF.2 --seller-id 1234567 --confirm --output json-compact
```
