---
title: Economy
description: Inspect and modify Economy v2 inventory items, collections, and transactions, and run batched inventory operations.
---

# Economy

Inspect and modify Economy v2 inventory. The implemented command tree is
`playfab economy inventory`, with extra nouns for collections, transactions, and
batched operations.

Inventory commands target the calling entity by default. To target a specific
player or entity, provide both `--entity-id` and `--entity-type`; providing only
one is invalid. For a player, pass their title player account ID with
`--entity-type title_player_account`, not their master player ID;
[`xbox playfab player account get`](player.md) returns it as
`UserInfo.TitleInfo.TitlePlayerAccount.Id`. `--collection-id` selects a
non-default inventory collection on commands that read or mutate collection
contents.

Mutating commands change production title state and require explicit operator
review. `operation execute` is backed by `ExecuteInventoryOperations`, so the
normal hard gate does not classify it as a `Delete*` operation. The CLI still
inspects the batch and requires `--confirm` / `--yes` / `-y` when any operation
inside the batch is a delete; see [Safety and destructive operations](../../safety.md).

## Inventory items

| Command | PlayFab API path | Options | Safety |
| --- | --- | --- | --- |
| `playfab economy inventory list` | `/Inventory/GetInventoryItems` | Optional `--entity-id`, `--entity-type`, `--collection-id`, `--limit`, `--continuation-token`, `--filter`. | Read-only. |
| `playfab economy inventory add` | `/Inventory/AddInventoryItems` | Optional `--entity-id`, `--entity-type`, `--collection-id`, `--idempotency-id`; item reference via `--item-id` with optional `--stack-id`, or full JSON `--item`; `--amount` sets the quantity. | Mutating; not hard-gated. |

`--filter` on `inventory list` is an OData filter over inventory item properties
`type`, `id`, and `stackId`. `--limit` defaults to 10 and controls one returned
page; use `--continuation-token` from the previous response to read later pages.

`inventory add` accepts either a simple catalog item id or a full
`InventoryItemReference` JSON value. `--item` is mutually exclusive with
`--item-id` and `--stack-id`.

`inventory add` returns `ETag`, `IdempotencyId`, and `TransactionIds`, the
transactions the write recorded. It doesn't return the changed inventory or an
operation token, so there's no operation to poll. Run `inventory list` to see
the inventory, or `inventory transaction list` to see the transactions.

## Inventory collections

| Command | PlayFab API path | Options | Safety |
| --- | --- | --- | --- |
| `playfab economy inventory collection list` | `/Inventory/GetInventoryCollectionIds` | Optional `--entity-id`, `--entity-type`, `--limit`, `--continuation-token`. | Read-only. |

## Inventory operations

| Command | PlayFab API path | Options | Safety |
| --- | --- | --- | --- |
| `playfab economy inventory operation execute` | `/Inventory/ExecuteInventoryOperations` | Optional `--entity-id`, `--entity-type`, `--collection-id`, `--idempotency-id`; exactly one of `--operations` or `--operations-file` is required. | Mutating; delete batches are hard-gated by `--confirm` / `--yes` / `-y`. |
| `playfab economy inventory operation status` | `/Inventory/GetInventoryOperationStatus` | `--operation-token` required; pass `--collection-id`, which the API requires; optional `--entity-id`, `--entity-type`. | Read-only. |

`--operations` and `--operations-file` both contain the JSON array of inventory
operations sent transactionally to PlayFab. The CLI rejects a batch containing a
delete operation unless confirmation is supplied, even though the PlayFab API
operation name is `ExecuteInventoryOperations`.

`operation execute` returns the same shape as `inventory add` for the whole
batch: `ETag`, `IdempotencyId`, and `TransactionIds`. It doesn't return
per-operation results or an operation token. Run `inventory list` or
`inventory transaction list` to see what the batch did.

`operation status` reads the status of an inventory transfer that hadn't
finished when its request returned. Its `--operation-token` is the
`OperationToken` that `TransferInventoryItems` or `ExecuteTransferOperations`
returns while their `OperationStatus` is `InProgress`. The CLI has no verb for
either API; call them with `playfab api` after approval for a mutating
call. `inventory add` and `operation execute` don't return an operation token.
A transaction ID isn't an operation token either; PlayFab rejects one with
`Bad OperationToken.`

The CLI accepts `operation status` without `--collection-id`, but PlayFab
rejects the request with `CollectionId cannot be null`, so pass
`--collection-id default` for the default collection.

## Inventory transactions

| Command | PlayFab API path | Options | Safety |
| --- | --- | --- | --- |
| `playfab economy inventory transaction list` | `/Inventory/GetTransactionHistory` | Optional `--entity-id`, `--entity-type`, `--collection-id`, `--limit`, `--continuation-token`, `--filter`, `--order-by`. | Read-only. |

`--filter` on transaction history can refer to `timestamp`, `transactionid`,
`apiname`, and `operationtype`. `--order-by` accepts `timestamp asc` or
`timestamp desc`.

The platform-receipt `Redeem*` endpoints for Apple, Google Play, Microsoft
Store, Nintendo eShop, and PlayStation Store are not exposed as CLI verbs; use
`playfab api` only when you have a receipt to redeem and approval for a
mutating call.

## Table output

`--output table` is opt-in per command. Four `economy` commands declare a table
view; every other `economy` command refuses `table` rather than quietly
returning JSON:

```console
$ xbox --playfab-title-id ABC123 playfab economy inventory add --item-id 11111111-2222-3333-4444-555555555555 --output table
Output format 'table' is not available for this command, because it does not declare a
table view. Omit --output to get this command's default output.
```

Each command's own Help lists its columns under a `TABLE OUTPUT` heading. The
columns are a human convenience and **not a stability contract** — use `json` or
`json-compact` for scripts and agents.

Commands returning a collection render one row per record:

| Command | Columns |
| --- | --- |
| `playfab economy inventory list` | `ID`, `TYPE`, `STACK`, `AMOUNT` |
| `playfab economy inventory collection list` | `COLLECTION ID` |
| `playfab economy inventory transaction list` | `TRANSACTION ID`, `TIMESTAMP`, `API`, `OPERATION` |

`collection list` returns an array of bare strings rather than objects, so its
one column reads the element itself.

The single-record command renders a one-row table with the same headers and
separator:

| Command | Columns |
| --- | --- |
| `playfab economy inventory operation status` | `STATUS` |

The two mutating commands deliberately have no view:

| Command | Why |
| --- | --- |
| `playfab economy inventory add` | Returns an acknowledgement — an ETag, the idempotency id, and the resulting transaction ids — not the inventory it changed. Re-run `inventory list` to see the result. |
| `playfab economy inventory operation execute` | Same acknowledgement shape for a whole batch. One row cannot report per-operation outcomes, so it stays JSON. |

A table never shows the continuation token that `inventory list`,
`collection list`, and `transaction list` return, so paging past the first page
needs `json` or `json-compact`.

`--data-only` has no effect with `--output table`: a table has no response
envelope to strip. A collection view prints `No results.` when the collection is
empty, rather than headers with no rows.

```bash
xbox --playfab-title-id ABC123 playfab economy inventory list --entity-id TITLEPLAYER123 --entity-type title_player_account --output table
```

```text
ID                                    TYPE      STACK    AMOUNT
------------------------------------  --------  -------  ------
11111111-2222-3333-4444-555555555555  currency  default  500
22222222-3333-4444-5555-666666666666  bundle    default  1
```

See [Commands](../../commands.md) for the general output rules.

## Examples

Read one player's inventory, collections, and transaction history:

```bash
xbox --playfab-title-id ABC123 playfab economy inventory list --entity-id TITLEPLAYER123 --entity-type title_player_account --limit 50 --data-only --output json-compact
xbox --playfab-title-id ABC123 playfab economy inventory collection list --entity-id TITLEPLAYER123 --entity-type title_player_account
xbox --playfab-title-id ABC123 playfab economy inventory transaction list --entity-id TITLEPLAYER123 --entity-type title_player_account --order-by "timestamp desc"
```

Add an item, execute a reviewed batch from a file, and read the transactions
they recorded:

```bash
xbox --playfab-title-id ABC123 playfab economy inventory add --entity-id TITLEPLAYER123 --entity-type title_player_account --item-id 11111111-2222-3333-4444-555555555555 --amount 1 --idempotency-id 22222222-3333-4444-5555-666666666666
xbox --playfab-title-id ABC123 playfab economy inventory operation execute --entity-id TITLEPLAYER123 --entity-type title_player_account --operations-file ops.json --idempotency-id 33333333-4444-5555-6666-777777777777
xbox --playfab-title-id ABC123 playfab economy inventory transaction list --entity-id TITLEPLAYER123 --entity-type title_player_account --order-by "timestamp desc"
```

Check a transfer that returned `"OperationStatus": "InProgress"`, using the
`OperationToken` from its response:

```bash
xbox --playfab-title-id ABC123 playfab economy inventory operation status --entity-id TITLEPLAYER123 --entity-type title_player_account --collection-id default --operation-token OPERATIONTOKEN123
```

If `ops.json` contains a delete operation, include confirmation after review:

```bash
xbox --playfab-title-id ABC123 playfab economy inventory operation execute --entity-id TITLEPLAYER123 --entity-type title_player_account --operations-file ops.json --confirm
```

