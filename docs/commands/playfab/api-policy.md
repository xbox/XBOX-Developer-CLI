---
title: API policy
description: Read and update the title's API access policy, which controls which callers may invoke which PlayFab APIs.
---

# API policy

Read and update the title's API access policy — the statement set that controls
which callers may invoke which PlayFab APIs.

No `api-policy` command declares a table view, so `--output table` is refused on
both. `json` (the default) and `json-compact` are the available formats.

| Command | PlayFab API path | Description |
| --- | --- | --- |
| `playfab api-policy get` | `/Admin/GetPolicy` | Get the current policy statements and version. |
| `playfab api-policy set` | `/Admin/UpdatePolicy` | Replace or append policy statements. Mutating. |

| Command | Options |
| --- | --- |
| `api-policy get` | `--policy-name` (optional; defaults to `ApiPolicy`). |
| `api-policy set` | `--policy-name` (defaults to `ApiPolicy`); either `--statements` (inline JSON array) or `--statements-file`, exactly one required; `--policy-version` (required); `--overwrite` to replace the whole statement set instead of appending (requires `--confirm`, and `--playfab-title-id` on the command line). |

```bash
xbox --playfab-title-id ABC123 playfab api-policy get
xbox --playfab-title-id ABC123 playfab api-policy set --policy-version 3 --statements-file policy.json
```

`--policy-version` is required because `UpdatePolicy` expects the *current*
version. Run `get` first to read it: this makes a blind overwrite of a policy
you have not looked at much harder. `--overwrite` is opt-in; the default appends
to the existing statements.

> [!WARNING]
> **`--overwrite` requires `--confirm`.** It replaces the entire policy with the
> statements you supply, discarding every statement not listed, and the previous
> policy cannot be recovered through this CLI. `UpdatePolicy` is not a
> `Delete*`/`Shutdown*` path, so the built-in hard gate does not fire on it — the
> command applies the requirement itself. Appending (the default) is not gated.

A wrong policy can lock every player out of the title, so `--overwrite` also
needs the title named with `--playfab-title-id` on the same command. It refuses a
title from `XBOX_PLAYFAB_TITLE_ID` or saved context, even with `--confirm`, and
prints the command to run instead, with `<statements>` in place of an inline
`--statements` value:

```text
$ xbox playfab api-policy set --policy-version 3 --statements-file policy.json --overwrite --confirm
Using --playfab-title-id ABC123 from saved context.
'/Admin/UpdatePolicy' with --overwrite acts on playfab-title-id ABC123, which comes from saved context.
The target of a destructive operation must be named on the command line. To proceed, run:
  xbox --playfab-title-id ABC123 playfab api-policy set --policy-version 3 --statements-file policy.json --overwrite --confirm
```

Appending takes the title from the flag, `XBOX_PLAYFAB_TITLE_ID`, or saved
context, and names on stderr where it came from.

Statement fields are validated before the request is sent, and an unrecognized
field is an error rather than being ignored:

```console
$ xbox ... playfab api-policy set --policy-version 3 --statements-file policy.json
Invalid policy statements: unknown field 'ApiCondition'. Valid fields are Action, ApiConditions, Comment, Effect, Principal, Resource. Field names are matched case-insensitively; check for typos, because an unrecognized field would otherwise be dropped and could silently change what the statement permits.
```

This matters because a dropped field changes what a statement *permits*: a
misspelled `ApiConditions` would discard the condition and leave an
unconditional permission in its place, widening access while the command still
reported success. Field names themselves remain case-insensitive, so `effect`
and `Effect` both bind.

`UpdatePolicy` changes production API access control. It is an `Update*`
operation, so it is not behind the `Delete*`/`Shutdown*` hard gate, but it is a
mutation — see [Safety and destructive operations](../../safety.md).
