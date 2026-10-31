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
/// Coverage for the <c>playfab title api-policy</c> command group: command/option structure,
/// the <c>show</c> action (default and explicit <c>--policy-name</c>), the <c>set</c> action's
/// statement-source mutual exclusion, file handling and JSON validation, the request body actually
/// serialized (URI path + JSON), success output shaping, and the API-error path.
/// </summary>
public class ApiPolicyCommandTests
{
    private const string OkEnvelope = """{"code":200,"status":"OK","data":{"PolicyName":"ApiPolicy","PolicyVersion":3}}""";

    private const string SampleStatements =
        """[{"Resource":"pfrn:api--*","Action":"*","Effect":"Allow","Principal":"*","Comment":"allow all"}]""";

    #region Command structure

    [Fact]
    public void Create_BuildsApiPolicyTree()
    {
        var apiPolicy = ApiPolicyCommandFactory.Create();

        // Flat under `playfab`, matching title-data / player-data / file / group rather than
        // nesting a single child under an intermediate `title` group.
        Assert.Equal("api-policy", apiPolicy.Name);
        Assert.Contains(apiPolicy.Subcommands, c => c.Name == "get");
        Assert.Contains(apiPolicy.Subcommands, c => c.Name == "set");
    }

    [Fact]
    public void Show_HasOptionalPolicyNameOption()
    {
        var apiPolicy = ApiPolicyCommandFactory.Create();
        var show = apiPolicy.Subcommands.First(c => c.Name == "get");

        var policyName = Assert.Single(show.Options, o => o.Name == "--policy-name");
        Assert.False(policyName.Required);
    }

    [Fact]
    public void Set_HasExpectedOptions()
    {
        var apiPolicy = ApiPolicyCommandFactory.Create();
        var set = apiPolicy.Subcommands.First(c => c.Name == "set");

        foreach (var name in new[] { "--policy-name", "--statements", "--statements-file", "--policy-version", "--overwrite" })
        {
            Assert.Contains(set.Options, o => o.Name == name);
        }

        // The current policy version is mandatory for UpdatePolicy; the statement sources are not
        // individually required (exactly one is validated at runtime).
        Assert.True(set.Options.First(o => o.Name == "--policy-version").Required);
        Assert.False(set.Options.First(o => o.Name == "--statements").Required);
        Assert.False(set.Options.First(o => o.Name == "--statements-file").Required);
    }

    #endregion

    #region ResolvePolicyName

    [Theory]
    [InlineData(null, "ApiPolicy")]
    [InlineData("", "ApiPolicy")]
    [InlineData("   ", "ApiPolicy")]
    [InlineData("  Custom  ", "Custom")]
    [InlineData("ApiPolicy", "ApiPolicy")]
    public void ResolvePolicyName_DefaultsAndTrims(string? input, string expected)
    {
        Assert.Equal(expected, ApiPolicyCommandFactory.ResolvePolicyName(input));
    }

    #endregion

    #region TryParseStatements

    [Fact]
    public void TryParseStatements_ValidArray_ParsesAndRoundTripsEnum()
    {
        Assert.True(ApiPolicyCommandFactory.TryParseStatements(SampleStatements, out var statements, out var error));
        Assert.Null(error);
        var statement = Assert.Single(statements!);
        Assert.Equal("pfrn:api--*", statement.Resource);
        Assert.Equal("*", statement.Action);
        Assert.Equal(EffectType.Allow, statement.Effect);
        Assert.Equal("allow all", statement.Comment);
    }

    [Fact]
    public void TryParseStatements_CaseInsensitiveProperties()
    {
        var lower = """[{"resource":"pfrn:api--*","effect":"Deny"}]""";
        Assert.True(ApiPolicyCommandFactory.TryParseStatements(lower, out var statements, out _));
        var statement = Assert.Single(statements!);
        Assert.Equal("pfrn:api--*", statement.Resource);
        Assert.Equal(EffectType.Deny, statement.Effect);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParseStatements_Blank_ReturnsFalse(string? json)
    {
        Assert.False(ApiPolicyCommandFactory.TryParseStatements(json, out var statements, out var error));
        Assert.Null(statements);
        Assert.Contains("empty", error);
    }

    [Fact]
    public void TryParseStatements_InvalidJson_ReturnsFriendlyError()
    {
        Assert.False(ApiPolicyCommandFactory.TryParseStatements("[{", out var statements, out var error));
        Assert.Null(statements);
        Assert.Contains("Invalid JSON", error);
    }

    [Theory]
    [InlineData("""{"Resource":"pfrn:api--*"}""")]
    [InlineData("\"hi\"")]
    [InlineData("5")]
    public void TryParseStatements_NonArrayRoot_ReturnsFriendlyError(string json)
    {
        // Valid JSON that isn't an array must produce a friendly message without leaking the
        // internal List/PermissionStatement type names.
        Assert.False(ApiPolicyCommandFactory.TryParseStatements(json, out var statements, out var error));
        Assert.Null(statements);
        Assert.Contains("expected a JSON array", error);
        Assert.DoesNotContain("PermissionStatement", error);
    }

    [Fact]
    public void TryParseStatements_EmptyArray_ReturnsFalse()
    {
        Assert.False(ApiPolicyCommandFactory.TryParseStatements("[]", out var statements, out var error));
        Assert.Null(statements);
        Assert.Contains("at least one", error);
    }

    [Fact]
    public void TryParseStatements_MisspelledField_IsRejectedNotSilentlyDropped()
    {
        // The security case: with the serializer's default (Skip), a typo'd "ApiConditions" is
        // dropped without a word, turning a conditional permission into an unconditional one while
        // the command still reports success. The parse must fail instead.
        const string TypoedCondition = """
            [{"Resource":"pfrn:api--*","Action":"*","Effect":"Allow","Principal":"*",
              "ApiCondition":{"HasSignatureOrEncryption":"Required"}}]
            """;

        Assert.False(
            ApiPolicyCommandFactory.TryParseStatements(TypoedCondition, out var statements, out var error));
        Assert.Null(statements);
        Assert.Contains("ApiCondition", error);
        // The message must name the valid fields rather than the generated .NET type.
        Assert.Contains("ApiConditions", error);
        Assert.DoesNotContain("XboxDevCLI.PlayFab.Generated", error);
    }

    [Fact]
    public void TryParseStatements_UnknownField_IsRejected()
    {
        const string UnknownField = """
            [{"Resource":"pfrn:api--*","Action":"*","Effect":"Allow","Principal":"*","Nope":true}]
            """;

        Assert.False(
            ApiPolicyCommandFactory.TryParseStatements(UnknownField, out var statements, out var error));
        Assert.Null(statements);
        Assert.Contains("Nope", error);
    }

    [Fact]
    public void TryParseStatements_KnownFieldsWithDifferentCasing_StillParse()
    {
        // Rejecting unknown fields must not regress the documented case-insensitive binding.
        const string LowerCased = """
            [{"resource":"pfrn:api--*","action":"*","effect":"Allow","principal":"*","comment":"ok"}]
            """;

        Assert.True(
            ApiPolicyCommandFactory.TryParseStatements(LowerCased, out var statements, out var error));
        Assert.Null(error);
        var statement = Assert.Single(statements!);
        Assert.Equal("ok", statement.Comment);
    }

    #endregion

    #region show action

    [Fact]
    public async Task RunShowAsync_DefaultPolicyName_SendsApiPolicyToGetPolicy()
    {
        var handler = new ApiPolicyMockHandler(Ok(OkEnvelope));
        using var client = CreateClientWithHandler(handler);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await ApiPolicyCommandFactory.RunShowAsync(
            client, null, OutputOptions.Json, stdout, stderr,
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("/Admin/GetPolicy", handler.LastRequestUri);
        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        Assert.Equal("ApiPolicy", doc.RootElement.GetProperty("PolicyName").GetString());
        Assert.Contains("PolicyVersion", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public async Task RunShowAsync_ExplicitPolicyName_IsTrimmedAndSent()
    {
        var handler = new ApiPolicyMockHandler(Ok(OkEnvelope));
        using var client = CreateClientWithHandler(handler);

        var exit = await ApiPolicyCommandFactory.RunShowAsync(
            client, "  Custom  ", OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        Assert.Equal("Custom", doc.RootElement.GetProperty("PolicyName").GetString());
    }

    [Fact]
    public async Task RunShowAsync_JsonCompact_EmitsSingleLine()
    {
        var handler = new ApiPolicyMockHandler(Ok(OkEnvelope));
        using var client = CreateClientWithHandler(handler);
        var stdout = new StringWriter();

        var exit = await ApiPolicyCommandFactory.RunShowAsync(
            client, null, OutputOptions.JsonCompact, stdout, new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        var output = stdout.ToString().TrimEnd();
        Assert.DoesNotContain("\n", output);
        Assert.Contains("PolicyVersion", output);
    }

    [Fact]
    public async Task RunShowAsync_DataOnly_StripsEnvelope()
    {
        var handler = new ApiPolicyMockHandler(Ok(OkEnvelope));
        using var client = CreateClientWithHandler(handler);
        var stdout = new StringWriter();

        var exit = await ApiPolicyCommandFactory.RunShowAsync(
            client, null, OutputOptions.JsonDataOnly, stdout, new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        var output = stdout.ToString();
        Assert.Contains("PolicyVersion", output);
        Assert.DoesNotContain("status", output);
    }

    [Fact]
    public async Task RunShowAsync_ApiError_ReturnsOneAndWritesError()
    {
        var errorJson = """{"code":400,"status":"BadRequest","error":"InvalidParams","errorCode":1000,"errorMessage":"bad policy"}""";
        var handler = new ApiPolicyMockHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(errorJson) });
        using var client = CreateClientWithHandler(handler);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await ApiPolicyCommandFactory.RunShowAsync(
            client, null, OutputOptions.Json, stdout, stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.NotEqual(string.Empty, stderr.ToString());
    }

    #endregion

    #region set action

    [Fact]
    public async Task RunSetAsync_InlineStatements_SendsFullRequestToUpdatePolicy()
    {
        var handler = new ApiPolicyMockHandler(Ok(OkEnvelope));
        using var client = CreateClientWithHandler(handler);

        var exit = await ApiPolicyCommandFactory.RunSetAsync(
            client, null, SampleStatements, null, policyVersion: 7, overwrite: true,
            OutputOptions.Json, new StringWriter(), new StringWriter(), confirmed: true, cancellationToken: CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("/Admin/UpdatePolicy", handler.LastRequestUri);

        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        var root = doc.RootElement;
        Assert.Equal("ApiPolicy", root.GetProperty("PolicyName").GetString());
        Assert.Equal(7, root.GetProperty("PolicyVersion").GetDouble());
        Assert.True(root.GetProperty("OverwritePolicy").GetBoolean());

        var statement = Assert.Single(root.GetProperty("Statements").EnumerateArray());
        Assert.Equal("pfrn:api--*", statement.GetProperty("Resource").GetString());
        // The EffectType enum must serialize back to its string form on the wire.
        Assert.Equal("Allow", statement.GetProperty("Effect").GetString());
    }

    [Fact]
    public async Task RunSetAsync_DefaultsOverwriteToAppend()
    {
        var handler = new ApiPolicyMockHandler(Ok(OkEnvelope));
        using var client = CreateClientWithHandler(handler);

        var exit = await ApiPolicyCommandFactory.RunSetAsync(
            client, "Custom", SampleStatements, null, policyVersion: 0, overwrite: false,
            OutputOptions.Json, new StringWriter(), new StringWriter(), cancellationToken: CancellationToken.None);

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        Assert.False(doc.RootElement.GetProperty("OverwritePolicy").GetBoolean());
        Assert.Equal("Custom", doc.RootElement.GetProperty("PolicyName").GetString());
    }

    [Fact]
    public async Task RunSetAsync_StatementsFromFile_ReadsAndSends()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var path = Path.Combine(tempDir, "policy.json");
            await File.WriteAllTextAsync(path, SampleStatements);

            var handler = new ApiPolicyMockHandler(Ok(OkEnvelope));
            using var client = CreateClientWithHandler(handler);

            var exit = await ApiPolicyCommandFactory.RunSetAsync(
                client, null, null, path, policyVersion: 2, overwrite: false,
                OutputOptions.Json, new StringWriter(), new StringWriter(), cancellationToken: CancellationToken.None);

            Assert.Equal(0, exit);
            Assert.Contains("/Admin/UpdatePolicy", handler.LastRequestUri);
            using var doc = JsonDocument.Parse(handler.CapturedBody!);
            Assert.Single(doc.RootElement.GetProperty("Statements").EnumerateArray());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunSetAsync_BothStatementSources_ReturnsOneWithoutCalling()
    {
        var handler = new ApiPolicyMockHandler(Ok(OkEnvelope));
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await ApiPolicyCommandFactory.RunSetAsync(
            client, null, SampleStatements, "some-file.json", policyVersion: 1, overwrite: false,
            OutputOptions.Json, new StringWriter(), stderr, cancellationToken: CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
        Assert.Contains("not both", stderr.ToString());
    }

    [Fact]
    public async Task RunSetAsync_NoStatementSource_ReturnsOneWithoutCalling()
    {
        var handler = new ApiPolicyMockHandler(Ok(OkEnvelope));
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await ApiPolicyCommandFactory.RunSetAsync(
            client, null, null, null, policyVersion: 1, overwrite: false,
            OutputOptions.Json, new StringWriter(), stderr, cancellationToken: CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
        Assert.Contains("--statements", stderr.ToString());
    }

    [Fact]
    public async Task RunSetAsync_MissingFile_ReturnsOneWithoutCalling()
    {
        var handler = new ApiPolicyMockHandler(Ok(OkEnvelope));
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        // Point at a path under a directory that is guaranteed not to exist, so no temp directory
        // is created (and therefore none is leaked) just to name a missing file.
        var missing = Path.Combine(Path.GetTempPath(), $"xdc-missing-{Guid.NewGuid():N}", "does-not-exist.json");
        var exit = await ApiPolicyCommandFactory.RunSetAsync(
            client, null, null, missing, policyVersion: 1, overwrite: false,
            OutputOptions.Json, new StringWriter(), stderr, cancellationToken: CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
        Assert.Contains("File not found", stderr.ToString());
    }

    [Fact]
    public async Task RunSetAsync_InvalidStatementsJson_ReturnsOneWithoutCalling()
    {
        var handler = new ApiPolicyMockHandler(Ok(OkEnvelope));
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await ApiPolicyCommandFactory.RunSetAsync(
            client, null, """{"not":"an array"}""", null, policyVersion: 1, overwrite: false,
            OutputOptions.Json, new StringWriter(), stderr, cancellationToken: CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
        Assert.Contains("expected a JSON array", stderr.ToString());
    }

    [Fact]
    public async Task RunSetAsync_ApiError_ReturnsOneAndWritesError()
    {
        var errorJson = """{"code":400,"status":"BadRequest","error":"InvalidParams","errorCode":1000,"errorMessage":"stale version"}""";
        var handler = new ApiPolicyMockHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(errorJson) });
        using var client = CreateClientWithHandler(handler);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await ApiPolicyCommandFactory.RunSetAsync(
            client, null, SampleStatements, null, policyVersion: 1, overwrite: false,
            OutputOptions.Json, stdout, stderr, cancellationToken: CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.NotEqual(string.Empty, stderr.ToString());
    }

    [Fact]
    public async Task RunSetAsync_AppendSucceedsWithoutConfirmation()
    {
        // Appending is additive and reversible, and UpdatePolicy is not a catastrophic
        // Delete*/Shutdown* path, so the default (append) path is deliberately NOT hard-gated; it
        // relies on the read-only convention instead. Only --overwrite is gated, because it
        // discards statements. This documents that append still succeeds unconfirmed.
        var handler = new ApiPolicyMockHandler(Ok(OkEnvelope));
        using var client = CreateClientWithHandler(handler);

        var exit = await ApiPolicyCommandFactory.RunSetAsync(
            client, null, SampleStatements, null, policyVersion: 1, overwrite: false,
            OutputOptions.Json, new StringWriter(), new StringWriter(), cancellationToken: CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("/Admin/UpdatePolicy", handler.LastRequestUri);
    }

    [Fact]
    public async Task RunSetAsync_OverwriteWithoutConfirmation_ReturnsOneWithoutCalling()
    {
        // --overwrite replaces the whole policy, discarding any statement not restated. That is
        // not recoverable through this CLI, and the path-name guard in PlayFabApiClient does not
        // fire on UpdatePolicy, so the gate has to live here.
        var handler = new ApiPolicyMockHandler(Ok(OkEnvelope));
        using var client = CreateClientWithHandler(handler);
        var stderr = new StringWriter();

        var exit = await ApiPolicyCommandFactory.RunSetAsync(
            client, null, SampleStatements, null, policyVersion: 1, overwrite: true,
            OutputOptions.Json, new StringWriter(), stderr, cancellationToken: CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
        Assert.Contains("--confirm", stderr.ToString());
    }

    [Fact]
    public async Task RunSetAsync_UnreadableStatementsFile_ReturnsOneWithoutCalling()
    {
        // A file that exists but cannot be read (here: held with an exclusive lock) must surface a
        // clean error rather than an unhandled IOException. A missing path is already covered by
        // the File.Exists check; this covers the read itself failing.
        var path = Path.Combine(Path.GetTempPath(), $"xdc-locked-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, SampleStatements);
        try
        {
            using var exclusive = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.None);

            var handler = new ApiPolicyMockHandler(Ok(OkEnvelope));
            using var client = CreateClientWithHandler(handler);
            var stderr = new StringWriter();

            var exit = await ApiPolicyCommandFactory.RunSetAsync(
                client, null, null, path, policyVersion: 1, overwrite: false,
                OutputOptions.Json, new StringWriter(), stderr, cancellationToken: CancellationToken.None);

            Assert.Equal(1, exit);
            Assert.Null(handler.LastRequestUri);
            Assert.Contains("Could not read", stderr.ToString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    #endregion

    #region Test helpers

    private static HttpResponseMessage Ok(string json) =>
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
internal sealed class ApiPolicyMockHandler : HttpMessageHandler
{
    private readonly HttpResponseMessage _response;

    public ApiPolicyMockHandler(HttpResponseMessage response) => _response = response;

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
