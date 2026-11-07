// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using XboxDevCLI.Output;
using System.Text.Json;
using XboxDevCLI.PlayFab.Generated.Economy;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Services;
using static XboxDevCLI.PlayFab.Commands.PlayFabCommandHelper;

namespace XboxDevCLI.PlayFab.Commands;

/// <summary>
/// Creates the <c>playfab economy inventory</c> command group: reading and mutating an entity's
/// PlayFab Economy v2 inventory. The verbs wrap the <c>/Inventory/*</c> endpoints:
/// <list type="bullet">
///   <item><c>list</c> — <c>GetInventoryItems</c> (paged, optional OData <c>--filter</c>).</item>
///   <item><c>collection list</c> — <c>GetInventoryCollectionIds</c> (paged).</item>
///   <item><c>transaction list</c> — <c>GetTransactionHistory</c> (paged, optional <c>--filter</c>/<c>--order-by</c>).</item>
///   <item><c>add</c> — <c>AddInventoryItems</c> (an <c>--item-id</c>/<c>--item</c> reference + <c>--amount</c>).</item>
///   <item><c>operation execute</c> — <c>ExecuteInventoryOperations</c> (a batch of operations as JSON).</item>
///   <item><c>operation status</c> — <c>GetInventoryOperationStatus</c> (the status of an in-progress inventory transfer, by its operation token).</item>
/// </list>
/// The target entity is addressed with <c>--entity-id</c>/<c>--entity-type</c> (both or neither;
/// omitting both defers to the calling entity). <c>add</c> and <c>operation execute</c> mutate
/// production inventory but are not <c>Delete*</c>/<c>Shutdown*</c> paths, so the name-based hard
/// gate in <c>PlayFabApiClient</c> does not fire for them. Because an <c>operation execute</c>
/// batch can still carry a <c>Delete</c> operation, <c>operation execute</c> re-applies the
/// confirmation requirement itself: a batch containing a delete is refused unless
/// <c>--confirm</c> is supplied. The platform-receipt <c>Redeem*</c> verbs and a dedicated
/// <c>DeleteInventoryItems</c> verb are not exposed here.
/// </summary>
internal static class EconomyCommandFactory
{
    internal const string GetInventoryItemsPath = "/Inventory/GetInventoryItems";
    internal const string GetInventoryCollectionIdsPath = "/Inventory/GetInventoryCollectionIds";
    internal const string GetTransactionHistoryPath = "/Inventory/GetTransactionHistory";
    internal const string AddInventoryItemsPath = "/Inventory/AddInventoryItems";
    internal const string ExecuteInventoryOperationsPath = "/Inventory/ExecuteInventoryOperations";
    internal const string GetInventoryOperationStatusPath = "/Inventory/GetInventoryOperationStatus";

    /// <summary>Default page size, matching PlayFab's documented server-side default of 10.</summary>
    internal const int DefaultCount = 10;

    /// <summary>
    /// The default collection id ("default") used by Inventory when none is specified.
    /// Sent explicitly by <c>transaction list</c>, since GetTransactionHistory rejects requests
    /// that omit it.
    /// </summary>
    internal const string DefaultCollectionId = "default";

    /// <summary>
    /// Case-insensitive deserialization for JSON escape-hatch options (<c>--item</c>,
    /// <c>--operations</c>) so users need not match the PascalCase wire names exactly.
    /// </summary>
    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static Command Create()
    {
        var economy = new Command("economy", "Manage Economy v2 inventory");
        economy.Subcommands.Add(CreateInventoryCommand());
        return economy;
    }

    private static Command CreateInventoryCommand()
    {
        var inventory = new Command("inventory", "Read and mutate an entity's Economy v2 inventory.");
        inventory.Subcommands.Add(CreateListCommand());
        inventory.Subcommands.Add(CreateAddCommand());

        // collection: the set of named inventory collections an entity owns.
        var collection = new Command("collection", "Inspect an entity's inventory collections.");
        collection.Subcommands.Add(CreateCollectionsCommand());
        inventory.Subcommands.Add(collection);

        // transaction: the entity's inventory transaction history.
        var transaction = new Command("transaction", "Inspect an entity's inventory transactions.");
        transaction.Subcommands.Add(CreateHistoryCommand());
        inventory.Subcommands.Add(transaction);

        // operation: batched inventory operations, and the status of inventory transfers.
        var operation = new Command("operation", "Execute batched inventory operations, and check the status of inventory transfers.");
        operation.Subcommands.Add(CreateOpsCommand());
        operation.Subcommands.Add(CreateStatusCommand());
        inventory.Subcommands.Add(operation);

        return inventory;
    }

    #region Shared option factories

    private static Option<string?> EntityIdOption() =>
        OptionalOption("--entity-id", "The id of the entity whose inventory to target. Requires --entity-type. Omit both to default to the calling entity.");

    private static Option<string?> EntityTypeOption() =>
        OptionalOption("--entity-type", "The type of the target entity (e.g. 'title_player_account'). Requires --entity-id.");

    private static Option<string?> CollectionIdOption() =>
        OptionalOption("--collection-id", "Optional inventory collection id to target (default: 'default').");

    private static Option<int?> CountOption() =>
        new("--limit")
        {
            Description =
                $"Maximum number of records to return (default {DefaultCount}). Because each " +
                "invocation fetches a single page, this is also the page size sent to PlayFab; " +
                "use --continuation-token to retrieve further pages.",
        };

    private static Option<string?> ContinuationTokenOption() =>
        OptionalOption("--continuation-token", "Opaque token from a previous page's response to retrieve the next page.");

    #endregion

    #region list

    private static Command CreateListCommand()
    {
        var entityIdOption = EntityIdOption();
        var entityTypeOption = EntityTypeOption();
        var collectionIdOption = CollectionIdOption();
        var countOption = CountOption();
        var continuationTokenOption = ContinuationTokenOption();
        var filterOption = OptionalOption("--filter", "Optional OData filter over inventory item properties 'type', 'id', and 'stackId' (e.g. \"type eq 'currency'\").");

        var command = new Command("list", "List an entity's inventory items (GetInventoryItems).")
        {
            entityIdOption,
            entityTypeOption,
            collectionIdOption,
            countOption,
            continuationTokenOption,
            filterOption,
        };

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            WithClientAsync(parseResult, cancellationToken, (client, output, stdout, stderr, cancellationToken) =>
                RunListAsync(
                    client,
                    parseResult.GetValue(entityIdOption),
                    parseResult.GetValue(entityTypeOption),
                    parseResult.GetValue(collectionIdOption),
                    parseResult.GetValue(countOption),
                    parseResult.GetValue(continuationTokenOption),
                    parseResult.GetValue(filterOption),
                    output, stdout, stderr,
                    cancellationToken)));

        return command.WithMetadata(new CommandMetadata(
            ApiPaths: [GetInventoryItemsPath],
            Scope: "Reads one collection of one entity's inventory.",
            Output: "A page of inventory items plus a continuation token for the next page.",
            Table: new TableView(
                nameof(GetInventoryItemsResponse.Items),
                [
                    new TableColumn("ID", nameof(InventoryItem.Id), 38),
                    new TableColumn("TYPE", nameof(InventoryItem.Type), 14),
                    new TableColumn("STACK", nameof(InventoryItem.StackId), 24),
                    new TableColumn("AMOUNT", nameof(InventoryItem.Amount), 10),
                ]),
            Examples: [
                "xbox playfab economy inventory list --entity-id TITLEPLAYER123 --entity-type title_player_account",
                "xbox playfab economy inventory list --entity-id TITLEPLAYER123 --entity-type title_player_account --limit 50",
            ]));
    }

    /// <summary>Action for <c>economy inventory list</c>: pages an entity's inventory items.</summary>
    internal static async Task<int> RunListAsync(
        PlayFabApiClient client, string? entityId, string? entityType, string? collectionId,
        int? count, string? continuationToken, string? filter,
        OutputOptions output, TextWriter stdout, TextWriter stderr,
        CancellationToken cancellationToken)
    {
        if (!TryBuildEntity(entityId, entityType, out var entity, out var entityError))
        {
            stderr.WriteLine(entityError);
            return 1;
        }

        if (!TryResolveCount(count, out var resolvedCount, out var countError))
        {
            stderr.WriteLine(countError);
            return 1;
        }

        var request = new GetInventoryItemsRequest
        {
            Entity = entity,
            CollectionId = Normalize(collectionId),
            Count = resolvedCount,
            ContinuationToken = Normalize(continuationToken),
            Filter = Normalize(filter),
        };

        return await RunAsync(client, GetInventoryItemsPath, request, output, stdout, stderr, cancellationToken: cancellationToken);
    }

    #endregion

    #region collections

    private static Command CreateCollectionsCommand()
    {
        var entityIdOption = EntityIdOption();
        var entityTypeOption = EntityTypeOption();
        var countOption = CountOption();
        var continuationTokenOption = ContinuationTokenOption();

        var command = new Command("list", "List an entity's inventory collection ids (GetInventoryCollectionIds).")
        {
            entityIdOption,
            entityTypeOption,
            countOption,
            continuationTokenOption,
        };

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            WithClientAsync(parseResult, cancellationToken, (client, output, stdout, stderr, cancellationToken) =>
                RunCollectionsAsync(
                    client,
                    parseResult.GetValue(entityIdOption),
                    parseResult.GetValue(entityTypeOption),
                    parseResult.GetValue(countOption),
                    parseResult.GetValue(continuationTokenOption),
                    output, stdout, stderr,
                    cancellationToken)));

        return command.WithMetadata(new CommandMetadata(
            ApiPaths: [GetInventoryCollectionIdsPath],
            Scope: "Reads the ids of every inventory collection the entity owns.",
            Output: "A page of collection ids plus a continuation token for the next page.",
            // A collection of bare strings: the column reads the element itself.
            Table: new TableView(
                nameof(GetInventoryCollectionIdsResponse.CollectionIds),
                [
                    new TableColumn("COLLECTION ID", string.Empty, 60),
                ]),
            Examples: [
                "xbox playfab economy inventory collection list --entity-id TITLEPLAYER123 --entity-type title_player_account",
            ]));
    }

    /// <summary>Action for <c>economy inventory collection list</c>: pages an entity's collection ids.</summary>
    internal static async Task<int> RunCollectionsAsync(
        PlayFabApiClient client, string? entityId, string? entityType,
        int? count, string? continuationToken,
        OutputOptions output, TextWriter stdout, TextWriter stderr,
        CancellationToken cancellationToken)
    {
        if (!TryBuildEntity(entityId, entityType, out var entity, out var entityError))
        {
            stderr.WriteLine(entityError);
            return 1;
        }

        if (!TryResolveCount(count, out var resolvedCount, out var countError))
        {
            stderr.WriteLine(countError);
            return 1;
        }

        var request = new GetInventoryCollectionIdsRequest
        {
            Entity = entity,
            Count = resolvedCount,
            ContinuationToken = Normalize(continuationToken),
        };

        return await RunAsync(client, GetInventoryCollectionIdsPath, request, output, stdout, stderr, cancellationToken: cancellationToken);
    }

    #endregion

    #region history

    private static Command CreateHistoryCommand()
    {
        var entityIdOption = EntityIdOption();
        var entityTypeOption = EntityTypeOption();
        var collectionIdOption = CollectionIdOption();
        var countOption = CountOption();
        var continuationTokenOption = ContinuationTokenOption();
        var filterOption = OptionalOption("--filter", "Optional OData filter over 'timestamp', 'transactionid', 'apiname', and 'operationtype' (e.g. \"apiname eq 'AddInventoryItems'\").");
        var orderByOption = OptionalOption("--order-by", "Optional OData orderby: 'timestamp asc' (default) or 'timestamp desc'.");

        var command = new Command("list", "List an entity's inventory transaction history (GetTransactionHistory).")
        {
            entityIdOption,
            entityTypeOption,
            collectionIdOption,
            countOption,
            continuationTokenOption,
            filterOption,
            orderByOption,
        };

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            WithClientAsync(parseResult, cancellationToken, (client, output, stdout, stderr, cancellationToken) =>
                RunHistoryAsync(
                    client,
                    parseResult.GetValue(entityIdOption),
                    parseResult.GetValue(entityTypeOption),
                    parseResult.GetValue(collectionIdOption),
                    parseResult.GetValue(countOption),
                    parseResult.GetValue(continuationTokenOption),
                    parseResult.GetValue(filterOption),
                    parseResult.GetValue(orderByOption),
                    output, stdout, stderr,
                    cancellationToken)));

        return command.WithMetadata(new CommandMetadata(
            ApiPaths: [GetTransactionHistoryPath],
            Scope: "Reads the entity's inventory transaction history.",
            Output: "A page of transactions plus a continuation token for the next page.",
            Table: new TableView(
                nameof(GetTransactionHistoryResponse.Transactions),
                [
                    new TableColumn("TRANSACTION ID", nameof(Transaction.TransactionId), 38),
                    new TableColumn("TIMESTAMP", nameof(Transaction.Timestamp), 24),
                    new TableColumn("API", nameof(Transaction.ApiName), 28),
                    new TableColumn("OPERATION", nameof(Transaction.OperationType), 16),
                ]),
            Examples: [
                "xbox playfab economy inventory transaction list --entity-id TITLEPLAYER123 --entity-type title_player_account",
            ]));
    }

    /// <summary>Action for <c>economy inventory transaction list</c>: pages an entity's transaction history.</summary>
    internal static async Task<int> RunHistoryAsync(
        PlayFabApiClient client, string? entityId, string? entityType, string? collectionId,
        int? count, string? continuationToken, string? filter, string? orderBy,
        OutputOptions output, TextWriter stdout, TextWriter stderr,
        CancellationToken cancellationToken)
    {
        if (!TryBuildEntity(entityId, entityType, out var entity, out var entityError))
        {
            stderr.WriteLine(entityError);
            return 1;
        }

        if (!TryResolveCount(count, out var resolvedCount, out var countError))
        {
            stderr.WriteLine(countError);
            return 1;
        }

        var request = new GetTransactionHistoryRequest
        {
            Entity = entity,
            // GetTransactionHistory rejects a request with no CollectionId: "CollectionId is required."
            CollectionId = Normalize(collectionId) ?? DefaultCollectionId,
            Count = resolvedCount,
            ContinuationToken = Normalize(continuationToken),
            Filter = Normalize(filter),
            OrderBy = Normalize(orderBy),
        };

        return await RunAsync(client, GetTransactionHistoryPath, request, output, stdout, stderr, cancellationToken: cancellationToken);
    }

    #endregion

    #region add

    private static Command CreateAddCommand()
    {
        var entityIdOption = EntityIdOption();
        var entityTypeOption = EntityTypeOption();
        var collectionIdOption = CollectionIdOption();
        var itemIdOption = OptionalOption("--item-id", "The catalog item id to add. Mutually exclusive with --item.");
        var itemOption = OptionalOption("--item", "A full InventoryItemReference as JSON (e.g. '{\"Id\":\"abc\",\"StackId\":\"default\"}'). Mutually exclusive with --item-id/--stack-id.");
        var stackIdOption = OptionalOption("--stack-id", "Optional stack id for the item (used with --item-id; default 'default').");
        var amountOption = new Option<double?>("--amount") { Description = "The amount to add for the item." };
        var idempotencyIdOption = OptionalOption("--idempotency-id", "Optional idempotency id to guard against duplicate application of this request.");

        var command = new Command("add", "Add an item (or currency amount) to an entity's inventory (AddInventoryItems).")
        {
            entityIdOption,
            entityTypeOption,
            collectionIdOption,
            itemIdOption,
            itemOption,
            stackIdOption,
            amountOption,
            idempotencyIdOption,
        };

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            WithClientAsync(parseResult, cancellationToken, (client, output, stdout, stderr, cancellationToken) =>
                RunAddAsync(
                    client,
                    parseResult.GetValue(entityIdOption),
                    parseResult.GetValue(entityTypeOption),
                    parseResult.GetValue(collectionIdOption),
                    parseResult.GetValue(itemIdOption),
                    parseResult.GetValue(itemOption),
                    parseResult.GetValue(stackIdOption),
                    parseResult.GetValue(amountOption),
                    parseResult.GetValue(idempotencyIdOption),
                    output, stdout, stderr,
                    cancellationToken)));

        return command.WithMetadata(new CommandMetadata(
            ApiPaths: [AddInventoryItemsPath],
            Scope: "Adds an item to one collection of one entity's inventory.",
            Output: "An acknowledgement with 'ETag', 'IdempotencyId', and 'TransactionIds', the transactions " +
                "the write recorded. It doesn't include the changed inventory or an operation token. " +
                "Run 'inventory list' to see the inventory, or 'inventory transaction list' to see the transactions.",
            Examples: [
                "xbox playfab economy inventory add --entity-id TITLEPLAYER123 --entity-type title_player_account --item-id 11111111-2222-3333-4444-555555555555 --amount 1",
            ]));
    }

    /// <summary>
    /// Action for <c>economy inventory add</c>: adds an item reference (built from <c>--item-id</c>
    /// [+ <c>--stack-id</c>] or supplied whole via <c>--item</c> JSON) and an optional
    /// <c>--amount</c>. The two item forms are mutually exclusive and exactly one is required.
    /// </summary>
    internal static async Task<int> RunAddAsync(
        PlayFabApiClient client, string? entityId, string? entityType, string? collectionId,
        string? itemId, string? itemJson, string? stackId, double? amount, string? idempotencyId,
        OutputOptions output, TextWriter stdout, TextWriter stderr,
        CancellationToken cancellationToken)
    {
        if (!TryBuildEntity(entityId, entityType, out var entity, out var entityError))
        {
            stderr.WriteLine(entityError);
            return 1;
        }

        if (!TryBuildItemReference(itemId, itemJson, stackId, out var item, out var itemError))
        {
            stderr.WriteLine(itemError);
            return 1;
        }

        var request = new AddInventoryItemsRequest
        {
            Entity = entity,
            CollectionId = Normalize(collectionId),
            Item = item,
            Amount = amount,
            IdempotencyId = Normalize(idempotencyId),
        };

        return await RunAsync(client, AddInventoryItemsPath, request, output, stdout, stderr, cancellationToken: cancellationToken);
    }

    #endregion

    #region ops

    private static Command CreateOpsCommand()
    {
        var entityIdOption = EntityIdOption();
        var entityTypeOption = EntityTypeOption();
        var collectionIdOption = CollectionIdOption();
        var operationsOption = OptionalOption("--operations", "A JSON array of inventory operations to run transactionally. Mutually exclusive with --operations-file; exactly one is required.");
        var operationsFileOption = OptionalOption("--operations-file", "Path to a file containing the JSON array of inventory operations. Mutually exclusive with --operations; exactly one is required.");
        var idempotencyIdOption = OptionalOption("--idempotency-id", "Optional idempotency id to guard against duplicate application of this batch.");

        var command = new Command("execute", "Execute a batch of inventory operations transactionally (ExecuteInventoryOperations).")
        {
            entityIdOption,
            entityTypeOption,
            collectionIdOption,
            operationsOption,
            operationsFileOption,
            idempotencyIdOption,
        };

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            WithClientAsync(parseResult, cancellationToken, (client, output, stdout, stderr, cancellationToken) =>
                RunOpsAsync(
                    client,
                    parseResult.GetValue(entityIdOption),
                    parseResult.GetValue(entityTypeOption),
                    parseResult.GetValue(collectionIdOption),
                    parseResult.GetValue(operationsOption),
                    parseResult.GetValue(operationsFileOption),
                    parseResult.GetValue(idempotencyIdOption),
                    output, stdout, stderr,
                    cancellationToken,
                    parseResult.GetValue(Program.ConfirmOption))));

        return command.WithMetadata(new CommandMetadata(
            ApiPaths: [ExecuteInventoryOperationsPath],
            Scope: "Applies a batch of operations transactionally to one collection.",
            Output: "An acknowledgement with 'ETag', 'IdempotencyId', and 'TransactionIds', the transactions " +
                "the batch recorded. It doesn't include per-operation results, the changed inventory, or an " +
                "operation token. Run 'inventory list' to see the inventory, or 'inventory transaction list' " +
                "to see the transactions.",
            Examples: [
                "xbox playfab economy inventory operation execute --entity-id TITLEPLAYER123 --entity-type title_player_account --operations-file ops.json",
            ],
            SafetyDetails: "Batches containing Delete operations require --confirm."));
    }

    /// <summary>
    /// Action for <c>economy inventory operation execute</c>: runs a transactional batch of operations, read
    /// either inline via <c>--operations</c> or from <c>--operations-file</c> (exactly one).
    /// </summary>
    internal static async Task<int> RunOpsAsync(
        PlayFabApiClient client, string? entityId, string? entityType, string? collectionId,
        string? operationsJson, string? operationsFile, string? idempotencyId,
        OutputOptions output, TextWriter stdout, TextWriter stderr,
        CancellationToken cancellationToken,
        bool confirmed = false)
    {
        if (!TryBuildEntity(entityId, entityType, out var entity, out var entityError))
        {
            stderr.WriteLine(entityError);
            return 1;
        }

        var hasInline = !string.IsNullOrWhiteSpace(operationsJson);
        var hasFile = !string.IsNullOrWhiteSpace(operationsFile);

        if (hasInline == hasFile)
        {
            stderr.WriteLine("Provide exactly one of --operations or --operations-file.");
            return 1;
        }

        string operationsPayload;
        if (hasFile)
        {
            try
            {
                operationsPayload = await File.ReadAllTextAsync(operationsFile!.Trim(), cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                stderr.WriteLine($"Could not read --operations-file '{operationsFile}': {ex.Message}");
                return 1;
            }
        }
        else
        {
            operationsPayload = operationsJson!;
        }

        if (!TryParseOperations(operationsPayload, out var operations, out var opsError))
        {
            stderr.WriteLine(opsError);
            return 1;
        }

        // An operations batch can contain Delete operations, which permanently remove item stacks.
        // The name-based guard in PlayFabApiClient only fires on Delete*/Shutdown* *paths*, and
        // this path is ExecuteInventoryOperations, so it would not catch them. Re-apply the
        // confirmation requirement here so deleting through a batch is no easier than deleting
        // through a dedicated verb.
        if (operations!.Any(operation => operation.Delete != null) && !confirmed)
        {
            stderr.WriteLine(
                "This operations batch contains a Delete operation, which permanently removes item stacks. " +
                "Re-run with --confirm to proceed.");
            return 1;
        }

        var request = new ExecuteInventoryOperationsRequest
        {
            Entity = entity,
            CollectionId = Normalize(collectionId),
            Operations = operations,
            IdempotencyId = Normalize(idempotencyId),
        };

        return await RunAsync(client, ExecuteInventoryOperationsPath, request, output, stdout, stderr, cancellationToken: cancellationToken);
    }

    #endregion

    #region status

    private static Command CreateStatusCommand()
    {
        var entityIdOption = OptionalOption("--entity-id", "The id of the entity that gave the items: the transfer's 'GivingEntity'. Requires --entity-type.");
        var entityTypeOption = EntityTypeOption();
        var collectionIdOption = CollectionIdOption();
        var operationTokenOption = RequiredOption("--operation-token", "The 'OperationToken' from a TransferInventoryItems or ExecuteTransferOperations response whose 'OperationStatus' is InProgress.");

        var command = new Command("status", "Get the status of an inventory transfer from its operation token (GetInventoryOperationStatus).")
        {
            entityIdOption,
            entityTypeOption,
            collectionIdOption,
            operationTokenOption,
        };

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            WithClientAsync(parseResult, cancellationToken, (client, output, stdout, stderr, cancellationToken) =>
                RunStatusAsync(
                    client,
                    parseResult.GetValue(entityIdOption),
                    parseResult.GetValue(entityTypeOption),
                    parseResult.GetValue(collectionIdOption),
                    parseResult.GetValue(operationTokenOption)!,
                    output, stdout, stderr,
                    cancellationToken)));

        return command.WithMetadata(new CommandMetadata(
            ApiPaths: [GetInventoryOperationStatusPath],
            Scope: "Reads the status of an inventory transfer. Only TransferInventoryItems and " +
                "ExecuteTransferOperations return an operation token, and only while the transfer is in " +
                "progress. The CLI has no verb for either; 'playfab api' reaches them. 'inventory add' " +
                "and 'inventory operation execute' don't return one.",
            Output: "The operation's current status.",
            Table: TableView.Record(
                [
                    new TableColumn("STATUS", nameof(GetInventoryOperationStatusResponse.OperationStatus), 32),
                ]),
            Examples: [
                "xbox playfab economy inventory operation status --entity-id TITLEPLAYER123 --entity-type title_player_account --operation-token OPERATIONTOKEN123",
            ]));
    }

    /// <summary>Action for <c>economy inventory operation status</c>: reports an async operation's status.</summary>
    internal static async Task<int> RunStatusAsync(
        PlayFabApiClient client, string? entityId, string? entityType, string? collectionId,
        string operationToken,
        OutputOptions output, TextWriter stdout, TextWriter stderr,
        CancellationToken cancellationToken)
    {
        if (!TryBuildEntity(entityId, entityType, out var entity, out var entityError))
        {
            stderr.WriteLine(entityError);
            return 1;
        }

        if (string.IsNullOrWhiteSpace(operationToken))
        {
            stderr.WriteLine("--operation-token must not be blank.");
            return 1;
        }

        var request = new GetInventoryOperationStatusRequest
        {
            Entity = entity,
            CollectionId = Normalize(collectionId),
            OperationToken = operationToken.Trim(),
        };

        return await RunAsync(client, GetInventoryOperationStatusPath, request, output, stdout, stderr, cancellationToken: cancellationToken);
    }

    #endregion

    #region Build / parse helpers

    /// <summary>
    /// Builds the target <see cref="EntityKey"/> from <paramref name="entityId"/>/
    /// <paramref name="entityType"/>. Both must be supplied together or both omitted (in which case
    /// <paramref name="entity"/> is <c>null</c> and the request defers to the calling entity).
    /// Returns <c>false</c> with an <paramref name="error"/> when only one is provided.
    /// </summary>
    internal static bool TryBuildEntity(
        string? entityId, string? entityType, out EntityKey? entity, out string? error)
    {
        entity = null;
        error = null;

        var id = Normalize(entityId);
        var type = Normalize(entityType);

        if (id is null && type is null)
        {
            return true;
        }

        if (id is null || type is null)
        {
            error = "Both --entity-id and --entity-type are required when specifying an entity (or omit both to target the calling entity).";
            return false;
        }

        entity = new EntityKey { Id = id, Type = type };
        return true;
    }

    /// <summary>
    /// Resolves the effective page size: absent uses <see cref="DefaultCount"/>; a supplied value
    /// must be positive. Returns <c>false</c> with an <paramref name="error"/> for a non-positive
    /// <paramref name="count"/>.
    /// </summary>
    internal static bool TryResolveCount(int? count, out double resolved, out string? error)
    {
        error = null;

        if (!count.HasValue)
        {
            resolved = DefaultCount;
            return true;
        }

        if (count.Value <= 0)
        {
            resolved = 0;
            error = "--limit must be a positive integer.";
            return false;
        }

        resolved = count.Value;
        return true;
    }

    /// <summary>
    /// Builds the <see cref="InventoryItemReference"/> for <c>add</c> from either <c>--item-id</c>
    /// (+ optional <c>--stack-id</c>) or a whole-object <c>--item</c> JSON escape hatch. The two
    /// forms are mutually exclusive and exactly one must be supplied; <c>--stack-id</c> only applies
    /// to <c>--item-id</c>. Malformed <c>--item</c> JSON yields a clean error, never an exception.
    /// </summary>
    internal static bool TryBuildItemReference(
        string? itemId, string? itemJson, string? stackId, out InventoryItemReference? item, out string? error)
    {
        item = null;
        error = null;

        var id = Normalize(itemId);
        var json = Normalize(itemJson);
        var stack = Normalize(stackId);

        if (id is not null && json is not null)
        {
            error = "Use --item-id or --item, not both.";
            return false;
        }

        if (id is null && json is null)
        {
            error = "Provide the item to add via --item-id or --item.";
            return false;
        }

        if (json is not null)
        {
            if (stack is not null)
            {
                error = "--stack-id is only valid with --item-id; include StackId inside --item instead.";
                return false;
            }

            InventoryItemReference? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<InventoryItemReference>(json, DeserializeOptions);
            }
            catch (JsonException ex)
            {
                error = $"Invalid JSON for --item: {ex.Message}";
                return false;
            }

            if (parsed is null)
            {
                error = "Invalid JSON for --item: expected an InventoryItemReference object.";
                return false;
            }

            item = parsed;
            return true;
        }

        item = new InventoryItemReference { Id = id, StackId = stack };
        return true;
    }

    /// <summary>
    /// Parses the <c>--operations</c>/<c>--operations-file</c> payload into a list of
    /// <see cref="InventoryOperation"/>. Malformed JSON yields a clean error (never an exception),
    /// and an empty array is rejected.
    /// </summary>
    internal static bool TryParseOperations(
        string? json, out List<InventoryOperation>? operations, out string? error)
    {
        operations = null;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "--operations must contain a JSON array of at least one operation.";
            return false;
        }

        List<InventoryOperation>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<List<InventoryOperation>>(json, DeserializeOptions);
        }
        catch (JsonException ex)
        {
            error = $"Invalid JSON for --operations: {ex.Message}";
            return false;
        }

        if (parsed is null || parsed.Count == 0)
        {
            error = "--operations must contain a JSON array of at least one operation.";
            return false;
        }

        operations = parsed;
        return true;
    }

    /// <summary>Trims a string option value, returning <c>null</c> when it is absent or blank.</summary>
    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    #endregion
}
