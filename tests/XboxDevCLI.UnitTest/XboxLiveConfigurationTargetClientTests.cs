// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

public sealed class XboxLiveConfigurationTargetClientTests
{
    private const string Scid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string ScidHeader = "X-XboxPartner-Scid";
    private const string SellerHeader = "X-XboxPartner-Seller-Id";
    private const string PrimaryHeader = "X-XboxPartner-Resolved-Title-Id";
    private static readonly string[] TargetHeaders = [ScidHeader, SellerHeader, PrimaryHeader];

    /// <summary>
    /// The XboxPartner operations the client targets. They're keyed by operation, not CLI command:
    /// <c>config status</c> sends both the configuration read and the publish status read.
    /// </summary>
    public static TheoryData<string, string> Commands
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var (family, action) in AllCommands)
            {
                data.Add(family, action);
            }
            return data;
        }
    }

    public static TheoryData<string, string, string?> InvalidScid =>
        WithCommands([null, "", "invalid", "00000000-0000-0000-0000-000000000000"]);

    public static TheoryData<string, string, string, string?> IgnoredMetadata
    {
        get
        {
            var data = new TheoryData<string, string, string, string?>();
            (string Header, string? Value)[] ignored =
            [
                (SellerHeader, null),
                (SellerHeader, ""),
                (SellerHeader, "0"),
                (SellerHeader, "-1"),
                (SellerHeader, "1234567.0"),
                (SellerHeader, "9223372036854775808"),
                (PrimaryHeader, null),
                (PrimaryHeader, ""),
                (PrimaryHeader, "00000000"),
                (PrimaryHeader, "80000000"),
                (PrimaryHeader, "43"),
                (PrimaryHeader, "0x00000043"),
            ];
            foreach (var (family, action) in AllCommands)
            {
                foreach (var (header, value) in ignored)
                {
                    data.Add(family, action, header, value);
                }
            }
            return data;
        }
    }

    private static readonly (string Family, string Action)[] AllCommands =
    [
        ("configuration", "list"), ("configuration", "get"),
        ("document", "download"), ("document", "commit"),
        ("publish", "start"), ("publish", "status"),
    ];

    private static TheoryData<string, string, string?> WithCommands(string?[] values)
    {
        var data = new TheoryData<string, string, string?>();
        foreach (var (family, action) in AllCommands)
        {
            foreach (string? value in values)
            {
                data.Add(family, action, value);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task TitleResponses_ExposeTypedBoundTargetWithoutChangingOperationRequest(
        string family, string action)
    {
        var handler = new XboxPartnerHttpMessageHandler(Target(Response(family, action), bound: true));
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        var targets = new List<XboxLiveResolvedConfigurationTarget>();

        await InvokeAsync(client, family, action, targets.Add);

        var target = Assert.Single(targets);
        Assert.Equal(Guid.Parse(Scid), target.Scid);
        Assert.Equal(1234567L, target.SellerId);
        Assert.Equal("00000042", target.TitleId);
        Assert.Equal("00000043", target.ResolvedTitleId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(OperationPath(family, action), request.RequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("00000042", body.RootElement.GetProperty("titleId").GetString());
        AssertNullOrAbsent(body.RootElement, "scid");
        AssertNullOrAbsent(body.RootElement, "sellerId");
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task TitleResponses_AcceptCaseInsensitiveHeaderNamesAndUnboundPrimary(
        string family, string action)
    {
        var response = Target(Response(family, action));
        foreach (var name in TargetHeaders)
        {
            if (!response.Headers.TryGetValues(name, out var values)) continue;
            string[] copied = values.ToArray();
            response.Headers.Remove(name);
            response.Headers.Add(name.ToLowerInvariant(), copied);
        }
        var handler = new XboxPartnerHttpMessageHandler(response);
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        XboxLiveResolvedConfigurationTarget? target = null;

        await InvokeAsync(client, family, action, value => target = value);

        Assert.NotNull(target);
        Assert.Equal("00000042", target.TitleId);
        Assert.Equal("00000042", target.ResolvedTitleId);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [MemberData(nameof(InvalidScid))]
    public async Task InvalidResponseScid_ThrowsTypedCorrelatedErrorWithoutCallbackOrReplay(
        string family, string action, string? value)
    {
        var response = Target(Response(family, action));
        response.Headers.Remove(ScidHeader);
        if (value is not null) response.Headers.TryAddWithoutValidation(ScidHeader, value);
        response.Headers.Add("x-ms-correlation-id", "invalid-operation-target");
        var handler = new XboxPartnerHttpMessageHandler(response);
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        int callbacks = 0;

        var error = await Assert.ThrowsAsync<XboxPartnerResponseException>(() =>
            InvokeAsync(client, family, action, _ => callbacks++));

        Assert.Equal("invalid-operation-target", error.CorrelationId);
        Assert.Equal(0, callbacks);
        Assert.Equal(OperationPath(family, action), Assert.Single(handler.Requests).RequestUri!.AbsolutePath);
    }

    [Theory]
    [MemberData(nameof(IgnoredMetadata))]
    public async Task InvalidDisplayMetadata_IsIgnoredWithoutReplay(
        string family, string action, string header, string? value)
    {
        var response = Target(Response(family, action));
        response.Headers.Remove(header);
        if (value is not null) response.Headers.TryAddWithoutValidation(header, value);
        var handler = new XboxPartnerHttpMessageHandler(response);
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        XboxLiveResolvedConfigurationTarget? target = null;

        await InvokeAsync(client, family, action, value => target = value);

        Assert.NotNull(target);
        Assert.Equal(Guid.Parse(Scid), target.Scid);
        if (header == SellerHeader) Assert.Null(target.SellerId);
        else Assert.Null(target.ResolvedTitleId);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task ResponseSellerCannotReplaceExplicitlySelectedSeller(string family, string action)
    {
        var handler = new XboxPartnerHttpMessageHandler(Target(Response(family, action)));
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        XboxLiveResolvedConfigurationTarget? target = null;

        await InvokeAsync(client, family, action, value => target = value, sellerId: 7654321);

        Assert.NotNull(target);
        Assert.Equal(7654321L, target.SellerId);
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        Assert.Equal(7654321L, body.RootElement.GetProperty("sellerId").GetInt64());
    }

    [Fact]
    public async Task InvalidMetadataWithoutServerCorrelation_PreservesRequestCorrelation()
    {
        var handler = new XboxPartnerHttpMessageHandler(Response("configuration", "get"));
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);

        var error = await Assert.ThrowsAsync<XboxPartnerResponseException>(() =>
            InvokeAsync(client, "configuration", "get"));

        Assert.Equal(Assert.Single(Assert.Single(handler.Requests).Headers["x-ms-correlation-id"]),
            error.CorrelationId);
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task DirectScid_LegacyResponsesMayOmitAllTargetHeaders(string family, string action)
    {
        var handler = new XboxPartnerHttpMessageHandler(Response(family, action));
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);

        await InvokeAsync(client, family, action, scid: Guid.Parse(Scid), titleId: null);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(OperationPath(family, action), request.RequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal(Scid, body.RootElement.GetProperty("scid").GetString());
        AssertNullOrAbsent(body.RootElement, "titleId");
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task DirectScid_SellerHeaderReportsRequestedScidAndEffectiveSeller(string family, string action)
    {
        var handler = new XboxPartnerHttpMessageHandler(Target(Response(family, action), includeTitle: false));
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        XboxLiveResolvedConfigurationTarget? target = null;

        await InvokeAsync(client, family, action, value => target = value, scid: Guid.Parse(Scid), titleId: null);

        Assert.NotNull(target);
        Assert.Equal(Guid.Parse(Scid), target.Scid);
        Assert.Equal(1234567L, target.SellerId);
        Assert.Null(target.TitleId);
        Assert.Null(target.ResolvedTitleId);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(ScidHeader, Scid)]
    [InlineData(PrimaryHeader, "00000042")]
    public async Task DirectScid_TitleOnlyMetadataIsIgnored(string header, string value)
    {
        var response = Target(Response("publish", "status"), includeTitle: false);
        response.Headers.Add(header, value);
        var handler = new XboxPartnerHttpMessageHandler(response);
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        XboxLiveResolvedConfigurationTarget? target = null;

        await InvokeAsync(client, "publish", "status", value => target = value,
            scid: Guid.Parse(Scid), titleId: null);

        Assert.NotNull(target);
        Assert.Equal(Guid.Parse(Scid), target.Scid);
        Assert.Null(target.TitleId);
        Assert.Null(target.ResolvedTitleId);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task DirectScid_MetadataCannotReplaceSelectedSeller(string family, string action)
    {
        var handler = new XboxPartnerHttpMessageHandler(Target(Response(family, action), includeTitle: false));
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        XboxLiveResolvedConfigurationTarget? target = null;

        await InvokeAsync(client, family, action, value => target = value,
            sellerId: 7654321, scid: Guid.Parse(Scid), titleId: null);

        Assert.NotNull(target);
        Assert.Equal(7654321L, target.SellerId);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task DirectScid_TitleMetadataWithoutSellerReportsNoSeller()
    {
        var response = Response("publish", "status");
        response.Headers.Add(ScidHeader, Scid);
        var handler = new XboxPartnerHttpMessageHandler(response);
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        XboxLiveResolvedConfigurationTarget? target = null;

        await InvokeAsync(client, "publish", "status", value => target = value,
            scid: Guid.Parse(Scid), titleId: null);

        Assert.NotNull(target);
        Assert.Null(target.SellerId);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task PreCanceledOperation_StopsBeforeAuthenticationAndNetwork(string family, string action)
    {
        var handler = new XboxPartnerHttpMessageHandler(Target(Response(family, action)));
        using var http = new HttpClient(handler);
        int authentications = 0;
        using var client = CreateClient(http, () =>
        {
            authentications++;
            return Task.FromResult<string?>("test-token");
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InvokeAsync(client, family, action, cancellationToken: cancellation.Token));

        Assert.Equal(0, authentications);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task InvalidSelectors_AllCommandsRejectBeforeAuthenticationAndNetwork(string family, string action)
    {
        (Guid? Scid, string? Title)[] invalid =
        [
            (null, null),
            (Guid.Empty, null),
            (Guid.Parse(Scid), "00000042"),
            (null, ""),
            (null, " "),
            (null, "00000000"),
            (null, "80000000"),
            (null, "FFFFFFFF"),
            (null, "42"),
            (null, "0x00000042"),
        ];
        foreach (var (scid, title) in invalid)
        {
            var handler = new XboxPartnerHttpMessageHandler(Response(family, action));
            using var http = new HttpClient(handler);
            int authentications = 0;
            using var client = CreateClient(http, () =>
            {
                authentications++;
                return Task.FromResult<string?>("test-token");
            });

            await Assert.ThrowsAnyAsync<ArgumentException>(() =>
                InvokeAsync(client, family, action, scid: scid, titleId: title));

            Assert.Equal(0, authentications);
            Assert.Empty(handler.Requests);
        }
    }

    [Fact]
    public async Task ConfigurationList_CancelingAfterFirstPageDoesNotAuthenticateOrSendNextPage()
    {
        var first = Target(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"configurations":[{"sandbox":"XDKS.1"}],"continuationToken":"next"}"""),
        });
        var handler = new XboxPartnerHttpMessageHandler(first, Response("configuration", "list"));
        using var http = new HttpClient(handler);
        int authentications = 0;
        using var client = CreateClient(http, () =>
        {
            authentications++;
            return Task.FromResult<string?>("test-token");
        });
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetXboxLiveConfigurationsAsync(null, null, cancellation.Token, "00000042",
                _ => cancellation.Cancel()));

        Assert.Equal(1, authentications);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("publish", "start")]
    [InlineData("document", "commit")]
    public async Task CancellationAfterSubmission_NeverReplaysWrite(string family, string action)
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new CancelingHandler(cancellation);
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InvokeAsync(client, family, action, cancellationToken: cancellation.Token));

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task DocumentDownload_TargetMetadataDoesNotReplaceArchiveMetadataOrBytes()
    {
        var response = Target(Response("document", "download"), bound: true);
        byte[] expected = await response.Content.ReadAsByteArrayAsync();
        var handler = new XboxPartnerHttpMessageHandler(response);
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);

        var result = await client.GetXboxLiveDocumentsAsync(null,
            new XboxLiveGetDocumentsRequest("sandbox", null, "XDKS.1", "published", 7, ["nsal.xml"], "00000042"),
            CancellationToken.None);

        Assert.Equal(expected, result.Content);
        Assert.Equal("\"etag\"", result.ETag);
        Assert.Equal(7UL, result.Version);
        Assert.Equal("published", result.View);
        Assert.NotNull(result.Target);
        Assert.Equal(Guid.Parse(Scid), result.Target.Scid);
        Assert.Equal(1234567L, result.Target.SellerId);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("download")]
    [InlineData("commit")]
    public async Task AccountDocuments_DoNotRequireTitleMetadata(string action)
    {
        var handler = new XboxPartnerHttpMessageHandler(Response("document", action));
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);

        if (action == "download")
        {
            var archive = await client.GetXboxLiveDocumentsAsync(null,
                new XboxLiveGetDocumentsRequest("account", null, null, "working", null, []),
                CancellationToken.None);
            Assert.Null(archive.Target);
        }
        else
        {
            await client.CommitXboxLiveDocumentsAsync(null,
                new XboxLiveCommitDocumentsRequest("account", null, null, true, false, null, null, []),
                CancellationToken.None);
        }

        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        AssertNullOrAbsent(body.RootElement, "scid");
        AssertNullOrAbsent(body.RootElement, "titleId");
    }

    private static Task InvokeAsync(
        XboxPartnerClient client,
        string family,
        string action,
        Action<XboxLiveResolvedConfigurationTarget>? targetResolved = null,
        long? sellerId = null,
        Guid? scid = null,
        string? titleId = "00000042",
        CancellationToken cancellationToken = default) =>
        (family, action) switch
        {
            ("configuration", "list") => client.GetXboxLiveConfigurationsAsync(
                sellerId, scid, cancellationToken, titleId, targetResolved),
            ("configuration", "get") => client.GetXboxLiveConfigurationAsync(
                sellerId, scid, "XDKS.1", cancellationToken, titleId, targetResolved),
            ("document", "download") => CaptureArchiveTargetAsync(
                client.GetXboxLiveDocumentsAsync(sellerId,
                    new XboxLiveGetDocumentsRequest("sandbox", scid, "XDKS.1", "working", null, [], titleId),
                    cancellationToken),
                targetResolved),
            ("document", "commit") => client.CommitXboxLiveDocumentsAsync(sellerId,
                new XboxLiveCommitDocumentsRequest("sandbox", scid, "XDKS.1", false, false, null, null,
                    [new XboxDocumentUpload("nsal.xml", "<ServiceNsal />"u8.ToArray())], titleId),
                cancellationToken, targetResolved),
            ("publish", "start") => client.PublishXboxLiveConfigurationAsync(sellerId,
                new XboxLivePublishRequest(scid, "XDKS.1", "XDKS.1", false, null, TitleId: titleId),
                cancellationToken, targetResolved),
            ("publish", "status") => client.GetXboxLivePublishStatusAsync(
                sellerId, scid, "XDKS.1", cancellationToken, titleId, targetResolved),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

    private static async Task CaptureArchiveTargetAsync(
        Task<XboxPartnerDocumentArchive> operation,
        Action<XboxLiveResolvedConfigurationTarget>? targetResolved)
    {
        var archive = await operation;
        if (archive.Target is { } target) targetResolved?.Invoke(target);
    }

    private static string OperationPath(string family, string action) =>
        "/xboxpartner/v1/xboxlive/" + ((family, action) switch
        {
            ("configuration", "list") => "configurations",
            ("configuration", "get") => "configurations/get",
            ("document", "download") => "documents",
            ("document", "commit") => "documents/commit",
            ("publish", "start") => "publish",
            ("publish", "status") => "publish/status",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        });

    private static XboxPartnerClient CreateClient(HttpClient http, Func<Task<string?>>? tokenFactory = null) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["XboxPartner:BaseUrl"] = "https://gateway.example" }).Build(),
            http, tokenFactory ?? (() => Task.FromResult<string?>("test-token")));

    private static void AssertNullOrAbsent(JsonElement body, string property) =>
        Assert.True(!body.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null);

    private static HttpResponseMessage Target(
        HttpResponseMessage response, bool bound = false, bool includeTitle = true)
    {
        response.Headers.Add(SellerHeader, "1234567");
        if (includeTitle)
        {
            response.Headers.Add(ScidHeader, Scid);
            response.Headers.Add(PrimaryHeader, bound ? "00000043" : "00000042");
        }
        return response;
    }

    private static HttpResponseMessage Response(string family, string action) =>
        XboxLiveConfigurationTargetTests.Operation("config", (family, action) switch
        {
            ("configuration", "list") => "list",
            ("configuration", "get") => "status",
            ("document", "download") => "download",
            ("document", "commit") => "save",
            _ => "publish",
        });

    private sealed class CancelingHandler(CancellationTokenSource cancellation) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The request token was not canceled.");
        }
    }
}
