// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;
using Moq;
using System.CommandLine;
using System.Net;
using System.Text.Json;
using XboxDevCLI.PlayFab.Commands;
using XboxDevCLI.Services;
using XboxDevCLI.Output;
using Xunit;

namespace XboxDevCLI.UnitTest;

/// <summary>
/// Coverage for the <c>playfab economy inventory</c> command group: command/option structure, the
/// entity/count/item/operations build helpers and their validation branches, the exact request URI
/// and serialized body each verb produces, success and API-error output paths, and the
/// malformed-JSON handling for the <c>--item</c>/<c>--operations</c> escape hatches. Actions take an
/// injected client and captured writers, so no <c>Console</c> redirection (and no
/// <c>[Collection("ConsoleRedirect")]</c>) is needed.
/// </summary>
public class EconomyInventoryCommandTests
{
    #region Command structure

    [Fact]
    public void Economy_HasInventoryGroupWithAllVerbs()
    {
        var economy = EconomyCommandFactory.Create();
        Assert.Equal("economy", economy.Name);

        var inventory = economy.Subcommands.First(c => c.Name == "inventory");
        Assert.Equal(
            ["add", "collection", "list", "operation", "transaction"],
            inventory.Subcommands.Select(c => c.Name).Order(StringComparer.Ordinal));

        Assert.Equal(
            ["list"],
            inventory.Subcommands.First(c => c.Name == "collection").Subcommands.Select(c => c.Name));
        Assert.Equal(
            ["list"],
            inventory.Subcommands.First(c => c.Name == "transaction").Subcommands.Select(c => c.Name));
        Assert.Equal(
            ["execute", "status"],
            inventory.Subcommands.First(c => c.Name == "operation")
                .Subcommands.Select(c => c.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Inventory_Verbs_HaveExpectedOptions()
    {
        var inventory = EconomyCommandFactory.Create().Subcommands.First(c => c.Name == "inventory");

        Command Verb(string path)
        {
            var current = inventory;
            foreach (var segment in path.Split(' '))
            {
                current = current.Subcommands.First(c => c.Name == segment);
            }

            return current;
        }

        void AssertHas(Command c, params string[] names)
        {
            foreach (var n in names)
            {
                Assert.Contains(c.Options, o => o.Name == n);
            }
        }

        AssertHas(Verb("list"), "--entity-id", "--entity-type", "--collection-id", "--limit", "--continuation-token", "--filter");
        AssertHas(Verb("collection list"), "--entity-id", "--entity-type", "--limit", "--continuation-token");
        AssertHas(Verb("transaction list"), "--entity-id", "--entity-type", "--collection-id", "--limit", "--continuation-token", "--filter", "--order-by");
        AssertHas(Verb("add"), "--entity-id", "--entity-type", "--collection-id", "--item-id", "--item", "--stack-id", "--amount", "--idempotency-id");
        AssertHas(Verb("operation execute"), "--entity-id", "--entity-type", "--collection-id", "--operations", "--operations-file", "--idempotency-id");
        AssertHas(Verb("operation status"), "--entity-id", "--entity-type", "--collection-id", "--operation-token");

        Assert.True(Verb("operation status").Options.First(o => o.Name == "--operation-token").Required);

        // Paging vocabulary is normalized; the retired spelling must be gone everywhere.
        foreach (var paged in new[] { "list", "collection list", "transaction list" })
        {
            Assert.DoesNotContain(Verb(paged).Options, o => o.Name == "--count");
        }
    }

    #endregion

    #region TryBuildEntity

    [Fact]
    public void TryBuildEntity_BothProvided_BuildsKey()
    {
        Assert.True(EconomyCommandFactory.TryBuildEntity("E1", "title_player_account", out var entity, out var err));
        Assert.Null(err);
        Assert.Equal("E1", entity!.Id);
        Assert.Equal("title_player_account", entity.Type);
    }

    [Fact]
    public void TryBuildEntity_BothOmitted_ReturnsNullEntity()
    {
        Assert.True(EconomyCommandFactory.TryBuildEntity(null, "  ", out var entity, out var err));
        Assert.Null(entity);
        Assert.Null(err);
    }

    [Theory]
    [InlineData("E1", null)]
    [InlineData(null, "title_player_account")]
    public void TryBuildEntity_HalfProvided_ReturnsError(string? id, string? type)
    {
        Assert.False(EconomyCommandFactory.TryBuildEntity(id, type, out var entity, out var err));
        Assert.Null(entity);
        Assert.Contains("--entity-id", err);
        Assert.Contains("--entity-type", err);
    }

    #endregion

    #region TryResolveCount

    [Fact]
    public void TryResolveCount_Absent_UsesDefault()
    {
        Assert.True(EconomyCommandFactory.TryResolveCount(null, out var resolved, out var err));
        Assert.Equal(EconomyCommandFactory.DefaultCount, resolved);
        Assert.Null(err);
    }

    [Fact]
    public void TryResolveCount_Positive_UsesValue()
    {
        Assert.True(EconomyCommandFactory.TryResolveCount(25, out var resolved, out _));
        Assert.Equal(25, resolved);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void TryResolveCount_NonPositive_ReturnsError(int count)
    {
        Assert.False(EconomyCommandFactory.TryResolveCount(count, out _, out var err));
        Assert.Contains("--limit", err);
    }

    #endregion

    #region TryBuildItemReference

    [Fact]
    public void TryBuildItemReference_ItemId_BuildsReference()
    {
        Assert.True(EconomyCommandFactory.TryBuildItemReference("sword", null, "stack7", out var item, out var err));
        Assert.Null(err);
        Assert.Equal("sword", item!.Id);
        Assert.Equal("stack7", item.StackId);
    }

    [Fact]
    public void TryBuildItemReference_Json_Deserializes_CaseInsensitive()
    {
        Assert.True(EconomyCommandFactory.TryBuildItemReference(null, """{"id":"abc","stackId":"s1"}""", null, out var item, out var err));
        Assert.Null(err);
        Assert.Equal("abc", item!.Id);
        Assert.Equal("s1", item.StackId);
    }

    [Fact]
    public void TryBuildItemReference_BothForms_ReturnsError()
    {
        Assert.False(EconomyCommandFactory.TryBuildItemReference("sword", """{"Id":"abc"}""", null, out var item, out var err));
        Assert.Null(item);
        Assert.Contains("not both", err);
    }

    [Fact]
    public void TryBuildItemReference_Neither_ReturnsError()
    {
        Assert.False(EconomyCommandFactory.TryBuildItemReference(null, null, null, out var item, out var err));
        Assert.Null(item);
        Assert.Contains("--item-id", err);
    }

    [Fact]
    public void TryBuildItemReference_StackIdWithJson_ReturnsError()
    {
        Assert.False(EconomyCommandFactory.TryBuildItemReference(null, """{"Id":"abc"}""", "s1", out var item, out var err));
        Assert.Null(item);
        Assert.Contains("--stack-id", err);
    }

    [Theory]
    [InlineData("{not json}")]
    [InlineData("[1,2]")]
    public void TryBuildItemReference_MalformedJson_ReturnsFriendlyError(string json)
    {
        Assert.False(EconomyCommandFactory.TryBuildItemReference(null, json, null, out var item, out var err));
        Assert.Null(item);
        Assert.Contains("--item", err);
    }

    #endregion

    #region TryParseOperations

    [Fact]
    public void TryParseOperations_ValidArray_Parses()
    {
        var json = """[{"Add":{"Item":{"Id":"sword"},"Amount":1}}]""";
        Assert.True(EconomyCommandFactory.TryParseOperations(json, out var ops, out var err));
        Assert.Null(err);
        Assert.Single(ops!);
        Assert.NotNull(ops![0].Add);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[]")]
    public void TryParseOperations_EmptyOrBlank_ReturnsError(string json)
    {
        Assert.False(EconomyCommandFactory.TryParseOperations(json, out var ops, out var err));
        Assert.Null(ops);
        Assert.Contains("--operations", err);
    }

    [Fact]
    public void TryParseOperations_MalformedJson_ReturnsFriendlyError()
    {
        Assert.False(EconomyCommandFactory.TryParseOperations("[{oops}]", out var ops, out var err));
        Assert.Null(ops);
        Assert.Contains("--operations", err);
    }

    #endregion

    #region list

    [Fact]
    public async Task RunListAsync_SendsRequestToCorrectPathAndBody()
    {
        var handler = new EconomyMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);

        var exit = await EconomyCommandFactory.RunListAsync(
            client, "E1", "title_player_account", "coll1", 25, "tok", "type eq 'currency'",
            OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains(EconomyCommandFactory.GetInventoryItemsPath, handler.LastRequestUri);

        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        var root = doc.RootElement;
        Assert.Equal("E1", root.GetProperty("Entity").GetProperty("Id").GetString());
        Assert.Equal("title_player_account", root.GetProperty("Entity").GetProperty("Type").GetString());
        Assert.Equal("coll1", root.GetProperty("CollectionId").GetString());
        Assert.Equal(25, root.GetProperty("Count").GetDouble());
        Assert.Equal("tok", root.GetProperty("ContinuationToken").GetString());
        Assert.Equal("type eq 'currency'", root.GetProperty("Filter").GetString());
    }

    [Fact]
    public async Task RunListAsync_OmitsEntityAndDefaultsCount()
    {
        var handler = new EconomyMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);

        var exit = await EconomyCommandFactory.RunListAsync(
            client, null, null, null, null, null, null,
            OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("Entity", out _));
        Assert.False(root.TryGetProperty("CollectionId", out _));
        Assert.False(root.TryGetProperty("Filter", out _));
        Assert.Equal(EconomyCommandFactory.DefaultCount, root.GetProperty("Count").GetDouble());
    }

    [Fact]
    public async Task RunListAsync_HalfEntity_ReturnsOneWithoutCalling()
    {
        var handler = new EconomyMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await EconomyCommandFactory.RunListAsync(
            client, "E1", null, null, null, null, null,
            OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
        Assert.Contains("--entity-type", stderr.ToString());
    }

    [Fact]
    public async Task RunListAsync_NonPositiveCount_ReturnsOneWithoutCalling()
    {
        var handler = new EconomyMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await EconomyCommandFactory.RunListAsync(
            client, null, null, null, 0, null, null,
            OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
        Assert.Contains("--limit", stderr.ToString());
    }

    [Fact]
    public async Task RunListAsync_ApiError_ReturnsOneAndWritesError()
    {
        var errorJson = """{"code":400,"status":"BadRequest","error":"InvalidParams","errorCode":1000,"errorMessage":"bad entity"}""";
        var handler = new EconomyMockHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(errorJson) });
        using var client = CreateClientWithHandler(handler);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await EconomyCommandFactory.RunListAsync(
            client, "E1", "title_player_account", null, null, null, null,
            OutputOptions.Json, stdout, stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.NotEqual(string.Empty, stderr.ToString());
    }

    [Fact]
    public async Task RunListAsync_DataOnly_StripsEnvelope()
    {
        var handler = new EconomyMockHandler(Ok("""{"code":200,"status":"OK","data":{"Items":[],"ContinuationToken":"c"}}"""));
        using var client = CreateClientWithHandler(handler);
        var stdout = new StringWriter();

        var exit = await EconomyCommandFactory.RunListAsync(
            client, null, null, null, null, null, null,
            OutputOptions.JsonDataOnly, stdout, new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        var output = stdout.ToString();
        Assert.Contains("ContinuationToken", output);
        Assert.DoesNotContain("status", output);
    }

    #endregion

    #region collections

    [Fact]
    public async Task RunCollectionsAsync_SendsRequestToCorrectPath()
    {
        var handler = new EconomyMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);

        var exit = await EconomyCommandFactory.RunCollectionsAsync(
            client, "E1", "title_player_account", 5, "tok",
            OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains(EconomyCommandFactory.GetInventoryCollectionIdsPath, handler.LastRequestUri);
        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        Assert.Equal(5, doc.RootElement.GetProperty("Count").GetDouble());
        Assert.Equal("tok", doc.RootElement.GetProperty("ContinuationToken").GetString());
    }

    #endregion

    #region history

    [Fact]
    public async Task RunHistoryAsync_SendsFilterAndOrderBy()
    {
        var handler = new EconomyMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);

        var exit = await EconomyCommandFactory.RunHistoryAsync(
            client, "E1", "title_player_account", "coll1", 15, null,
            "apiname eq 'AddInventoryItems'", "timestamp desc",
            OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains(EconomyCommandFactory.GetTransactionHistoryPath, handler.LastRequestUri);
        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        var root = doc.RootElement;
        Assert.Equal("apiname eq 'AddInventoryItems'", root.GetProperty("Filter").GetString());
        Assert.Equal("timestamp desc", root.GetProperty("OrderBy").GetString());
        Assert.Equal("coll1", root.GetProperty("CollectionId").GetString());
    }

    [Fact]
    public async Task RunHistoryAsync_WithoutCollectionId_SendsDefaultCollection()
    {
        var handler = new EconomyMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);

        var exit = await EconomyCommandFactory.RunHistoryAsync(
            client, "E1", "title_player_account", null, null, null, null, null,
            OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        Assert.Equal(
            EconomyCommandFactory.DefaultCollectionId,
            doc.RootElement.GetProperty("CollectionId").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RunHistoryAsync_BlankCollectionId_SendsDefaultCollection(string collectionId)
    {
        var handler = new EconomyMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);

        var exit = await EconomyCommandFactory.RunHistoryAsync(
            client, "E1", "title_player_account", collectionId, null, null, null, null,
            OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        Assert.Equal(
            EconomyCommandFactory.DefaultCollectionId,
            doc.RootElement.GetProperty("CollectionId").GetString());
    }

    #endregion

    #region add

    [Fact]
    public async Task RunAddAsync_ItemIdAndAmount_SendsItemReference()
    {
        var handler = new EconomyMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);

        var exit = await EconomyCommandFactory.RunAddAsync(
            client, "E1", "title_player_account", "coll1", "sword", null, "stack7", 3, "idem1",
            OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains(EconomyCommandFactory.AddInventoryItemsPath, handler.LastRequestUri);
        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        var root = doc.RootElement;
        Assert.Equal("sword", root.GetProperty("Item").GetProperty("Id").GetString());
        Assert.Equal("stack7", root.GetProperty("Item").GetProperty("StackId").GetString());
        Assert.Equal(3, root.GetProperty("Amount").GetDouble());
        Assert.Equal("idem1", root.GetProperty("IdempotencyId").GetString());
        Assert.Equal("coll1", root.GetProperty("CollectionId").GetString());
    }

    [Fact]
    public async Task RunAddAsync_ItemJson_SendsParsedReference()
    {
        var handler = new EconomyMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);

        var exit = await EconomyCommandFactory.RunAddAsync(
            client, null, null, null, null, """{"Id":"potion","StackId":"s1"}""", null, null, null,
            OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        Assert.Equal("potion", doc.RootElement.GetProperty("Item").GetProperty("Id").GetString());
        Assert.False(doc.RootElement.TryGetProperty("Amount", out _));
    }

    [Fact]
    public async Task RunAddAsync_MalformedItemJson_ReturnsOneWithoutCalling()
    {
        var handler = new EconomyMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await EconomyCommandFactory.RunAddAsync(
            client, null, null, null, null, "{bad json", null, null, null,
            OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
        Assert.Contains("--item", stderr.ToString());
    }

    [Fact]
    public async Task RunAddAsync_NoItem_ReturnsOneWithoutCalling()
    {
        var handler = new EconomyMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await EconomyCommandFactory.RunAddAsync(
            client, null, null, null, null, null, null, 1, null,
            OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
        Assert.Contains("--item-id", stderr.ToString());
    }

    #endregion

    #region ops

    [Fact]
    public async Task RunOpsAsync_InlineOperations_SendsBatch()
    {
        var handler = new EconomyMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);
        var json = """[{"Add":{"Item":{"Id":"sword"},"Amount":1}},{"Subtract":{"Item":{"Id":"gold"},"Amount":5}}]""";

        var exit = await EconomyCommandFactory.RunOpsAsync(
            client, "E1", "title_player_account", "coll1", json, null, "idem2",
            OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains(EconomyCommandFactory.ExecuteInventoryOperationsPath, handler.LastRequestUri);
        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        var ops = doc.RootElement.GetProperty("Operations");
        Assert.Equal(2, ops.GetArrayLength());
        Assert.Equal("sword", ops[0].GetProperty("Add").GetProperty("Item").GetProperty("Id").GetString());
        Assert.Equal("idem2", doc.RootElement.GetProperty("IdempotencyId").GetString());
    }

    [Fact]
    public async Task RunOpsAsync_OperationsFile_ReadsAndSendsBatch()
    {
        var handler = new EconomyMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);

        // Write to the process working directory and delete it in the finally below, so no file
        // is left behind wherever the test host happens to run.
        var path = Path.Combine(Directory.GetCurrentDirectory(), $"ops-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """[{"Add":{"Item":{"Id":"sword"},"Amount":1}}]""");
        try
        {
            var exit = await EconomyCommandFactory.RunOpsAsync(
                client, null, null, null, null, path, null,
                OutputOptions.Json, new StringWriter(), new StringWriter(),
                CancellationToken.None);

            Assert.Equal(0, exit);
            using var doc = JsonDocument.Parse(handler.CapturedBody!);
            Assert.Equal(1, doc.RootElement.GetProperty("Operations").GetArrayLength());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RunOpsAsync_BothSources_ReturnsOneWithoutCalling()
    {
        var handler = new EconomyMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await EconomyCommandFactory.RunOpsAsync(
            client, null, null, null, "[{\"Add\":{}}]", "some-file.json", null,
            OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
        Assert.Contains("exactly one", stderr.ToString());
    }

    [Fact]
    public async Task RunOpsAsync_NeitherSource_ReturnsOneWithoutCalling()
    {
        var handler = new EconomyMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await EconomyCommandFactory.RunOpsAsync(
            client, null, null, null, null, null, null,
            OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
        Assert.Contains("exactly one", stderr.ToString());
    }

    [Fact]
    public async Task RunOpsAsync_MalformedInlineJson_ReturnsOneWithoutCalling()
    {
        var handler = new EconomyMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await EconomyCommandFactory.RunOpsAsync(
            client, null, null, null, "[{bad}]", null, null,
            OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
        Assert.Contains("--operations", stderr.ToString());
    }

    [Fact]
    public async Task RunOpsAsync_MissingFile_ReturnsOneWithoutCalling()
    {
        var handler = new EconomyMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();
        var missing = Path.Combine(Directory.GetCurrentDirectory(), $"missing-{Guid.NewGuid():N}.json");

        var exit = await EconomyCommandFactory.RunOpsAsync(
            client, null, null, null, null, missing, null,
            OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
        Assert.Contains("--operations-file", stderr.ToString());
    }

    [Fact]
    public async Task RunOpsAsync_DeleteOperationWithoutConfirm_IsRefusedBeforeCalling()
    {
        // ExecuteInventoryOperations is not a Delete*/Shutdown* path, so the name-based guard in
        // PlayFabApiClient never fires for it. A Delete carried inside the batch must still be
        // confirmed, otherwise deleting via a batch would be easier than via a dedicated verb.
        var handler = new EconomyMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();
        var json = """[{"Delete":{"Item":{"Id":"sword"}}}]""";

        var exit = await EconomyCommandFactory.RunOpsAsync(
            client, null, null, null, json, null, null,
            OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None, confirmed: false);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
        Assert.Contains("--confirm", stderr.ToString());
    }

    [Fact]
    public async Task RunOpsAsync_DeleteOperationWithConfirm_Proceeds()
    {
        var handler = new EconomyMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);
        var json = """[{"Delete":{"Item":{"Id":"sword"}}}]""";

        var exit = await EconomyCommandFactory.RunOpsAsync(
            client, null, null, null, json, null, null,
            OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None, confirmed: true);

        Assert.Equal(0, exit);
        Assert.Contains(EconomyCommandFactory.ExecuteInventoryOperationsPath, handler.LastRequestUri);
    }

    [Fact]
    public async Task RunOpsAsync_DeleteMixedIntoBatch_IsAlsoRefused()
    {
        // The guard must inspect every operation, not just the first one.
        var handler = new EconomyMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();
        var json = """[{"Add":{"Item":{"Id":"gold"},"Amount":1}},{"Delete":{"Item":{"Id":"sword"}}}]""";

        var exit = await EconomyCommandFactory.RunOpsAsync(
            client, null, null, null, json, null, null,
            OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None, confirmed: false);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
    }

    [Fact]
    public async Task RunOpsAsync_NonDeleteBatch_DoesNotRequireConfirm()
    {
        // Only Delete is gated; ordinary batches must not regress into needing --confirm.
        var handler = new EconomyMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);
        var json = """[{"Add":{"Item":{"Id":"sword"},"Amount":1}},{"Subtract":{"Item":{"Id":"gold"},"Amount":5}}]""";

        var exit = await EconomyCommandFactory.RunOpsAsync(
            client, null, null, null, json, null, null,
            OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None, confirmed: false);

        Assert.Equal(0, exit);
        Assert.Contains(EconomyCommandFactory.ExecuteInventoryOperationsPath, handler.LastRequestUri);
    }

    #endregion

    #region status

    [Fact]
    public async Task RunStatusAsync_SendsOperationToken()
    {
        var handler = new EconomyMockHandler(Ok("""{"code":200,"status":"OK","data":{"OperationStatus":"Completed"}}"""));
        using var client = CreateClientWithHandler(handler);
        var stdout = new StringWriter();

        var exit = await EconomyCommandFactory.RunStatusAsync(
            client, "E1", "title_player_account", "coll1", "op-token-123",
            OutputOptions.Json, stdout, new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains(EconomyCommandFactory.GetInventoryOperationStatusPath, handler.LastRequestUri);
        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        Assert.Equal("op-token-123", doc.RootElement.GetProperty("OperationToken").GetString());
        Assert.Equal("coll1", doc.RootElement.GetProperty("CollectionId").GetString());
        Assert.Contains("OperationStatus", stdout.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RunStatusAsync_WithoutCollectionId_OmitsCollectionId(string? collectionId)
    {
        var handler = new EconomyMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);

        var exit = await EconomyCommandFactory.RunStatusAsync(
            client, "E1", "title_player_account", collectionId, "op-token-123",
            OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        Assert.False(doc.RootElement.TryGetProperty("CollectionId", out _));
    }

    [Fact]
    public async Task RunStatusAsync_BlankToken_ReturnsOneWithoutCalling()
    {
        var handler = new EconomyMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await EconomyCommandFactory.RunStatusAsync(
            client, null, null, null, "   ",
            OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
        Assert.Contains("--operation-token", stderr.ToString());
    }

    #endregion

    #region Endpoint coverage

    [Fact]
    public async Task AllVerbs_ReachTheirDistinctEndpoints()
    {
        var paths = new List<string>();

        async Task Capture(Func<PlayFabApiClient, Task<int>> run)
        {
            var handler = new EconomyMockHandler(Ok());
            using var client = CreateClientWithHandler(handler);
            var exit = await run(client);
            Assert.Equal(0, exit);
            paths.Add(new Uri(handler.LastRequestUri!).AbsolutePath);
        }

        await Capture(c => EconomyCommandFactory.RunListAsync(c, null, null, null, null, null, null, OutputOptions.Json, new StringWriter(), new StringWriter(), CancellationToken.None));
        await Capture(c => EconomyCommandFactory.RunCollectionsAsync(c, null, null, null, null, OutputOptions.Json, new StringWriter(), new StringWriter(), CancellationToken.None));
        await Capture(c => EconomyCommandFactory.RunHistoryAsync(c, null, null, null, null, null, null, null, OutputOptions.Json, new StringWriter(), new StringWriter(), CancellationToken.None));
        await Capture(c => EconomyCommandFactory.RunAddAsync(c, null, null, null, "sword", null, null, 1, null, OutputOptions.Json, new StringWriter(), new StringWriter(), CancellationToken.None));
        await Capture(c => EconomyCommandFactory.RunOpsAsync(c, null, null, null, "[{\"Add\":{\"Item\":{\"Id\":\"x\"},\"Amount\":1}}]", null, null, OutputOptions.Json, new StringWriter(), new StringWriter(), CancellationToken.None));
        await Capture(c => EconomyCommandFactory.RunStatusAsync(c, null, null, null, "tok", OutputOptions.Json, new StringWriter(), new StringWriter(), CancellationToken.None));

        Assert.Equal(new[]
        {
            EconomyCommandFactory.GetInventoryItemsPath,
            EconomyCommandFactory.GetInventoryCollectionIdsPath,
            EconomyCommandFactory.GetTransactionHistoryPath,
            EconomyCommandFactory.AddInventoryItemsPath,
            EconomyCommandFactory.ExecuteInventoryOperationsPath,
            EconomyCommandFactory.GetInventoryOperationStatusPath,
        }, paths);
        Assert.Equal(6, paths.Distinct().Count());
    }

    #endregion

    #region Test helpers

    private static HttpResponseMessage Ok(string json = """{"code":200,"status":"OK","data":{"ok":true}}""") =>
        new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private static IConfiguration CreateTestConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PlayFab:TitleId"] = "TEST123",
                ["PlayFab:BaseUrlTemplate"] = "https://{0}.playfabapi.com",
                ["AzureAd:ClientId"] = "test-client-id",
                ["AzureAd:TenantId"] = "test-tenant-id",
                ["AzureAd:Scopes"] = "api://test/.default",
            })
            .Build();

    private static AuthenticationResult CreateMockAuthResult(string accessToken) =>
        new(
            accessToken: accessToken,
            isExtendedLifeTimeToken: false,
            uniqueId: "test-unique-id",
            expiresOn: DateTimeOffset.UtcNow.AddHours(1),
            extendedExpiresOn: DateTimeOffset.UtcNow.AddHours(1),
            tenantId: "test-tenant-id",
            account: Mock.Of<IAccount>(),
            idToken: "test-id-token",
            scopes: new[] { "api://test/.default" },
            correlationId: Guid.NewGuid());

    private static PlayFabApiClient CreateClientWithHandler(HttpMessageHandler handler)
    {
        var config = CreateTestConfiguration();
        var mockAuth = new Mock<AuthenticationService>(config) { CallBase = false };
        mockAuth.Setup(a => a.AcquireTokenSilentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(CreateMockAuthResult("token"));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://TEST123.playfabapi.com/") };
        return new PlayFabApiClient(config, mockAuth.Object, httpClient);
    }

    #endregion
}

/// <summary>
/// Mock handler that captures the outgoing request URI and body for assertion, then returns a
/// fixed response.
/// </summary>
internal sealed class EconomyMockHandler : HttpMessageHandler
{
    private readonly HttpResponseMessage _response;

    public EconomyMockHandler(HttpResponseMessage response) => _response = response;

    public string? CapturedBody { get; private set; }

    public string? LastRequestUri { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri?.ToString();
        if (request.Content != null)
        {
            CapturedBody = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        return _response;
    }
}
