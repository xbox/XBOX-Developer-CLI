---
title: Title data
description: Read, set, and delete title-wide key/value data, either client-readable or internal.
---

# Title data

Manage title-wide key/value data. Commands target client-readable title data, which
players' game clients read, unless you add `--internal` to target server-only internal
title data. You need **Admin** permission on the title; see
[How PlayFab authentication works](../../authentication.md#how-playfab-authentication-works).

| Command | PlayFab API path | Description |
| --- | --- | --- |
| `playfab title-data get` | `/Admin/GetTitleData` or `/Admin/GetTitleInternalData` | Read one key, several keys, or all keys. |
| `playfab title-data set` | `/Admin/SetTitleData` or `/Admin/SetTitleInternalData` | Set one or more keys. |
| `playfab title-data delete` | `/Admin/SetTitleData` or `/Admin/SetTitleInternalData`, with `null` values | Remove one or more keys. |

`title-data` commands don't accept `--output table`.

## `playfab title-data get`

```text
xbox --playfab-title-id ABC123 playfab title-data get [key] [flags]
```

| Argument / option | Description |
| --- | --- |
| `[key]` | One key to read. Same as `--key`; pass one or the other. A blank key is refused, rather than read as every key. |
| `--key <key>` | One key to read. |
| `--keys <a,b,c>` | Comma-separated keys to read. Can't be combined with a single key. |
| `--internal` | Read internal title data instead of client-readable title data. |

Omit the key and `--keys` to read every key.

A `get` for one key prints the key's bare value. To get JSON instead, pass `--output` for
the full envelope, or `--data-only` for its `data`. Reading several keys or every key
always returns JSON.

```bash
xbox --playfab-title-id ABC123 playfab title-data get MyKey
```

```text
my-value
```

When the one key doesn't exist, `get` writes `Key 'MyKey' was not found.` to stderr and
exits with code `1`.

```bash
xbox --playfab-title-id ABC123 playfab title-data get MyKey --internal
xbox --playfab-title-id ABC123 playfab title-data get MyKey --output json
xbox --playfab-title-id ABC123 playfab title-data get --keys MyKey,OtherKey
xbox --playfab-title-id ABC123 playfab title-data get
```

## `playfab title-data set`

| Option | Description |
| --- | --- |
| `--key <key>` | The key to set. Use with `--value`. |
| `--value <value>` | The value for `--key`, stored as is. |
| `--data <data>` | A JSON object of key/value pairs, to set several keys at once. Can't be combined with `--key` and `--value`. |
| `--internal` | Write internal title data instead of client-readable title data. |

Set one key:

```bash
xbox --playfab-title-id ABC123 playfab title-data set --key MyKey --value my-value
```

Or set several keys at once:

```bash
xbox --playfab-title-id ABC123 playfab title-data set --data '{"MyKey":"my-value","OtherKey":"2"}'
```

On success, `set` writes the keys it set, such as `{"applied":["MyKey","OtherKey"]}`.

## `playfab title-data delete`

| Option | Required | Description |
| --- | --- | --- |
| `--keys <a,b,c>` | Yes | Comma-separated keys to remove. |
| `--internal` | No | Remove internal title data instead of client-readable title data. |

```bash
xbox --playfab-title-id ABC123 playfab title-data delete --keys MyKey,OldKey
```

On success, `delete` writes the keys it removed, such as `{"applied":["MyKey","OldKey"]}`.

## When a multi-key write stops partway

`set` and `delete` write one key at a time, in order, and stop at the first key that
doesn't succeed. Keys before it were written; keys after it weren't tried. The command
exits with code `1` and writes a summary to stderr:

| Field | Meaning |
| --- | --- |
| `applied` | The keys that were written. |
| `failed` | The key PlayFab rejected, with its error. It wasn't written. |
| `unknown` | The key whose request might have reached PlayFab without a conclusive answer, such as after a timeout, Ctrl+C, or an HTTP 5xx error. It might have been written, so check it with `title-data get` before you run the command again. |
| `notSent` | The key whose request never left, such as when signing in failed. It wasn't written. |

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
