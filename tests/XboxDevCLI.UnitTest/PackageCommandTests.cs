// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json;
using XboxDevCLI.Commands;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public sealed class PackageCommandTests : IDisposable
{
    private const string StoreId = "9N0000000000";
    private const string PackageId = "11111111-2222-3333-4444-555555555555";
    private const string PackagesPath = $"/xboxpartner/v1/games/{StoreId}/packages";
    private const string PackagePath = $"{PackagesPath}/{PackageId}";
    private const string PackageSetsPath = $"/xboxpartner/v1/games/{StoreId}/package-sets";

    private readonly string _directory = Directory.CreateTempSubdirectory("package-tests-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>Answers a request first; return null for the default answer.</summary>
    private Func<RecordedRequest, HttpResponseMessage?>? Gateway { get; set; }

    private string Games { get; set; } =
        $$"""{"games":[{"productId":"00000000000000000042","storeId":"{{StoreId}}","xboxTitleId":"1234ABCD","name":"Nod Arena"}]}""";

    private string PackageSets { get; set; } =
        $$"""{"packageSets":[{"id":"ps-0","name":"Flight","type":"Private","isMain":false,"isEditable":true,"marketGroups":[{"id":"default","name":"default","packageIds":["other"]}]},{"id":"ps-1","name":"Main","type":"Public","isMain":true,"isEditable":true,"marketGroups":[{"id":"default","name":"default","packageIds":["{{PackageId}}"]}]}],"mainPackageSetId":"ps-1"}""";

    private const string Package =
        $$"""{"id":"{{PackageId}}","version":"1.0.0.0","platform":"PC","packageType":"Msixvc","processingState":"Processed","isAvailableForPlaytest":true,"validationItems":[],"assets":[{"type":"Ekb","name":"Nod.Arena_1.0.0.0_x64_Full.ekb","size":6,"isCommitted":true}],"fileName":"Nod.Arena_1.0.0.0_x64.msixvc","size":11,"createdAt":"2026-11-04T18:00:00Z"}""";

    private HttpResponseMessage Respond(RecordedRequest request)
    {
        if (Gateway?.Invoke(request) is { } overridden)
        {
            return overridden;
        }

        return (request.Method.Method, request.Path) switch
        {
            ("GET", "/xboxpartner/v1/games") => RoutingHttpHandler.Json(Games),
            ("GET", PackageSetsPath) => RoutingHttpHandler.Json(PackageSets),
            ("GET", PackagePath) => RoutingHttpHandler.Json(Package),
            _ => RoutingHttpHandler.Json("""{"title":"Not found","status":404,"code":"package_not_found"}""", HttpStatusCode.NotFound),
        };
    }

    [Fact]
    public async Task List_ReadsEveryPackageOnMain()
    {
        PackageSets = """{"packageSets":[{"id":"ps-1","isMain":true,"marketGroups":[{"id":"default","packageIds":["p1","p2"]},{"id":"g2","packageIds":["p2","p3"]}]}],"mainPackageSetId":"ps-1"}""";
        Gateway = request => request.Method == HttpMethod.Get && request.Path.StartsWith(PackagesPath + "/", StringComparison.Ordinal)
            ? request.Path.EndsWith("/p3", StringComparison.Ordinal)
                ? RoutingHttpHandler.Json("""{"code":"package_not_found","status":404}""", HttpStatusCode.NotFound)
                : RoutingHttpHandler.Json($$"""{"id":"{{request.Path.Split('/')[^1]}}","processingState":"Processed","buildId":"b-1"}""")
            : null;

        var (handler, result) = await InvokeAsync(["package", "list", "--store-id", "9n0000000000", "--output", "json-compact"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            """{"packageSetId":"ps-1","packages":[{"id":"p1","processingState":"Processed","buildId":"b-1"},{"id":"p2","processingState":"Processed","buildId":"b-1"}]}""",
            result.Stdout.Trim());
        Assert.Contains("Package p3 is on the Main branch but couldn't be found", result.Stderr, StringComparison.Ordinal);
        Assert.Equal(PackageSetsPath, handler.Requests[0].Path);
        Assert.Equal(3, handler.Requests.Count(request => request.Path.StartsWith(PackagesPath + "/", StringComparison.Ordinal)));
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("?sellerId=1234567", request.Uri.Query);
            Assert.Null(request.Header("X-DeveloperAccountId"));
        });
        Assert.Single(handler.Requests.Select(request => request.Header("x-ms-correlation-id")).Distinct());
    }

    [Fact]
    public async Task List_NeverRequestsADotSegmentPackageId()
    {
        PackageSets = $$"""{"packageSets":[{"id":"ps-1","marketGroups":[{"id":"default","packageIds":[".","..","{{PackageId}}"]}]}],"mainPackageSetId":"ps-1"}""";

        var (handler, result) = await InvokeAsync(["package", "list", "--store-id", StoreId, "--output", "json-compact"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(PackageId, result.Stdout, StringComparison.Ordinal);
        Assert.Contains("Package . is on the Main branch but couldn't be found", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Package .. is on the Main branch but couldn't be found", result.Stderr, StringComparison.Ordinal);
        Assert.Equal([PackageSetsPath, PackagePath], handler.Requests.Select(request => request.Path).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public async Task Client_RefusesADotSegmentPackageId(string packageId)
    {
        var handler = new RoutingHttpHandler(Respond);
        var client = new XboxPartnerClient(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            new HttpClient(handler, disposeHandler: false),
            new FakeAuthenticationService(FakeAuthenticationService.CreateToken()));

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetPackageAsync(
            "https://gateway.example", 1234567, StoreId, packageId, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("get")]
    public async Task ARequestTimeoutIsReportedAsARequestFailure(string command)
    {
        Gateway = _ => throw new TaskCanceledException("timeout");
        string[] arguments = command == "get"
            ? ["package", "get", PackageId, "--store-id", StoreId]
            : ["package", "list", "--store-id", StoreId];

        var (_, result) = await InvokeAsync(arguments);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("Request timed out.", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CtrlC_ExitsQuietlyInsteadOfReportingATimeout()
    {
        using var cancellation = new CancellationTokenSource();
        Gateway = _ =>
        {
            cancellation.Cancel();
            throw new TaskCanceledException("canceled");
        };

        var (_, result) = await InvokeAsync(["package", "list", "--store-id", StoreId], cancellationToken: cancellation.Token);

        Assert.Equal(130, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Empty(TestEnvironment.WithoutEndpointWarnings(result.Stderr));
    }

    [Theory]
    [InlineData("list")]
    [InlineData("get")]
    public async Task CtrlC_WhileReadingAnErrorBodyExitsQuietly(string command)
    {
        using var cancellation = new CancellationTokenSource();
        Gateway = _ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StreamContent(new CancelOnReadStream(cancellation)),
        };
        string[] arguments = command == "get"
            ? ["package", "get", PackageId, "--store-id", StoreId]
            : ["package", "list", "--store-id", StoreId];

        var (_, result) = await InvokeAsync(arguments, cancellationToken: cancellation.Token);

        Assert.Equal(130, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Empty(TestEnvironment.WithoutEndpointWarnings(result.Stderr));
    }

    /// <summary>A response body that cancels the caller once its headers have arrived, then waits.</summary>
    private sealed class CancelOnReadStream(CancellationTokenSource caller) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await caller.CancelAsync();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task AStoreIdTheSellerHasNoGameWith_SaysSo()
    {
        Gateway = _ => RoutingHttpHandler.Json("""{"title":"Game not found.","status":404,"code":"game_not_found"}""", HttpStatusCode.NotFound);

        var (_, result) = await InvokeAsync(["package", "list", "--store-id", StoreId]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(
            $"No game with Store ID {StoreId} was found for seller 1234567. Check --store-id and --seller-id.",
            result.Stderr,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_AGameNotFoundOnAPackageRead_FailsInsteadOfSkippingThePackage()
    {
        Gateway = request => request.Path == PackagePath
            ? RoutingHttpHandler.Json("""{"title":"Game not found.","status":404,"code":"game_not_found"}""", HttpStatusCode.NotFound)
            : null;

        var (_, result) = await InvokeAsync(["package", "list", "--store-id", StoreId]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"No game with Store ID {StoreId} was found for seller 1234567.", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("couldn't be found", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_IsEmptyWhenTheGameHasNoMainBranch()
    {
        PackageSets = """{"packageSets":[]}""";

        var (handler, result) = await InvokeAsync(["package", "list", "--store-id", StoreId, "--output", "json-compact"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("""{"packageSetId":null,"packages":[]}""", result.Stdout.Trim());
        Assert.Contains("has no Main branch", result.Stderr, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task List_LeavesOutTheSellerWhenNoneIsGiven()
    {
        var (handler, result) = await InvokeAsync(["package", "list", "--store-id", StoreId], sellerId: null);

        Assert.Equal(0, result.ExitCode);
        Assert.All(handler.Requests, request => Assert.Equal(string.Empty, request.Uri.Query));
    }

    [Fact]
    public async Task List_SaysWhichSellerToChooseWhenThereAreSeveral()
    {
        Gateway = _ => RoutingHttpHandler.Json(
            """{"title":"Choose a seller","status":400,"code":"seller_selection_required"}""", HttpStatusCode.BadRequest);

        var (_, result) = await InvokeAsync(["package", "list", "--store-id", StoreId], sellerId: null);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Pass --seller-id", result.Stderr, StringComparison.Ordinal);
        Assert.Empty(result.Stdout);
    }

    [Fact]
    public async Task List_RendersATable()
    {
        var (_, result) = await InvokeAsync(["package", "list", "--store-id", StoreId, "--output", "table"]);

        Assert.Equal(0, result.ExitCode);
        string[] lines = result.Stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("PACKAGE ID", lines[0], StringComparison.Ordinal);
        Assert.Contains("PLAYTEST", lines[0], StringComparison.Ordinal);
        Assert.Contains(PackageId, lines[2], StringComparison.Ordinal);
        Assert.Contains("Processed", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_FindsTheGameByTitleId()
    {
        var (handler, result) = await InvokeAsync(["package", "list", "--xbox-title-id", "0x1234abcd"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("?titleId=1234ABCD&pageSize=100", handler.Requests[0].Uri.Query);
        Assert.Equal("1234567", handler.Requests[0].Header("X-DeveloperAccountId"));
        Assert.Equal(PackageSetsPath, handler.Requests[1].Path);
    }

    [Fact]
    public async Task Get_ReadsOnePackage()
    {
        var (handler, result) = await InvokeAsync(["package", "get", PackageId, "--store-id", StoreId]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(PackagePath, Assert.Single(handler.Requests).Path);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal(11, document.RootElement.GetProperty("size").GetInt32());
    }

    [Fact]
    public async Task Get_RendersATable()
    {
        var (_, result) = await InvokeAsync(["package", "get", PackageId, "--store-id", StoreId, "--output", "table"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("1.0.0.0", result.Stdout, StringComparison.Ordinal);
        Assert.Equal(3, result.Stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Theory]
    [InlineData(new[] { "package", "list" }, "Pass exactly one of --store-id or --xbox-title-id.")]
    [InlineData(new[] { "package", "get", " ", "--store-id", "9N0000000000" }, "The package ID cannot be empty.")]
    [InlineData(new[] { "package", "get", ".", "--store-id", "9N0000000000" }, "The package ID can't be '.' or '..'.")]
    [InlineData(new[] { "package", "get", "..", "--store-id", "9N0000000000" }, "The package ID can't be '.' or '..'.")]
    [InlineData(new[] { "package", "get", " . ", "--store-id", "9N0000000000" }, "The package ID can't be '.' or '..'.")]
    [InlineData(new[] { "package", "get", " .. ", "--store-id", "9N0000000000" }, "The package ID can't be '.' or '..'.")]
    public async Task GameSelection_IsCheckedBeforeAnyRequest(string[] arguments, string error)
    {
        var (handler, result) = await InvokeAsync(arguments);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(error, result.Stderr, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TitleId_NeedsASeller()
    {
        var (handler, result) = await InvokeAsync(["package", "list", "--xbox-title-id", "1234ABCD"], sellerId: null);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("needs a seller", result.Stderr, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "package_permission_denied", "Packages Read permission on this game for this seller")]
    [InlineData(HttpStatusCode.Forbidden, "identity_override_not_authorized", "You don't belong to that seller")]
    [InlineData(HttpStatusCode.BadRequest, "seller_selection_required", "Pass --seller-id")]
    [InlineData(HttpStatusCode.Forbidden, null, "names a game of this seller")]
    [InlineData(HttpStatusCode.Unauthorized, null, "Run 'xbox auth login' and retry")]
    public async Task RefusedAccess_SaysWhatToDo(HttpStatusCode status, string? code, string message)
    {
        Gateway = request => request.Path == PackageSetsPath
            ? RoutingHttpHandler.Json(
                code is null ? $$"""{"status":{{(int)status}}}""" : $$"""{"status":{{(int)status}},"code":"{{code}}"}""", status)
            : null;

        var (_, result) = await InvokeAsync(["package", "list", "--store-id", StoreId]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(message, result.Stderr, StringComparison.Ordinal);
        if (code is not null)
        {
            Assert.DoesNotContain("Check the supplied input", result.Stderr, StringComparison.Ordinal);
        }

        Assert.Empty(result.Stdout);
    }

    [Fact]
    public async Task SignedOut_SaysToSignIn()
    {
        var (handler, result) = await InvokeAsync(["package", "list", "--store-id", StoreId], signedIn: false);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("auth login", result.Stderr, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    private async Task<(RoutingHttpHandler Handler, CapturedInvocation Result)> InvokeAsync(
        string[] arguments,
        bool signedIn = true,
        long? sellerId = 1234567,
        CancellationToken cancellationToken = default)
    {
        var handler = new RoutingHttpHandler(Respond);
        var auth = new FakeAuthenticationService(signedIn ? FakeAuthenticationService.CreateToken() : null);
        var root = Program.BuildRootCommand(packageCommandFactory: () => PackageCommandFactory.Create(
            configuration => new XboxPartnerClient(configuration, new HttpClient(handler, disposeHandler: false), auth)));
        var args = new List<string>(arguments) { "--base-url", "https://gateway.example" };
        if (sellerId is { } id)
        {
            args.AddRange(["--seller-id", id.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }

        var oldOut = Console.Out;
        var oldError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            ContextResolver.ReadIdentityAsync = _ => SignedInIdentity.FromCacheAsync(auth, CancellationToken.None);
            ContextStore.DefaultDirectory = () => Path.Combine(_directory, "state");
            var parseResult = await Program.ResolveContextAsync(root.Parse(args.ToArray(), Program.NoResponseFileConfig), args);
            // The default handler is off so that exceptions the command lets through reach the test.
            int exitCode = parseResult is null ? 1 : await parseResult.InvokeAsync(
                new System.CommandLine.InvocationConfiguration { EnableDefaultExceptionHandler = false }, cancellationToken);
            return (handler, new CapturedInvocation(exitCode, stdout.ToString(), stderr.ToString()));
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldError);
            TestEnvironment.Reset();
        }
    }

    private sealed record CapturedInvocation(int ExitCode, string Stdout, string Stderr);
}
