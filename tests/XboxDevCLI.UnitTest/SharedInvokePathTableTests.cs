// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;
using Moq;
using System.CommandLine;
using System.Net;
using XboxDevCLI.CommandLine;
using XboxDevCLI.PlayFab;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

/// <summary>
/// Drives a real command end to end through <see cref="Program.Main"/> with a faked transport, so
/// the shared invocation path's choice of formatter is covered.
/// </summary>
/// <remarks>
/// <see cref="Program.InvokeApiAsync"/> is the path every metadata-backed command takes — all 13
/// MPS collection views and the MPS record views included. The other wiring tests call
/// <c>Run*Async</c> directly, and the renderer tests call the formatter directly, so nothing
/// asserted that this path consults the declared view. A regression in its branch would return
/// JSON for every one of those commands with the suite still green, which is how the feature
/// shipped broken the first time.
/// <para>
/// Console output is process-global, so this joins the shared collection rather than running in
/// parallel with other console tests.
/// </para>
/// </remarks>
[Collection("ConsoleRedirect")]
public class SharedInvokePathTableTests
{
    private const string BuildListResponse =
        """
        {"code":200,"status":"OK","data":{"BuildSummaries":[
          {"BuildId":"b1","BuildName":"combat-sandbox","CreationTime":"2026-04-26T22:15:16.7Z"}
        ]}}
        """;

    private const string BuildGetResponse =
        """
        {"code":200,"status":"OK","data":{"BuildId":"b1","BuildName":"combat-sandbox","BuildStatus":"Deployed"}}
        """;

    [Fact]
    public async Task SharedPath_RendersACollectionTable()
    {
        var (exit, stdout, _) = await RunAsync(
            BuildListResponse,
            ["--playfab-title-id", "ABC123", "playfab", "mps", "build", "list", "--output", "table"]);

        Assert.Equal(0, exit);
        Assert.False(
            stdout.TrimStart().StartsWith('{'),
            $"Expected a table from the shared invoke path but got JSON:\n{stdout}");
        Assert.Contains("BUILD ID", stdout, StringComparison.Ordinal);
        Assert.Contains("combat-sandbox", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SharedPath_RendersARecordTable()
    {
        var (exit, stdout, _) = await RunAsync(
            BuildGetResponse,
            ["--playfab-title-id", "ABC123", "playfab", "mps", "build", "get", "--body", """{"BuildId":"b1"}""", "--output", "table"]);

        Assert.Equal(0, exit);
        Assert.False(stdout.TrimStart().StartsWith('{'), $"Expected a table, got JSON:\n{stdout}");

        var lines = stdout.TrimEnd().Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        Assert.Equal(3, lines.Length);
        Assert.Contains("BUILD ID", lines[0], StringComparison.Ordinal);
        Assert.Contains("STATUS", lines[0], StringComparison.Ordinal);
        Assert.Contains("b1", lines[2], StringComparison.Ordinal);
        Assert.Contains("Deployed", lines[2], StringComparison.Ordinal);
    }

    /// <summary>Without <c>--output table</c> the same command must still produce JSON.</summary>
    [Fact]
    public async Task SharedPath_StillDefaultsToJson()
    {
        var (exit, stdout, _) = await RunAsync(
            BuildListResponse,
            ["--playfab-title-id", "ABC123", "playfab", "mps", "build", "list"]);

        Assert.Equal(0, exit);
        Assert.StartsWith("{", stdout.TrimStart(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The refusal is the documented contract for an undeclared command, so assert it end to end:
    /// nonzero exit, the explanation on stderr, and nothing on stdout.
    /// </summary>
    /// <remarks>
    /// The other tests here cover <c>SupportsTableOutput</c> in isolation. A regression in the
    /// guard in <c>Program.Main</c> would let an unsupported command run and return JSON with
    /// exit 0 — the silent-fallback behavior this feature exists to avoid.
    /// </remarks>
    [Fact]
    public async Task UnsupportedTableRequestIsRefusedEndToEnd()
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);

        try
        {
            var exit = await Program.Main(
                ["--playfab-title-id", "ABC123", "playfab", "api", "/Admin/GetTitleData", "--output", "table"]);

            Assert.Equal(1, exit);
            Assert.Contains("is not available for this command", stderr.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, stdout.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    /// <summary>
    /// A help request wins over format validation.
    /// </summary>
    /// <remarks>
    /// <c>--output</c> is recursive, so it is in scope for a help request too. Validating first
    /// meant <c>... --output table --help</c> printed the refusal instead of help — refusing to
    /// explain itself to the user most likely to be asking, namely one whose table request just
    /// failed.
    /// </remarks>
    /// <summary>
    /// Every route to Help outranks the table gate, not just <c>--help</c>.
    /// </summary>
    /// <remarks>
    /// There are four: the option, the <c>help</c> command, a bare group, and the bare root. The
    /// gate originally recognised only the option and rejected the other three, each reported as
    /// a separate bug. They are covered together here so a fifth route fails loudly.
    /// </remarks>
    [Theory]
    [InlineData("playfab", "api", "/Admin/GetTitleData", "--output", "table", "--help")]
    [InlineData("help", "playfab", "api", "--output", "table")]
    [InlineData("playfab", "mps", "--output", "table")]
    [InlineData("--output", "table")]
    public async Task HelpIsShownEvenWhenTableIsUnavailable(params string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);

        try
        {
            var exit = await Program.Main(args);

            Assert.Equal(0, exit);
            Assert.DoesNotContain(
                Program.TableUnavailableMessage, stderr.ToString(), StringComparison.Ordinal);
            Assert.NotEqual(string.Empty, stdout.ToString().Trim());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    /// <summary>
    /// Every Help action the CLI installs carries the marker the gate keys off.
    /// </summary>
    /// <remarks>
    /// The behavioural test above can only cover routes someone remembered to add. This walks the
    /// built tree instead: any command whose action renders Help must be recognisable as such, so
    /// a new group or default action wired with a bare lambda is caught here rather than by a user
    /// whose help request is refused.
    /// </remarks>
    [Fact]
    public void EveryDefaultGroupActionIsMarkedAsRenderingHelp()
    {
        var root = Program.BuildRootCommand();
        var unmarked = new List<string>();

        void Walk(Command command, string path)
        {
            // A command that only groups subcommands renders help when invoked bare.
            if (command.Subcommands.Count > 0
                && command.Subcommands.Any(child => !child.Hidden)
                && command.Action is not null
                && command.Action is not HelpRenderingAction)
            {
                unmarked.Add(path);
            }

            foreach (var child in command.Subcommands)
            {
                Walk(child, $"{path} {child.Name}".Trim());
            }
        }

        Walk(root, string.Empty);

        Assert.True(
            unmarked.Count == 0,
            "These group commands render help but their action is not a HelpRenderingAction, so " +
            $"the --output table gate will reject a bare invocation: {string.Join(", ", unmarked)}");
    }

    /// <summary>
    /// The command reference quotes the refusal verbatim, so it must still match what ships.
    /// </summary>
    /// <remarks>
    /// This exact text drifted once already: the runtime stopped telling users to re-run with
    /// `--output json (the default)` and the docs kept saying it, leaving the reference advising
    /// something the CLI no longer prints. Nothing generates the docs, so only a test catches it.
    /// </remarks>
    [Fact]
    public void DocumentedRefusalMatchesTheOneThatShips()
    {
        // The development repository keeps the user docs in docs/public, and the released
        // source keeps them in docs.
        var docs = Path.Combine(RepositoryRoot(), "docs");
        var reference = new[] { Path.Combine(docs, "public", "commands.md"), Path.Combine(docs, "commands.md") }
            .FirstOrDefault(File.Exists);
        Assert.True(reference is not null, $"Command reference not found under '{docs}'.");

        // The docs hard-wrap the message, so compare on collapsed whitespace.
        static string Collapse(string value) =>
            string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        Assert.Contains(
            Collapse(Program.TableUnavailableMessage),
            Collapse(File.ReadAllText(reference)),
            StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !Directory.Exists(Path.Combine(directory.FullName, "docs")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "Could not locate the repository root from the test directory.");
        return directory!.FullName;
    }

    /// <summary>
    /// A parse error outranks the table gate, so a typo is reported as a typo.
    /// </summary>
    /// <remarks>
    /// The parser reports its errors when the action runs, which is after the gate, so adding
    /// <c>--output table</c> to an otherwise-invalid invocation used to replace the real
    /// diagnostic with a complaint about the format — sending the user to fix the wrong thing.
    /// </remarks>
    [Fact]
    public async Task ParseErrorsOutrankTheTableRefusal()
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);

        try
        {
            var exit = await Program.Main(["auth", "status", "--nope", "--output", "table"]);

            Assert.Equal(1, exit);

            var written = stdout.ToString() + stderr.ToString();
            Assert.Contains("--nope", written, StringComparison.Ordinal);
            Assert.DoesNotContain(Program.TableUnavailableMessage, written, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    /// <summary>
    /// The commands that reach the API through <c>PlayFabCommandHelper</c> rather than
    /// <see cref="Program.InvokeApiAsync"/> also honour their declared view end to end.
    /// </summary>
    /// <remarks>
    /// <c>file list</c> is the reason this feature needed a second look: it declared a view, its
    /// own action formatted JSON anyway, and every test passed because they drove the renderer and
    /// the declaration separately. Supplying the options by hand reproduces that blind spot, so
    /// this goes through <see cref="Program.Main"/> and would fail if the action stopped
    /// forwarding what the parse resolved.
    /// </remarks>
    [Fact]
    public async Task HelperPath_RendersATableEndToEnd()
    {
        const string Response =
            """
            {"code":200,"status":"OK","data":{"Metadata":{
              "save.dat":{"FileName":"save.dat","LastModified":"2026-04-26T22:15:16.7Z","Checksum":"abc123"}
            }}}
            """;

        var (exit, stdout, _) = await RunAsync(
            Response,
            ["--playfab-title-id", "ABC123", "playfab", "file", "list",
             "--entity-id", "E1", "--entity-type", "title", "--output", "table"]);

        Assert.Equal(0, exit);
        Assert.False(
            stdout.TrimStart().StartsWith('{'),
            $"Expected a table from the helper path but got JSON:\n{stdout}");
        Assert.Contains("FILE NAME", stdout, StringComparison.Ordinal);
        Assert.Contains("save.dat", stdout, StringComparison.Ordinal);
    }

    /// <summary>The same command still produces JSON when no table is asked for.</summary>
    [Fact]
    public async Task HelperPath_StillDefaultsToJson()
    {
        const string Response =
            """{"code":200,"status":"OK","data":{"Metadata":{"save.dat":{"FileName":"save.dat"}}}}""";

        var (exit, stdout, _) = await RunAsync(
            Response,
            ["--playfab-title-id", "ABC123", "playfab", "file", "list",
             "--entity-id", "E1", "--entity-type", "title"]);

        Assert.Equal(0, exit);
        Assert.StartsWith("{", stdout.TrimStart(), StringComparison.Ordinal);
    }

    private const string MorePagesNotice = "More results exist; this table shows one page.";

    /// <summary>
    /// A table has nowhere to show the next-page token, so when the response carries one the
    /// user is told on stderr that the table is one page, and given the token to send back.
    /// </summary>
    [Fact]
    public async Task TableOutputSaysOnStderrWhenMorePagesExist()
    {
        const string Response =
            """
            {"code":200,"status":"OK","data":{"BuildSummaries":[
              {"BuildId":"b1","BuildName":"combat-sandbox","CreationTime":"2026-04-26T22:15:16.7Z"}
            ],"PageSize":1,"SkipToken":"page2+/="}}
            """;

        var (exit, stdout, stderr) = await RunAsync(
            Response,
            ["--playfab-title-id", "ABC123", "playfab", "mps", "build", "list", "--output", "table"]);

        Assert.Equal(0, exit);
        Assert.Contains("combat-sandbox", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(MorePagesNotice, stdout, StringComparison.Ordinal);
        Assert.Contains(MorePagesNotice, stderr, StringComparison.Ordinal);
        Assert.Contains("\"SkipToken\": \"page2+/=\"", stderr, StringComparison.Ordinal);
        Assert.Contains("\"PageSize\"", stderr, StringComparison.Ordinal);
    }

    /// <summary>The last page has no token, and gets no notice.</summary>
    [Theory]
    [InlineData(",\"SkipToken\":null")]
    [InlineData(",\"SkipToken\":\"\"")]
    [InlineData("")]
    public async Task TableOutputIsSilentOnTheLastPage(string skipToken)
    {
        var response =
            "{\"code\":200,\"status\":\"OK\",\"data\":{\"BuildSummaries\":[" +
            "{\"BuildId\":\"b1\",\"BuildName\":\"combat-sandbox\"}]" + skipToken + "}}";

        var (exit, stdout, stderr) = await RunAsync(
            response,
            ["--playfab-title-id", "ABC123", "playfab", "mps", "build", "list", "--output", "table"]);

        Assert.Equal(0, exit);
        Assert.Contains("combat-sandbox", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(MorePagesNotice, stderr, StringComparison.Ordinal);
    }

    /// <summary>JSON already carries the token, so it is left exactly as it was.</summary>
    [Fact]
    public async Task JsonOutputGetsNoPagingNotice()
    {
        const string Response =
            """{"code":200,"status":"OK","data":{"BuildSummaries":[],"SkipToken":"page2"}}""";

        var (exit, stdout, stderr) = await RunAsync(
            Response,
            ["--playfab-title-id", "ABC123", "playfab", "mps", "build", "list"]);

        Assert.Equal(0, exit);
        Assert.Contains("\"SkipToken\": \"page2\"", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(MorePagesNotice, stdout + stderr, StringComparison.Ordinal);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string responseJson,
        string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        Program.ApiClientFactory = config => CreateClient(config, responseJson);

        try
        {
            var exit = await Program.Main(args);
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Program.ApiClientFactory = null;
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static PlayFabApiClient CreateClient(IConfiguration config, string responseJson)
    {
        var mockAuth = new Mock<AuthenticationService>(config) { CallBase = false };
        mockAuth.Setup(auth => auth.AcquireTokenSilentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(AuthResult());

        var httpClient = new HttpClient(new StubHandler(responseJson))
        {
            BaseAddress = new Uri("https://ABC123.playfabapi.com/"),
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
            tenantId: "test-tenant",
            account: null,
            idToken: null,
            scopes: ["api://test/.default"],
            correlationId: Guid.NewGuid());

    private sealed class StubHandler(string responseJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson),
            });
    }
}
