---
title: Seller
description: List the Partner Center sellers available to the signed-in developer.
---

# Seller

`xbox seller list` lists the Partner Center sellers available to the signed-in developer.
To save one for later commands, use
[`xbox context set seller-id <id>`](context.md).

This command sits at the top level rather than under `playfab`, because it doesn't call a
PlayFab API.

## Commands

| Command | Description |
| --- | --- |
| `seller list` | List the sellers associated with the signed-in developer. |

## Options

| Option | Commands | Description |
| --- | --- | --- |
| `--base-url <url>` | `list` | Base URL for requests, overriding the `XboxPartner:BaseUrl` configuration key. |
| `--output <format>` | `list` | Output format: `json` (default), `json-compact`, or `table`. |

`--data-only` is accepted as a global option but does nothing here. The response
carries no PlayFab response envelope, so there is no wrapper to remove.

Setting `--base-url` to anything other than the shipped default writes a warning to stderr,
because every request carries a Microsoft Entra access token. For the same reason, the URL
must use HTTPS; plain HTTP is accepted only for a loopback address.

Authentication and other execution failures print an error message to stderr without a
stack trace and exit with code 1, leaving stdout empty.

## Table output

`seller list` accepts `--output table`, with `SELLER ID` and `NAME` columns. The default
remains `json`, so scripts and agents are unaffected unless they ask for a table.

Table output is a human convenience, not a stability contract: columns may change without
a breaking-change bump. Use `--output json-compact` for anything parsed by a script.

## Examples

```bash
xbox seller list
xbox seller list --output table
xbox context set seller-id 1234567
```

See [Commands](../commands.md) for the global options that apply across
the CLI, and [Context](context.md) for how the saved seller is used.
