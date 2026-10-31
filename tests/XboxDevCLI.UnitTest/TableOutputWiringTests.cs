// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;
using Moq;
using System.CommandLine;
using System.Net;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.PlayFab.Commands;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

/// <summary>
/// Drives command actions with <c>--output table</c> and asserts a table actually comes out.
/// </summary>
/// <remarks>
/// This is the gap that let a broken feature ship green. The suite already covered the renderer
/// (called directly) and the view declarations (validated against the generated models), but
/// nothing exercised the wiring between them — so a command could declare a view, pass every
/// check, and still emit JSON because its action never consulted the view. `file list` did
/// exactly that.
/// <para>
/// Each test drives a real <c>Run*Async</c> through a fake transport, one per invocation path:
/// the shared helper path, the direct-format path, and a command with its own formatter.
/// </para>
/// </remarks>
[Collection("ConsoleRedirect")]
public class TableOutputWiringTests
{
    /// <summary>A rendered table always has a header and an all-dashes separator beneath it.</summary>
    private static void AssertLooksLikeATable(string output, params string[] expectedHeaders)
    {
        var lines = output.TrimEnd().Split('\n').Select(line => line.TrimEnd('\r')).ToArray();

        Assert.True(
            lines.Length >= 2,
            $"Expected a header and separator, got:\n{output}");

        foreach (var header in expectedHeaders)
        {
            Assert.Contains(header, lines[0], StringComparison.Ordinal);
        }

        Assert.True(
            lines[1].Length > 0 && lines[1].All(character => character is '-' or ' '),
            $"Expected a separator row of dashes, got: '{lines[1]}'");

        // The decisive assertion: JSON would start with '{'.
        Assert.False(
            output.TrimStart().StartsWith('{'),
            $"Expected a table but got JSON:\n{output}");
    }

    [Fact]
    public async Task HelperPath_RendersATableWhenTheCommandDeclaresAView()
    {
        // experiment list routes through PlayFabCommandHelper.InvokeAsync.
        const string Response =
            """{"code":200,"status":"OK","data":{"Experiments":[{"Id":"E1","Name":"Checkout","State":"Running","ExperimentType":"ABTest"}]}}""";

        var view = ViewFor("playfab", "experiment", "list");
        using var client = CreateClient(Response);
        var stdout = new StringWriter();

        var exit = await ExperimentCommandFactory.RunListAsync(
            client, OutputOptions.Table(view), stdout, new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);

        AssertLooksLikeATable(stdout.ToString(), "ID", "NAME", "STATE");
        Assert.Contains("Checkout", stdout.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The helper path owes a paged command the same next-page notice the shared path writes,
    /// on stderr so the table on stdout stays clean.
    /// </summary>
    [Fact]
    public async Task HelperPath_WritesTheNextPageNoticeForAPagedCommand()
    {
        const string Response =
            """{"code":200,"status":"OK","data":{"Experiments":[{"Id":"E1","Name":"Checkout"}],"SkipToken":"page2"}}""";

        var output = OutputOptions.Table(ViewFor("playfab", "experiment", "list"))
            with { Paging = new CommandPaging("PageSize", "SkipToken") };
        using var client = CreateClient(Response);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await ExperimentCommandFactory.RunListAsync(
            client, output, stdout, stderr, CancellationToken.None);

        Assert.Equal(0, exit);
        AssertLooksLikeATable(stdout.ToString(), "ID", "NAME");
        Assert.DoesNotContain("page2", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"SkipToken\": \"page2\"", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OwnFormatterPath_RendersATableWhenTheCommandDeclaresAView()
    {
        // file list has its own formatter and does not route through the shared invoke path.
        // It is the command that shipped broken, so it gets an explicit regression test.
        const string Response =
            """{"code":200,"status":"OK","data":{"Metadata":{"save.json":{"FileName":"save.json","LastModified":"2026-01-01T00:00:00Z","Checksum":"abc123"}},"ProfileVersion":3}}""";

        var view = ViewFor("playfab", "file", "list");
        using var client = CreateClient(Response);
        var stdout = new StringWriter();

        var exit = await FileCommandFactory.RunListAsync(
            client, "E1", "title_player_account", OutputOptions.Table(view), stdout, new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        AssertLooksLikeATable(stdout.ToString(), "FILE NAME", "LAST MODIFIED", "CHECKSUM");
        Assert.Contains("save.json", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("abc123", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegisteredHelperPath_RendersATableWhenTheCommandDeclaresAView()
    {
        const string Response =
            """{"code":200,"status":"OK","data":{"Experiments":[{"Id":"E1","Name":"Checkout","State":"Running","ExperimentType":"ABTest"}]}}""";

        var result = await InvokeRegisteredActionAsync(
            Response,
            ["playfab", "experiment", "list", "--output", "table"]);

        Assert.Equal(0, result.ExitCode);
        AssertLooksLikeATable(result.Stdout, "ID", "NAME", "STATE");
        Assert.Contains("Checkout", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegisteredOwnFormatterPath_RendersATableWhenTheCommandDeclaresAView()
    {
        const string Response =
            """{"code":200,"status":"OK","data":{"Metadata":{"save.json":{"FileName":"save.json","LastModified":"2026-01-01T00:00:00Z","Checksum":"abc123"}},"ProfileVersion":3}}""";

        var result = await InvokeRegisteredActionAsync(
            Response,
            [
                "playfab", "file", "list",
                "--entity-id", "E1",
                "--entity-type", "title_player_account",
                "--output", "table",
            ]);

        Assert.Equal(0, result.ExitCode);
        AssertLooksLikeATable(result.Stdout, "FILE NAME", "LAST MODIFIED", "CHECKSUM");
        Assert.Contains("save.json", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("abc123", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HelperPath_StillEmitsJsonWhenTableIsNotRequested()
    {
        // The view is declared, but the caller asked for JSON: the default must not change.
        const string Response =
            """{"code":200,"status":"OK","data":{"Experiments":[{"Id":"E1","Name":"Checkout"}]}}""";

        var view = ViewFor("playfab", "experiment", "list");
        using var client = CreateClient(Response);
        var stdout = new StringWriter();

        // The view exists but the caller asked for JSON, so it must be ignored.
        Assert.NotNull(view);
        await ExperimentCommandFactory.RunListAsync(
            client, OutputOptions.Json, stdout, new StringWriter(),
            CancellationToken.None);

        Assert.StartsWith("{", stdout.ToString().TrimStart(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OwnFormatterPath_StillEmitsJsonWhenTableIsNotRequested()
    {
        const string Response =
            """{"code":200,"status":"OK","data":{"Metadata":{"save.json":{"FileName":"save.json"}},"ProfileVersion":3}}""";

        var view = ViewFor("playfab", "file", "list");
        using var client = CreateClient(Response);
        var stdout = new StringWriter();

        await FileCommandFactory.RunListAsync(
            client, "E1", "title_player_account", OutputOptions.Json, stdout, new StringWriter(),
            CancellationToken.None);

        Assert.StartsWith("{", stdout.ToString().TrimStart(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyCollectionReportsNoResultsRatherThanAnEmptyTable()
    {
        const string Response = """{"code":200,"status":"OK","data":{"Experiments":[]}}""";

        var view = ViewFor("playfab", "experiment", "list");
        using var client = CreateClient(Response);
        var stdout = new StringWriter();

        var exit = await ExperimentCommandFactory.RunListAsync(
            client, OutputOptions.Table(view), stdout, new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);

        Assert.Contains("No results.", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileListTableTreatsOmittedMetadataAsNoResults()
    {
        const string Response = """{"code":200,"status":"OK","data":{"ProfileVersion":1}}""";

        var view = ViewFor("playfab", "file", "list");
        using var client = CreateClient(Response);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await FileCommandFactory.RunListAsync(
            client, "E1", "title_player_account", OutputOptions.Table(view), stdout, stderr,
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains(TableFormatter.EmptyNotice, stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, stderr.ToString());
    }

    /// <summary>
    /// A 2xx body that will not parse is a failed request, whichever format was asked for.
    /// </summary>
    /// <remarks>
    /// <c>file list</c> formats the response itself instead of routing through the shared invoke
    /// path, so nothing upstream validates the body on its behalf. Returning the raw text wrote a
    /// proxy error page to stdout and reported success — a failure wearing a success's clothes.
    /// Both formats are driven because each reaches the parse through a different call site.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileListRejectsAMalformedSuccessBody(bool asTable)
    {
        // An intercepting proxy's error page: HTTP 200, but not JSON.
        const string Response = "<html><body>502 Bad Gateway</body></html>";

        var output = asTable
            ? OutputOptions.Table(ViewFor("playfab", "file", "list"))
            : OutputOptions.Json;
        using var client = CreateClient(Response);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await FileCommandFactory.RunListAsync(
            client, "E1", "title_player_account", output, stdout, stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Contains("was not valid JSON", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("502 Bad Gateway", stdout.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Asserts the production handoff itself: that resolving options from a parse result picks up
    /// the command's declared view.
    /// </summary>
    /// <remarks>
    /// The tests above call <c>Run*Async</c> with options constructed directly, which proves the
    /// renderer is reached but not that the real invocation path supplies the view. A regression
    /// in <see cref="OutputOptions.From"/> — the single place every command resolves it — would
    /// otherwise go unnoticed.
    /// </remarks>
    [Theory]
    [InlineData("playfab", "mps", "build", "list")]
    [InlineData("playfab", "experiment", "list")]
    [InlineData("playfab", "file", "list")]
    [InlineData("context", "get")]
    [InlineData("context", "key", "list")]
    [InlineData("sandbox", "list")]
    [InlineData("sandbox", "get")]
    public void ResolvingOptionsFromAParseResultCarriesTheDeclaredView(params string[] path)
    {
        // One tree, so reference equality is meaningful: it proves the resolved view is the very
        // declaration hanging off the command, not a coincidentally equal copy.
        var root = Program.BuildRootCommand();
        var parseResult = root.Parse([.. path, "--output", "table"], Program.NoResponseFileConfig);

        var output = OutputOptions.From(parseResult);

        Assert.True(output.IsTable);
        Assert.NotNull(output.TableView);
        Assert.Same(DeclaredView(root, path), output.TableView);
    }

    /// <summary>A command with no declared view must resolve to no view, whatever was asked for.</summary>
    [Fact]
    public void ResolvingOptionsForACommandWithoutAViewCarriesNone()
    {
        var root = Program.BuildRootCommand();
        var parseResult = root.Parse(
            ["playfab", "api", "/Admin/GetTitleData", "--output", "table"],
            Program.NoResponseFileConfig);

        Assert.Null(OutputOptions.From(parseResult).TableView);
    }

    /// <summary>Asking for JSON must not surface a view, even where one is declared.</summary>
    [Fact]
    public void ResolvingOptionsForJsonCarriesNoViewEvenWhenDeclared()
    {
        var root = Program.BuildRootCommand();
        var parseResult = root.Parse(
            ["playfab", "mps", "build", "list"], Program.NoResponseFileConfig);

        var output = OutputOptions.From(parseResult);

        Assert.False(output.IsTable);
        Assert.Null(output.TableView);
        // The declaration is still reachable; only the format-gated accessor is null.
        Assert.NotNull(output.View);
    }

    #region Helpers

    private static TableView ViewFor(params string[] path) =>
        DeclaredView(Program.BuildRootCommand(), path);

    private static TableView DeclaredView(Command root, string[] path)
    {
        Command current = root;
        foreach (var segment in path)
        {
            current = current.Subcommands.Single(
                subcommand => string.Equals(subcommand.Name, segment, StringComparison.Ordinal));
        }

        var view = CommandMetadataRegistry.GetMetadata(current).Table;
        Assert.True(
            view is not null,
            $"'{string.Join(' ', path)}' declares no table view; this test is meaningless without one.");
        return view!;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> InvokeRegisteredActionAsync(
        string responseJson,
        string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        Program.ApiClientFactory = _ => CreateClient(responseJson);
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var root = Program.BuildRootCommand();
            var exitCode = await root.Parse(args, Program.NoResponseFileConfig).InvokeAsync();
            return (exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            Program.ApiClientFactory = null;
        }
    }

    private static PlayFabApiClient CreateClient(string responseJson)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PlayFab:TitleId"] = "TEST123",
                ["PlayFab:BaseUrlTemplate"] = "https://{0}.playfabapi.com",
                ["AzureAd:ClientId"] = "test-client-id",
                ["AzureAd:TenantId"] = "test-tenant-id",
                ["AzureAd:Scopes"] = "api://test/.default",
            })
            .Build();

        var mockAuth = new Mock<AuthenticationService>(config) { CallBase = false };
        mockAuth.Setup(auth => auth.AcquireTokenSilentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(AuthResult());

        var httpClient = new HttpClient(new StubHandler(responseJson))
        {
            BaseAddress = new Uri("https://TEST123.playfabapi.com/"),
        };

        return new PlayFabApiClient(config, mockAuth.Object, httpClient);
    }

    private static AuthenticationResult AuthResult() =>
        new(
            accessToken: "token",
            isExtendedLifeTimeToken: false,
            uniqueId: "test-unique-id",
            expiresOn: DateTimeOffset.UtcNow.AddHours(1),
            extendedExpiresOn: DateTimeOffset.UtcNow.AddHours(1),
            tenantId: "test-tenant-id",
            account: Mock.Of<IAccount>(),
            idToken: "test-id-token",
            scopes: ["api://test/.default"],
            correlationId: Guid.NewGuid());

    private sealed class StubHandler(string responseJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson),
            });
    }

    #endregion
}
