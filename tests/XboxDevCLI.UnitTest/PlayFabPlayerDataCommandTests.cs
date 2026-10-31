// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;
using Moq;
using System.Net;
using System.Text.Json;
using XboxDevCLI.PlayFab.Commands;
using XboxDevCLI.PlayFab.Generated.Admin;
using XboxDevCLI.Services;
using XboxDevCLI.Output;
using Xunit;

namespace XboxDevCLI.UnitTest;

/// <summary>
/// Coverage for the <c>playfab player-data</c> command group: command structure, the
/// <c>--visibility</c>/<c>--publisher</c> endpoint resolution, the <c>--key</c>/<c>--value</c> /
/// <c>--data</c> parsing and mutual exclusion, single-key value extraction (<c>get --key --value-only</c>), the dedicated
/// <c>delete</c> verb, and the <c>Run*Async</c> action validation branches.
/// </summary>
[Collection("ConsoleRedirect")]
public class PlayFabPlayerDataCommandTests
{
    private static readonly JsonSerializerOptions IgnoreNulls = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    #region Command structure

    [Fact]
    public void PlayerData_HasGetSetDelete()
    {
        var data = PlayerDataCommandFactory.Create();

        Assert.Equal("player-data", data.Name);
        foreach (var name in new[] { "get", "set", "delete" })
        {
            Assert.Contains(data.Subcommands, c => c.Name == name);
        }
    }

    [Fact]
    public void PlayerData_Options()
    {
        var data = PlayerDataCommandFactory.Create();
        var get = data.Subcommands.First(c => c.Name == "get");
        var set = data.Subcommands.First(c => c.Name == "set");
        var delete = data.Subcommands.First(c => c.Name == "delete");

        foreach (var o in new[] { "--visibility", "--publisher", "--keys", "--key", "--value-only" })
        {
            Assert.Contains(get.Options, x => x.Name == o);
        }

        foreach (var o in new[] { "--visibility", "--publisher", "--key", "--value", "--data", "--permission" })
        {
            Assert.Contains(set.Options, x => x.Name == o);
        }

        Assert.Contains(delete.Options, x => x.Name == "--keys");
        Assert.True(delete.Options.First(o => o.Name == "--keys").Required);
    }

    #endregion

    #region Path resolution

    [Theory]
    [InlineData("client", false, "/Admin/GetUserData", "/Admin/UpdateUserData", false)]
    [InlineData("read-only", false, "/Admin/GetUserReadOnlyData", "/Admin/UpdateUserReadOnlyData", false)]
    [InlineData("internal", false, "/Admin/GetUserInternalData", "/Admin/UpdateUserInternalData", true)]
    [InlineData("client", true, "/Admin/GetUserPublisherData", "/Admin/UpdateUserPublisherData", false)]
    [InlineData("read-only", true, "/Admin/GetUserPublisherReadOnlyData", "/Admin/UpdateUserPublisherReadOnlyData", false)]
    [InlineData("internal", true, "/Admin/GetUserPublisherInternalData", "/Admin/UpdateUserPublisherInternalData", true)]
    public void TryResolvePaths_MapsVisibilityAndScope(string visibility, bool publisher, string getPath, string setPath, bool isInternal)
    {
        Assert.True(PlayerDataCommandFactory.TryResolvePaths(visibility, publisher, out var g, out var s, out var i, out var err));
        Assert.Equal(getPath, g);
        Assert.Equal(setPath, s);
        Assert.Equal(isInternal, i);
        Assert.Null(err);
    }

    [Fact]
    public void TryResolvePaths_DefaultsToClient()
    {
        Assert.True(PlayerDataCommandFactory.TryResolvePaths(null, false, out var g, out var s, out _, out _));
        Assert.Equal("/Admin/GetUserData", g);
        Assert.Equal("/Admin/UpdateUserData", s);
    }

    [Fact]
    public void TryResolvePaths_InvalidVisibility_ReturnsFalse()
    {
        Assert.False(PlayerDataCommandFactory.TryResolvePaths("bogus", false, out _, out _, out _, out var err));
        Assert.Contains("--visibility", err);
    }

    #endregion

    #region Parsing / extract helpers

    [Fact]
    public void TryExtractDataValue_Found_ReturnsValue()
    {
        var response = """{"code":200,"status":"OK","data":{"Data":{"level":{"Value":"5"}},"DataVersion":3}}""";
        Assert.True(PlayerDataCommandFactory.TryExtractDataValue(response, "level", out var value));
        Assert.Equal("5", value);
    }

    [Fact]
    public void TryExtractDataValue_MissingKey_ReturnsFalse()
    {
        var response = """{"code":200,"status":"OK","data":{"Data":{},"DataVersion":3}}""";
        Assert.False(PlayerDataCommandFactory.TryExtractDataValue(response, "level", out _));
    }

    [Fact]
    public void TryParseDataObject_StoresValuesAsStrings()
    {
        Assert.True(PlayerDataCommandFactory.TryParseDataObject(
            """{"s":"v","n":5,"f":1.5,"b":true,"obj":{"name":"Bob"},"arr":[1,2]}""", out var result, out var error));
        Assert.Null(error);
        Assert.Equal("v", result!["s"]);
        Assert.Equal("5", result["n"]);
        Assert.Equal("1.5", result["f"]);
        Assert.Equal("true", result["b"]);
        Assert.Equal("""{"name":"Bob"}""", result["obj"]);
        Assert.Equal("[1,2]", result["arr"]);
    }

    [Fact]
    public void TryParseDataObject_RejectsNullValue()
    {
        // A null value is ambiguous in a strings-only map — reject it (use delete to remove keys).
        Assert.False(PlayerDataCommandFactory.TryParseDataObject("""{"n":null}""", out var result, out var error));
        Assert.Null(result);
        Assert.Contains("--data", error);
    }

    [Theory]
    [InlineData("""["a","b"]""")]
    [InlineData("\"hi\"")]
    [InlineData("5")]
    [InlineData("true")]
    [InlineData("null")]
    public void TryParseDataObject_NonObjectJson_ReturnsFriendlyError(string json)
    {
        // Valid JSON that isn't an object must not leak the internal Dictionary type name.
        Assert.False(PlayerDataCommandFactory.TryParseDataObject(json, out var result, out var error));
        Assert.Null(result);
        Assert.Contains("expected a JSON object", error);
        Assert.DoesNotContain("Dictionary", error);
    }

    [Fact]
    public void TryParseDataObject_Blank_ReturnsTrueWithNull()
    {
        Assert.True(PlayerDataCommandFactory.TryParseDataObject("  ", out var result, out var error));
        Assert.Null(result);
        Assert.Null(error);
    }

    #endregion

    #region get actions

    [Fact]
    public async Task RunGetAsync_ValueOnly_WritesBareValue()
    {
        var handler = new PlayerDataMockHandler(Ok("""{"code":200,"status":"OK","data":{"Data":{"level":{"Value":"5"}},"DataVersion":3}}"""));
        using var client = CreateClientWithHandler(handler);
        var stdout = new StringWriter();

        var exit = await PlayerDataCommandFactory.RunGetAsync(
            client, "P1", "client", false, null, "level", valueOnly: true, null, OutputOptions.Json, stdout, new StringWriter(), cancellationToken: CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal("5", stdout.ToString().Trim());
        Assert.DoesNotContain("status", stdout.ToString());
        Assert.Contains("/Admin/GetUserData", handler.LastRequestUri);
    }

    [Fact]
    public async Task RunGetAsync_ValueOnly_MissingKey_ReturnsOne()
    {
        var handler = new PlayerDataMockHandler(Ok("""{"code":200,"status":"OK","data":{"Data":{},"DataVersion":3}}"""));
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await PlayerDataCommandFactory.RunGetAsync(
            client, "P1", "client", false, null, "missing", valueOnly: true, null, OutputOptions.Json, new StringWriter(), stderr, cancellationToken: CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("not found", stderr.ToString());
    }

    [Fact]
    public async Task RunGetAsync_ValueOnly_WithoutKey_ReturnsOneWithoutCalling()
    {
        var handler = new PlayerDataMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await PlayerDataCommandFactory.RunGetAsync(
            client, "P1", "client", false, null, null, valueOnly: true, null, OutputOptions.Json, new StringWriter(), stderr, cancellationToken: CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
        Assert.Contains("--value-only requires --key", stderr.ToString());
    }

    [Fact]
    public async Task RunGetAsync_KeyAndKeys_ReturnsOneWithoutCalling()
    {
        var handler = new PlayerDataMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await PlayerDataCommandFactory.RunGetAsync(
            client, "P1", "client", false, "a,b", "c", valueOnly: false, null, OutputOptions.Json, new StringWriter(), stderr, cancellationToken: CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
        Assert.Contains("not both", stderr.ToString());
    }

    [Fact]
    public async Task RunGetAsync_Normal_SendsToResolvedPath()
    {
        var handler = new PlayerDataMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);

        var exit = await PlayerDataCommandFactory.RunGetAsync(
            client, "P1", "read-only", true, "a,b", null, valueOnly: false, null, OutputOptions.Json, new StringWriter(), new StringWriter(), cancellationToken: CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("/Admin/GetUserPublisherReadOnlyData", handler.LastRequestUri);
    }

    [Fact]
    public async Task RunGetAsync_BlankKey_ValueOnly_ReturnsOneWithoutCalling()
    {
        var handler = new PlayerDataMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await PlayerDataCommandFactory.RunGetAsync(
            client, "P1", "client", false, null, "   ", valueOnly: true, null, OutputOptions.Json, new StringWriter(), stderr, cancellationToken: CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
        Assert.Contains("--value-only requires --key", stderr.ToString());
    }

    [Fact]
    public async Task RunGetAsync_BlankKey_TreatedAsAllKeys_NotSentAsEmptyKey()
    {
        var handler = new PlayerDataMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);

        var exit = await PlayerDataCommandFactory.RunGetAsync(
            client, "P1", "client", false, null, "", valueOnly: false, null, OutputOptions.Json, new StringWriter(), new StringWriter(), cancellationToken: CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("/Admin/GetUserData", handler.LastRequestUri);
        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        Assert.False(doc.RootElement.TryGetProperty("Keys", out _));
    }

    [Fact]
    public async Task RunGetAsync_Normal_PrettyPrintsResponseToStdout()
    {
        var handler = new PlayerDataMockHandler(Ok("""{"code":200,"status":"OK","data":{"Data":{"level":{"Value":"5"}},"DataVersion":3}}"""));
        using var client = CreateClientWithHandler(handler);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await PlayerDataCommandFactory.RunGetAsync(
            client, "P1", "client", false, "level", null, valueOnly: false, null, OutputOptions.Json, stdout, stderr, cancellationToken: CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("DataVersion", stdout.ToString());
        Assert.Contains("\n", stdout.ToString().TrimEnd()); // pretty-printed is multi-line
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public async Task RunGetAsync_JsonCompact_EmitsSingleLine()
    {
        var handler = new PlayerDataMockHandler(Ok("""{"code":200,"status":"OK","data":{"Data":{"level":{"Value":"5"}},"DataVersion":3}}"""));
        using var client = CreateClientWithHandler(handler);
        var stdout = new StringWriter();

        var exit = await PlayerDataCommandFactory.RunGetAsync(
            client, "P1", "client", false, "level", null, valueOnly: false, null, OutputOptions.JsonCompact, stdout, new StringWriter(), cancellationToken: CancellationToken.None);

        Assert.Equal(0, exit);
        var output = stdout.ToString().TrimEnd();
        Assert.DoesNotContain("\n", output);
        Assert.Contains("DataVersion", output);
    }

    [Fact]
    public async Task RunGetAsync_DataOnly_StripsEnvelope()
    {
        var handler = new PlayerDataMockHandler(Ok("""{"code":200,"status":"OK","data":{"Data":{"level":{"Value":"5"}},"DataVersion":3}}"""));
        using var client = CreateClientWithHandler(handler);
        var stdout = new StringWriter();

        var exit = await PlayerDataCommandFactory.RunGetAsync(
            client, "P1", "client", false, "level", null, valueOnly: false, null, OutputOptions.JsonDataOnly, stdout, new StringWriter(), cancellationToken: CancellationToken.None);

        Assert.Equal(0, exit);
        var output = stdout.ToString();
        Assert.Contains("DataVersion", output); // the data payload is present
        Assert.DoesNotContain("status", output); // the {code,status,data} envelope is stripped
    }

    [Fact]
    public async Task RunGetAsync_ApiError_ReturnsOneAndWritesError()
    {
        var errorJson = """{"code":400,"status":"BadRequest","error":"InvalidParams","errorCode":1000,"errorMessage":"bad player id"}""";
        var handler = new PlayerDataMockHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(errorJson) });
        using var client = CreateClientWithHandler(handler);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await PlayerDataCommandFactory.RunGetAsync(
            client, "P1", "client", false, "level", null, valueOnly: false, null, OutputOptions.Json, stdout, stderr, cancellationToken: CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.NotEqual(string.Empty, stderr.ToString());
    }

    [Fact]
    public async Task RunGetAsync_ValueOnly_ApiError_ReturnsOneAndWritesError()
    {
        var errorJson = """{"code":400,"status":"BadRequest","error":"InvalidParams","errorCode":1000,"errorMessage":"bad player id"}""";
        var handler = new PlayerDataMockHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(errorJson) });
        using var client = CreateClientWithHandler(handler);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await PlayerDataCommandFactory.RunGetAsync(
            client, "P1", "client", false, null, "level", valueOnly: true, null, OutputOptions.Json, stdout, stderr, cancellationToken: CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.NotEqual(string.Empty, stderr.ToString());
    }

    [Fact]
    public async Task RunGetAsync_ValueOnly_WithIfChanged_ReturnsOneWithoutCalling()
    {
        var handler = new PlayerDataMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await PlayerDataCommandFactory.RunGetAsync(
            client, "P1", "client", false, null, "level", valueOnly: true, 3, OutputOptions.Json, new StringWriter(), stderr, cancellationToken: CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
        Assert.Contains("--if-changed-from-version", stderr.ToString());
    }

    [Fact]
    public async Task RunGetAsync_Normal_WithIfChanged_SendsFilter()
    {
        var handler = new PlayerDataMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);

        var exit = await PlayerDataCommandFactory.RunGetAsync(
            client, "P1", "client", false, "level", null, valueOnly: false, 3, OutputOptions.Json, new StringWriter(), new StringWriter(), cancellationToken: CancellationToken.None);

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        Assert.Equal(3, doc.RootElement.GetProperty("IfChangedFromDataVersion").GetDouble());
    }

    #endregion

    #region set actions

    [Fact]
    public async Task RunSetAsync_KeyValue_SendsData()
    {
        var handler = new PlayerDataMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);

        // The value is stored verbatim, including '=' characters.
        var exit = await PlayerDataCommandFactory.RunSetAsync(
            client, "P1", "client", false, "level", "5=hp", null, "Public", OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("/Admin/UpdateUserData", handler.LastRequestUri);
        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        Assert.Equal("5=hp", doc.RootElement.GetProperty("Data").GetProperty("level").GetString());
        Assert.Equal("Public", doc.RootElement.GetProperty("Permission").GetString());
    }

    [Fact]
    public async Task RunSetAsync_DataJson_Sends()
    {
        var handler = new PlayerDataMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);

        var exit = await PlayerDataCommandFactory.RunSetAsync(
            client, "P1", "client", false, null, null, """{"k":"v"}""", "Private", OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        Assert.Equal("v", doc.RootElement.GetProperty("Data").GetProperty("k").GetString());
        Assert.Equal("Private", doc.RootElement.GetProperty("Permission").GetString());
    }

    // PlayFab makes every written key Private when the request has no Permission, so leaving
    // --permission out would quietly hide a Public key from other players.
    [Theory]
    [InlineData("client", false)]
    [InlineData("read-only", false)]
    [InlineData("client", true)]
    [InlineData("read-only", true)]
    [InlineData(null, false)]
    public async Task RunSetAsync_WithoutPermission_ReturnsOneWithoutCalling(string? visibility, bool publisher)
    {
        var handler = new PlayerDataMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await PlayerDataCommandFactory.RunSetAsync(
            client, "P1", visibility, publisher, "level", "5", null, null, OutputOptions.Json, stdout, stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Contains("--permission is required", stderr.ToString());
        Assert.Contains("--permission Private", stderr.ToString());
        Assert.Contains("--permission Public", stderr.ToString());
    }

    [Fact]
    public async Task RunSetAsync_BlankPermission_ReturnsOneWithoutCalling()
    {
        var handler = new PlayerDataMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await PlayerDataCommandFactory.RunSetAsync(
            client, "P1", "client", false, null, null, """{"k":"v"}""", "  ", OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
        Assert.Contains("--permission is required", stderr.ToString());
    }

    [Fact]
    public void SetHelp_SaysPermissionIsRequiredAndReplacesTheKeysPermission()
    {
        var root = Program.BuildRootCommand();
        var set = root.Subcommands.Single(c => c.Name == "playfab")
            .Subcommands.Single(c => c.Name == "player-data")
            .Subcommands.Single(c => c.Name == "set");
        var writer = new StringWriter();
        XboxDevCLI.CommandLine.CommandHelpWriter.Write(writer, root, set, "xbox", width: 400, useAnsi: false);
        var help = writer.ToString();

        Assert.DoesNotContain("Optional", help, StringComparison.Ordinal);
        Assert.Contains("Required, except with '--visibility internal'", help, StringComparison.Ordinal);
        Assert.Contains("replacing the one it had", help, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunSetAsync_Internal_SendsInternalRequestWithoutPermission()
    {
        var handler = new PlayerDataMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);

        var exit = await PlayerDataCommandFactory.RunSetAsync(
            client, "P1", "internal", false, "k", "v", null, null, OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("/Admin/UpdateUserInternalData", handler.LastRequestUri);
        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        Assert.False(doc.RootElement.TryGetProperty("Permission", out _));
    }

    [Fact]
    public async Task RunSetAsync_PermissionWithInternal_ReturnsOne()
    {
        var handler = new PlayerDataMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await PlayerDataCommandFactory.RunSetAsync(
            client, "P1", "internal", false, "k", "v", null, "Public", OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
        Assert.Contains("--permission", stderr.ToString());
    }

    [Fact]
    public async Task RunSetAsync_NothingToSet_ReturnsOne()
    {
        var handler = new PlayerDataMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await PlayerDataCommandFactory.RunSetAsync(
            client, "P1", "client", false, null, null, null, null, OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
        Assert.Contains("Nothing to set", stderr.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RunSetAsync_BlankKey_ReturnsOne(string blankKey)
    {
        var handler = new PlayerDataMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await PlayerDataCommandFactory.RunSetAsync(
            client, "P1", "client", false, blankKey, "v", null, null, OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
        Assert.Contains("--key must not be blank", stderr.ToString());
    }

    [Fact]
    public async Task RunSetAsync_KeyWithoutValue_ReturnsOne()
    {
        var handler = new PlayerDataMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await PlayerDataCommandFactory.RunSetAsync(
            client, "P1", "client", false, "k", null, null, null, OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
        Assert.Contains("--key and --value must be used together", stderr.ToString());
    }

    [Fact]
    public async Task RunSetAsync_ValueWithoutKey_ReturnsOne()
    {
        var handler = new PlayerDataMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await PlayerDataCommandFactory.RunSetAsync(
            client, "P1", "client", false, null, "v", null, null, OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
        Assert.Contains("--key and --value must be used together", stderr.ToString());
    }

    [Fact]
    public async Task RunSetAsync_InvalidDataJson_ReturnsOne()
    {
        var handler = new PlayerDataMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await PlayerDataCommandFactory.RunSetAsync(
            client, "P1", "client", false, null, null, "not json", null, OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
        Assert.Contains("--data", stderr.ToString());
    }

    [Fact]
    public async Task RunSetAsync_NumericPermission_ReturnsOne()
    {
        var handler = new PlayerDataMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        // Numeric enum forms (e.g. "0") are not accepted — only the documented names.
        var exit = await PlayerDataCommandFactory.RunSetAsync(
            client, "P1", "client", false, "k", "v", null, "0", OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
        Assert.Contains("--permission", stderr.ToString());
    }

    [Fact]
    public async Task RunSetAsync_KeyValueAndData_ReturnsOneWithoutCalling()
    {
        var handler = new PlayerDataMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        // --key/--value and --data are mutually exclusive.
        var exit = await PlayerDataCommandFactory.RunSetAsync(
            client, "P1", "client", false, "k", "v", """{"a":"b"}""", null, OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
        Assert.Contains("not both", stderr.ToString());
    }

    [Fact]
    public async Task RunSetAsync_ComplexDataValue_StoredAsJsonString()
    {
        var handler = new PlayerDataMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);

        var exit = await PlayerDataCommandFactory.RunSetAsync(
            client, "P1", "client", false, null, null, """{"config":{"a":"b"},"tags":[1,2]}""", "Private", OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        var data = doc.RootElement.GetProperty("Data");
        Assert.Equal(JsonValueKind.String, data.GetProperty("config").ValueKind);
        Assert.Equal("""{"a":"b"}""", data.GetProperty("config").GetString());
        Assert.Equal("[1,2]", data.GetProperty("tags").GetString());
    }

    [Fact]
    public async Task RunSetAsync_NullDataValue_ReturnsOneWithoutCalling()
    {
        var handler = new PlayerDataMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await PlayerDataCommandFactory.RunSetAsync(
            client, "P1", "client", false, null, null, """{"k":null}""", null, OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
        Assert.Contains("--data", stderr.ToString());
    }

    [Fact]
    public async Task RunSetAsync_ScalarDataValues_SentAsStrings()
    {
        var handler = new PlayerDataMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);

        var exit = await PlayerDataCommandFactory.RunSetAsync(
            client, "P1", "client", false, null, null, """{"level":5,"vip":true}""", "Private", OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        var data = doc.RootElement.GetProperty("Data");
        Assert.Equal(JsonValueKind.String, data.GetProperty("level").ValueKind);
        Assert.Equal("5", data.GetProperty("level").GetString());
        Assert.Equal("true", data.GetProperty("vip").GetString());
    }

    [Fact]
    public async Task RunSetAsync_Success_WritesResponseToStdout()
    {
        var handler = new PlayerDataMockHandler(Ok("""{"code":200,"status":"OK","data":{"DataVersion":7}}"""));
        using var client = CreateClientWithHandler(handler);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await PlayerDataCommandFactory.RunSetAsync(
            client, "P1", "client", false, "level", "5", null, "Private", OutputOptions.Json, stdout, stderr,
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("DataVersion", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public async Task RunSetAsync_ApiError_ReturnsOneAndWritesError()
    {
        var errorJson = """{"code":400,"status":"BadRequest","error":"InvalidParams","errorCode":1000,"errorMessage":"bad key"}""";
        var handler = new PlayerDataMockHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(errorJson) });
        using var client = CreateClientWithHandler(handler);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await PlayerDataCommandFactory.RunSetAsync(
            client, "P1", "client", false, "level", "5", null, "Private", OutputOptions.Json, stdout, stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.NotEqual(string.Empty, stderr.ToString());
    }

    #endregion

    #region delete actions

    [Fact]
    public async Task RunDeleteAsync_SendsKeysToRemove()
    {
        var handler = new PlayerDataMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);

        var exit = await PlayerDataCommandFactory.RunDeleteAsync(
            client, "P1", "client", false, "a,b", OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("/Admin/UpdateUserData", handler.LastRequestUri);
        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        var keys = doc.RootElement.GetProperty("KeysToRemove").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new[] { "a", "b" }, keys);
        Assert.False(doc.RootElement.TryGetProperty("Data", out _));
    }

    [Fact]
    public async Task RunDeleteAsync_Internal_UsesInternalPath()
    {
        var handler = new PlayerDataMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);

        var exit = await PlayerDataCommandFactory.RunDeleteAsync(
            client, "P1", "internal", true, "a", OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("/Admin/UpdateUserPublisherInternalData", handler.LastRequestUri);
    }

    [Fact]
    public async Task RunDeleteAsync_ApiError_ReturnsOneAndWritesError()
    {
        var errorJson = """{"code":400,"status":"BadRequest","error":"InvalidParams","errorCode":1000,"errorMessage":"bad player id"}""";
        var handler = new PlayerDataMockHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(errorJson) });
        using var client = CreateClientWithHandler(handler);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await PlayerDataCommandFactory.RunDeleteAsync(
            client, "P1", "client", false, "a,b", OutputOptions.Json, stdout, stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.NotEqual(string.Empty, stderr.ToString());
    }

    [Fact]
    public async Task RunDeleteAsync_EmptyKeys_ReturnsOne()
    {
        var handler = new PlayerDataMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await PlayerDataCommandFactory.RunDeleteAsync(
            client, "P1", "client", false, "  ", OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
        Assert.Contains("--keys", stderr.ToString());
    }

    [Fact]
    public async Task RunDeleteAsync_SucceedsWithoutConfirmation_IntentionallyUngated()
    {
        // player-data delete routes through UpdateUser*Data (not a Delete*/Shutdown* path), so it is
        // deliberately NOT subject to the catastrophic --confirm hard-gate. This test documents that
        // intentional choice: removing named keys succeeds without any destructive confirmation.
        var handler = new PlayerDataMockHandler(Ok());
        using var client = CreateClientWithHandler(handler);

        var exit = await PlayerDataCommandFactory.RunDeleteAsync(
            client, "P1", "client", false, "a,b", OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("/Admin/UpdateUserData", handler.LastRequestUri);
    }

    #endregion

    #region Full path coverage (all 12 endpoints)

    [Fact]
    public async Task GetAndSet_ReachAll12Endpoints()
    {
        var paths = new List<string>();
        foreach (var visibility in new[] { "client", "read-only", "internal" })
        {
            foreach (var publisher in new[] { false, true })
            {
                PlayerDataCommandFactory.TryResolvePaths(visibility, publisher, out var getPath, out var setPath, out _, out _);
                paths.Add(getPath);
                paths.Add(setPath);

                foreach (var path in new[] { getPath, setPath })
                {
                    var handler = new PlayerDataMockHandler(Ok());
                    using var client = CreateClientWithHandler(handler);

                    // Only the resolved URL matters here, so send an empty body rather than a
                    // request shape that wouldn't match the update endpoints.
                    await client.CallApiAsync(path, "{}", CancellationToken.None);
                    Assert.Contains(path, handler.LastRequestUri);
                }
            }
        }

        Assert.Equal(12, paths.Distinct().Count());
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
internal sealed class PlayerDataMockHandler : HttpMessageHandler
{
    private readonly HttpResponseMessage _response;

    public PlayerDataMockHandler(HttpResponseMessage response) => _response = response;

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
