---
title: Title data
description: Read, set, and delete title-wide key/value data, either client-readable or internal.
---

# Title data

Manage title-wide key/value data. By default, commands target client-readable
title data. Add `--internal` to target server-only internal title data.

| Command | PlayFab API path(s) | Description |
| --- | --- | --- |
| `playfab title-data get` | `/Admin/GetTitleData` or `/Admin/GetTitleInternalData` | Read one key, several keys, or all keys. Read-only. |
| `playfab title-data set` | `/Admin/SetTitleData` or `/Admin/SetTitleInternalData` | Set one or more title data values. Mutating. |
| `playfab title-data delete` | `/Admin/SetTitleData` or `/Admin/SetTitleInternalData` with `null` values | Remove one or more title data keys. Mutating; this is not a `Delete*` API hard gate. |

No `title-data` command declares a table view, so `--output table` is refused on
all three. `json` (the default) and `json-compact` are the available formats.

## `playfab title-data get`

```bash
xbox --playfab-title-id ABC123 playfab title-data get [key] [flags]
```

| Argument | Required | Description |
| --- | --- | --- |
| `[key]` | No | A single title data key to read. Equivalent to `--key`; supply one or the other, not both. A present-but-blank key is rejected rather than treated as an all-keys read, so an empty shell variable cannot silently widen the request. |

| Option | Description |
| --- | --- |
| `--internal` | Read the server-only internal tier instead of the client-readable tier. |
| `--key <key>` | A single key to read. Equivalent to the positional `key` argument. |
| `--keys <a,b,c>` | Comma-separated keys to read. Cannot be combined with a single key. Must contain at least one key. |
| `--value-only` | Deprecated. It is an alias for the single-key bare-value default: it does not change the output, does not override `--output` or `--data-only`, and writes a deprecation warning to stderr. It still requires a single key. |

Omit both the key and `--keys` to read every key.

### Single-key reads print a bare value

A `get` for one key prints that key's **raw value** by default — no
`{code,status,data}` envelope and no flag required, because reading one key is
the common interactive case:

```bash
xbox --playfab-title-id ABC123 playfab title-data get MyKey
# my-value
```

Ask for structured output explicitly to opt back in. `--output` / `-o` returns
the full PlayFab envelope and `--data-only` returns the unwrapped `data`
payload; either wins over the bare-value default:

```bash
xbox --playfab-title-id ABC123 playfab title-data get MyKey --output json
xbox --playfab-title-id ABC123 playfab title-data get MyKey --data-only
```

Reading multiple keys with `--keys`, or every key by omitting the key entirely,
always returns structured JSON.

A single-key `get` that does not find the key exits non-zero and writes
`Key 'MyKey' was not found.` to stderr, so scripts can branch on the exit code
rather than parsing output.

```bash
xbox --playfab-title-id ABC123 playfab title-data get MyKey
xbox --playfab-title-id ABC123 playfab title-data get MyKey --internal
xbox --playfab-title-id ABC123 playfab title-data get --keys MyKey,OtherKey
xbox --playfab-title-id ABC123 playfab title-data get
```

## `playfab title-data set`

| Option | Description |
| --- | --- |
| `--internal` | Write to the server-only internal tier instead of the client-readable tier. |
| `--key <key>` | The single key to set. Use with `--value`. |
| `--value <value>` | The value to set for `--key`. Stored verbatim and may contain any characters. |
| `--data <data>` | A JSON object of key/value pairs, to set several keys at once. Mutually exclusive with `--key` / `--value`. |

```bash
xbox --playfab-title-id ABC123 playfab title-data set --key MyKey --value my-value
xbox --playfab-title-id ABC123 playfab title-data set --data '{"MyKey":"my-value","OtherKey":"2"}'
```

## `playfab title-data delete`

| Option | Required | Description |
| --- | --- | --- |
| `--keys <a,b,c>` | Yes | Comma-separated title data keys to remove. |
| `--internal` | No | Remove from the server-only internal tier instead of the client-readable tier. |

```bash
xbox --playfab-title-id ABC123 playfab title-data delete --keys MyKey,OldKey
```

The named keys are removed by writing a `null` value through `SetTitleData` or
`SetTitleInternalData`. Because the hard gate classifies by the **PlayFab
operation name** rather than the CLI verb, this `delete` subcommand is not
gated: `--confirm` / `--yes` / `-y` does not apply to it, and only the keys
named in `--keys` are affected. It still mutates production title state — review
it before running. See
[Safety and destructive operations](../../safety.md).

## When a multi-key write stops partway

`set` and `delete` write one key per request, in order, and stop at the first key
that does not succeed. They then write a JSON summary to stderr and exit with code 1:

- `applied` lists the keys that were written. A key whose request succeeded (HTTP 2xx)
  counts as written even if its response was over `PlayFab:MaxResponseBytes` or failed
  to arrive, and the command carries on with the next key.
- `failed` names the key PlayFab rejected (an HTTP 4xx error, including a throttling
  429), with its error. That key was not written. A 4xx whose body failed to arrive is
  still `failed`, and `error` names the status and why the body is missing.
- `unknown` replaces `failed` when the key's request may have reached PlayFab without a
  conclusive answer: it was canceled with Ctrl+C, timed out, or lost its connection, or
  it got a server error (HTTP 5xx, such as 500 or 504). That key may or may not have
  been written, so check it with `title-data get` before running the command again.
  `error` holds what failed (PlayFab's error JSON, when there is one) and
  `outcomeUnknown` holds the warning.
- `notSent` replaces `failed` when the key's request never left: for example, Ctrl+C
  arrived or signing in failed before it was sent, or the DNS lookup or TLS handshake
  failed. That key was not written.

Keys after the one that stopped the command were not attempted.

```json
{
  "applied": ["MyKey"],
  "unknown": {
    "key": "OtherKey",
    "error": "The operation was canceled.",
    "outcomeUnknown": "/Admin/SetTitleData may have reached PlayFab, but the request was canceled, so its outcome is unknown. Check whether it was applied before retrying."
  }
}
```
