---
title: API policy
description: Read and update the title's API access policy, which controls which callers can use which PlayFab APIs.
---

# API policy

Read and update the title's API access policy, the statements that control which callers
can use which PlayFab APIs. A wrong policy can lock every player out of the title. You
need **Admin** permission on the title; see
[How PlayFab authentication works](../../authentication.md#how-playfab-authentication-works).

| Command | PlayFab API path | Description |
| --- | --- | --- |
| `playfab api-policy get` | `/Admin/GetPolicy` | Get the policy's statements and version. |
| `playfab api-policy set` | `/Admin/UpdatePolicy` | Add statements to the policy, or replace it. |

`api-policy` commands don't accept `--output table`.

| Option | Command | Required | Description |
| --- | --- | --- | --- |
| `--policy-name` | `get`, `set` | No | The policy. Defaults to `ApiPolicy`. |
| `--statements` | `set` | One of these two | The statements, as an inline JSON array. |
| `--statements-file` | `set` | One of these two | A file holding the statements as a JSON array. |
| `--policy-version` | `set` | Yes | The policy's current version, which `get` returns. PlayFab refuses the update if the policy has changed since. |
| `--overwrite` | `set` | No | Replace every statement in the policy, rather than adding to them. See [Replace the policy](#replace-the-policy). |

## Add statements

Read the current policy and its version:

```bash
xbox --playfab-title-id ABC123 playfab api-policy get
```

Put the statements to add in a file. This `policy.json` denies the Server API's
`DeleteCharacterFromUser` to every caller:

```json
[
  {
    "Resource": "pfrn:api--/Server/DeleteCharacterFromUser",
    "Action": "*",
    "Effect": "Deny",
    "Principal": "*",
    "Comment": "Disable server character delete"
  }
]
```

- `Resource` is `pfrn:api--` and an API path, or `pfrn:api--*` for every API.
- `Action` is `*`, the only value PlayFab accepts.
- `Effect` is `Allow` or `Deny`.
- `Principal` is `*` to match every caller.
- `Comment` is optional text for your own records.
- `ApiConditions` is optional, and sets conditions such as
  `{ "HasSignatureOrEncryption": "True" }`, which takes `Any`, `True`, or `False`.

Field names aren't case-sensitive. An unrecognized field, such as a misspelled
`ApiConditions`, is an error, because dropping it could widen what the statement allows.
PlayFab's [API access policy](https://learn.microsoft.com/gaming/playfab/api-references/api-access-policy)
page describes each field, and shows the default policy that allows every API.

Then add the statements, passing the version that `get` returned:

```bash
xbox --playfab-title-id ABC123 playfab api-policy set --policy-version 3 --statements-file policy.json
```

## Replace the policy

> [!CAUTION]
> `--overwrite` replaces the whole policy with the statements you pass, and discards every
> statement you leave out. The CLI can't restore the previous policy, so save the output
> of `get` first.

`--overwrite` needs `--confirm`, and the title named with `--playfab-title-id` on the same
command. It refuses a title from `XBOX_PLAYFAB_TITLE_ID` or saved context, even with
`--confirm`, and prints the command to run instead:

```text
$ xbox playfab api-policy set --policy-version 3 --statements-file policy.json --overwrite --confirm
Using --playfab-title-id ABC123 from saved context.
'/Admin/UpdatePolicy' with --overwrite acts on playfab-title-id ABC123, which comes from saved context.
The target of a destructive operation must be named on the command line. To proceed, run:
  xbox --playfab-title-id ABC123 playfab api-policy set --policy-version 3 --statements-file policy.json --overwrite --confirm
```
