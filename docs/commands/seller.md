---
title: Seller
description: List the Partner Center sellers available to the signed-in developer.
---

# Seller

`xbox seller list` lists the Partner Center sellers you belong to:

```bash
xbox seller list --output table
```

Save the ID of the one you use with [`xbox context set seller-id <id>`](context.md), so later
commands use it:

```bash
xbox context set seller-id 1234567
```

If you belong to several sellers and one is missing, see
[this known issue](../known-issues.md#xbox-seller-list-sometimes-leaves-out-one-of-your-sellers).

## Commands

| Command | Description |
| --- | --- |
| `seller list` | List the sellers associated with the signed-in developer. |

## Options

| Option | Description |
| --- | --- |
| `--base-url <url>` | Base URL for requests, overriding the `XboxPartner:BaseUrl` configuration key. |
| `--output <format>` | `json` (default), `json-compact`, or `table`. |

`seller list` accepts `--output table`, with `SELLER ID` and `NAME` columns. A failure exits `1`
with the error on stderr.
