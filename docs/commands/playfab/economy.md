---
title: Economy
description: Inspect and modify Economy v2 inventory items, collections, and transactions, and run batched inventory operations.
---

# Economy

Read and change PlayFab Economy v2 inventory with `playfab economy inventory`, including
collections, transaction history, and batched inventory operations.

Inventory commands target the calling entity unless you pass both `--entity-id` and
`--entity-type`. For a player, pass their title player account ID with
`--entity-type title_player_account`, not their master player account ID;
[`playfab player account get`](player.md) returns it as
`UserInfo.TitleInfo.TitlePlayerAccount.Id`. `--collection-id` selects an inventory
collection other than `default`.

`operation execute` refuses a batch that contains a `Delete` operation unless you pass
`--confirm` (`--yes`, `-y`), even in a terminal. Other inventory changes run without asking.
See [Safety and destructive operations](../../safety.md).

## Inventory items

| Command | PlayFab API path | Options |
| --- | --- | --- |
| `playfab economy inventory list` | `/Inventory/GetInventoryItems` | Optional `--entity-id`, `--entity-type`, `--collection-id`, `--limit`, `--continuation-token`, `--filter`. |
| `playfab economy inventory add` | `/Inventory/AddInventoryItems` | Optional `--entity-id`, `--entity-type`, `--collection-id`, `--idempotency-id`. The item: `--item-id` with optional `--stack-id`, or an `InventoryItemReference` JSON object with `--item`. `--amount` sets the quantity. |

- `inventory list` returns one page of `--limit` items, 10 by default. To get the next page,
  pass the response's continuation token with `--continuation-token`.
- `--filter` is an OData filter over the item properties `type`, `id`, and `stackId`.
- `inventory add` returns `ETag`, `IdempotencyId`, and the `TransactionIds` it recorded, not
  the changed inventory. Run `inventory list` to see the result.

## Inventory collections

| Command | PlayFab API path | Options |
| --- | --- | --- |
| `playfab economy inventory collection list` | `/Inventory/GetInventoryCollectionIds` | Optional `--entity-id`, `--entity-type`, `--limit`, `--continuation-token`. |

## Inventory operations

| Command | PlayFab API path | Options |
| --- | --- | --- |
| `playfab economy inventory operation execute` | `/Inventory/ExecuteInventoryOperations` | Optional `--entity-id`, `--entity-type`, `--collection-id`, `--idempotency-id`. The operations, as a JSON array, with exactly one of `--operations` or `--operations-file`. |
| `playfab economy inventory operation status` | `/Inventory/GetInventoryOperationStatus` | `--operation-token` (required); `--entity-id` and `--entity-type` of the entity that gave the items; optional `--collection-id`. |

`operation execute` runs the batch as one transaction. Like `inventory add`, it returns
`ETag`, `IdempotencyId`, and `TransactionIds` for the whole batch. Run `inventory list` or
`inventory transaction list` to see what it did.

`operation status` checks an inventory transfer that hadn't finished when its request
returned. `TransferInventoryItems` and `ExecuteTransferOperations` return an
`OperationToken` while their `OperationStatus` is `InProgress`; pass that token, and the
transfer's `GivingEntity` as `--entity-id` and `--entity-type`. The CLI has no command for
those transfer APIs, so call them with [`playfab api`](api.md). A transaction ID isn't an
operation token, and PlayFab rejects one with `Bad OperationToken.`

## Inventory transactions

| Command | PlayFab API path | Options |
| --- | --- | --- |
| `playfab economy inventory transaction list` | `/Inventory/GetTransactionHistory` | Optional `--entity-id`, `--entity-type`, `--collection-id`, `--limit`, `--continuation-token`, `--filter`, `--order-by`. |

`--filter` can refer to `timestamp`, `transactionid`, `apiname`, and `operationtype`.
`--order-by` accepts `timestamp asc` or `timestamp desc`.

## Table output

These commands accept `--output table`; `inventory add` and `operation execute` don't. See
[Table output](../../commands.md#table-output).

| Command | Columns |
| --- | --- |
| `playfab economy inventory list` | `ID`, `TYPE`, `STACK`, `AMOUNT` |
| `playfab economy inventory collection list` | `COLLECTION ID` |
| `playfab economy inventory transaction list` | `TRANSACTION ID`, `TIMESTAMP`, `API`, `OPERATION` |
| `playfab economy inventory operation status` | `STATUS` |

A table doesn't show the continuation token, so use JSON output to page past the first
page.

```bash
xbox --playfab-title-id ABC123 playfab economy inventory list --entity-id TITLEPLAYER123 --entity-type title_player_account --output table
```

```text
ID                                    TYPE      STACK    AMOUNT
------------------------------------  --------  -------  ------
11111111-2222-3333-4444-555555555555  currency  default  500
22222222-3333-4444-5555-666666666666  bundle    default  1
```

## Examples

Read one player's inventory, collections, and transaction history:

```bash
xbox --playfab-title-id ABC123 playfab economy inventory list --entity-id TITLEPLAYER123 --entity-type title_player_account --limit 50 --data-only --output json-compact
xbox --playfab-title-id ABC123 playfab economy inventory collection list --entity-id TITLEPLAYER123 --entity-type title_player_account
xbox --playfab-title-id ABC123 playfab economy inventory transaction list --entity-id TITLEPLAYER123 --entity-type title_player_account --order-by "timestamp desc"
```

Add one of an item to a player's inventory:

```bash
xbox --playfab-title-id ABC123 playfab economy inventory add --entity-id TITLEPLAYER123 --entity-type title_player_account --item-id 11111111-2222-3333-4444-555555555555 --amount 1 --idempotency-id 22222222-3333-4444-5555-666666666666
```

Run a batch of inventory operations from `ops.json`:

```bash
xbox --playfab-title-id ABC123 playfab economy inventory operation execute --entity-id TITLEPLAYER123 --entity-type title_player_account --operations-file ops.json --idempotency-id 33333333-4444-5555-6666-777777777777
```

If `ops.json` contains a `Delete` operation, add `--confirm`:

```bash
xbox --playfab-title-id ABC123 playfab economy inventory operation execute --entity-id TITLEPLAYER123 --entity-type title_player_account --operations-file ops.json --confirm
```

Check a transfer that returned `"OperationStatus": "InProgress"`, using the
`OperationToken` from its response and the player who gave the items:

```bash
xbox --playfab-title-id ABC123 playfab economy inventory operation status --entity-id TITLEPLAYER123 --entity-type title_player_account --operation-token OPERATIONTOKEN123
```
