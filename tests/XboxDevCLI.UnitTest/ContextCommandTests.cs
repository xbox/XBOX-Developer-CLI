// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;
using Moq;
using XboxDevCLI.Commands;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

/// <summary>
/// <c>xbox context get|set|unset</c>. Context is local state: the account comes from the token
/// cache and nothing is sent over the network, which every test that signs in checks by
/// asserting no token was requested.
/// </summary>
[Collection("ConsoleRedirect")]
public sealed class ContextCommandTests : IDisposable
{
    private const string ProfileVariable = "XBOX_PROFILE";
    private const string LoopbackGateway = "http://localhost:54848";
    private const string SignInHint =
        "Not signed in, so saved context isn't shown. Run 'xbox auth login' to see it.";

    /// <summary>
    /// The variable these tests set, and those that would move the endpoints values are scoped to.
    /// Each test starts with them unset; the machine's values are restored afterwards.
    /// </summary>
    private static readonly string[] Variables =
    [
        ProfileVariable,
        "XBOX_XBOXPARTNER__BASEURL",
        "XBOX_PLAYFAB__BASEURLTEMPLATE",
    ];

    private static readonly IConfiguration ShippedConfiguration = TestSettings.BuiltIn();

    /// <summary>
    /// The endpoints the commands under test resolve to, read from the built-in settings
    /// rather than hardcoded so the scopes these tests save under stay aligned with what ships.
    /// </summary>
    private static readonly string GatewayBaseUrl =
        ShippedConfiguration["XboxPartner:BaseUrl"]!.TrimEnd('/');

    private static readonly string PlayFabEndpoint =
        ContextResolver.GetPlayFabEndpoint(ShippedConfiguration);

    /// <summary>The account <see cref="FakeAuthenticationService.CreateToken"/> signs in as.</summary>
    private static readonly SignedInIdentity Identity =
        SignedInIdentity.FromAccount(FakeAuthenticationService.CreateAccount());

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"xbox-context-command-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string?> _originalVariables =
        Variables.ToDictionary(name => name, name => Environment.GetEnvironmentVariable(name));
    private readonly FakeAuthenticationService _signedIn = new(FakeAuthenticationService.CreateToken());
    private readonly ContextStore _store;
    private int _storeRequests;

    public ContextCommandTests()
    {
        _store = new ContextStore(StorePath);
        foreach (var name in Variables)
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    private string StorePath => Path.Combine(_directory, ContextStore.FileName);

    public void Dispose()
    {
        foreach (var (name, value) in _originalVariables)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Set_SavesTheValueForTheSignedInAccountWithoutANetworkRequest()
    {
        var result = await InvokeAsync("context", "set", "seller-id", "1234567", "--output", "json-compact");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            """{"profile":"default","values":[{"key":"seller-id","value":"1234567"}]}""",
            result.Stdout.TrimEnd());
        Assert.Empty(result.Stderr);
        Assert.Equal("1234567", await _store.GetAsync(Identity, "seller-id", GatewayBaseUrl));
        Assert.Equal(1, _signedIn.AccountRequests);
        Assert.Equal(0, _signedIn.TokenRequests);
    }

    [Fact]
    public async Task Set_SavesTheSellerForTheGatewayNamedByBaseUrl()
    {
        var result = await InvokeAsync(
            "context", "set", "seller-id", "1234567", "--base-url", LoopbackGateway);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("1234567", await _store.GetAsync(Identity, "seller-id", LoopbackGateway));
        Assert.Null(await _store.GetAsync(Identity, "seller-id", GatewayBaseUrl));
        Assert.Equal(0, _signedIn.TokenRequests);
    }

    /// <summary>A title is not a seller: it is saved for the PlayFab endpoint.</summary>
    [Fact]
    public async Task Set_SavesThePlayFabTitleForThePlayFabEndpoint()
    {
        var result = await InvokeAsync(
            "context", "set", "playfab-title-id", "ABC12", "--output", "json-compact");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            """{"profile":"default","values":[{"key":"playfab-title-id","value":"ABC12"}]}""",
            result.Stdout.TrimEnd());
        Assert.Empty(result.Stderr);
        Assert.Equal("ABC12", await _store.GetAsync(Identity, "playfab-title-id", PlayFabEndpoint));
        Assert.Null(await _store.GetAsync(Identity, "playfab-title-id", GatewayBaseUrl));
        Assert.Equal(0, _signedIn.TokenRequests);
    }

    [Fact]
    public async Task Set_TableShowsTheSavedValue()
    {
        var result = await InvokeAsync("context", "set", "seller-id", "1234567", "--output", "table");

        Assert.Equal(0, result.ExitCode);
        var lines = result.Stdout.TrimEnd().Split(Environment.NewLine);
        Assert.Equal(3, lines.Length);
        Assert.Matches(@"^KEY\s+VALUE$", lines[0]);
        Assert.Matches(@"^seller-id\s+1234567$", lines[2]);
    }

    /// <summary>
    /// Key names are matched like <c>context unset</c> matches them, and the output names the key
    /// as it is saved. The value is trimmed like any other input.
    /// </summary>
    [Fact]
    public async Task Set_KeyNameIsCaseInsensitiveAndOutputUsesItsCanonicalName()
    {
        var result = await InvokeAsync(
            "context", "set", "SELLER-ID", " 1234567 ", "--output", "json-compact");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            """{"profile":"default","values":[{"key":"seller-id","value":"1234567"}]}""",
            result.Stdout.TrimEnd());
        Assert.Equal("1234567", await _store.GetAsync(Identity, "seller-id", GatewayBaseUrl));
    }

    /// <summary>
    /// Values are checked only for their format, before the sign-in is read, so nothing invalid
    /// is ever saved and a mistake costs no lookup.
    /// </summary>
    [Theory]
    [InlineData("Unknown context key 'title-id'. Valid keys: playfab-title-id, sandbox-id, seller-id.", "title-id", "ABC12")]
    [InlineData("seller-id must not be blank.", "seller-id", "   ")]
    [InlineData("seller-id must not be blank.", "seller-id", "")]
    [InlineData("Cannot parse argument 'abc' for option '--seller-id'", "seller-id", "abc")]
    [InlineData("--seller-id must be a positive numeric value.", "seller-id", "0")]
    [InlineData("Cannot parse argument '9223372036854775808' for option '--seller-id'", "seller-id", "9223372036854775808")]
    [InlineData("--playfab-title-id must contain only letters and digits.", "playfab-title-id", "ABC-12")]
    [InlineData("--sandbox-id must be non-empty and cannot contain whitespace.", "sandbox-id", "Dev 1")]
    [InlineData("Unknown context key 'sandbox'. Valid keys: playfab-title-id, sandbox-id, seller-id.", "sandbox", "Dev.1")]
    public async Task Set_InvalidInputFailsBeforeTheSignInIsRead(string expected, string key, string value)
    {
        var result = await InvokeAsync("context", "set", key, value);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.StartsWith(expected, result.Stderr, StringComparison.Ordinal);
        Assert.Equal(0, _signedIn.AccountRequests);
        Assert.False(File.Exists(StorePath));
    }

    /// <summary>
    /// The key and value are both required, and one call saves one key: anything else is a parse
    /// error, reported before the command runs.
    /// </summary>
    [Theory]
    [InlineData("context", "set")]
    [InlineData("context", "set", "seller-id")]
    [InlineData("context", "set", "seller-id", "1234567", "playfab-title-id", "ABC12")]
    public async Task Set_TakesExactlyOneKeyAndValue(params string[] args)
    {
        var result = await InvokeAsync(args);

        // System.CommandLine follows a parse error with Help on stdout; what matters is that no
        // result is written and nothing runs.
        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("\"values\"", result.Stdout, StringComparison.Ordinal);
        Assert.NotEmpty(result.Stderr);
        Assert.Equal(0, _signedIn.AccountRequests);
        Assert.Equal(0, _storeRequests);
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public async Task Set_NotSignedInWritesNothing()
    {
        var signedOut = new FakeAuthenticationService(token: null);

        var result = await InvokeAsync(signedOut, "context", "set", "seller-id", "1234567");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Equal("Not signed in. Run 'xbox auth login' first." + Environment.NewLine, result.Stderr);
        Assert.False(File.Exists(StorePath));
        Assert.Equal(0, signedOut.TokenRequests);
    }

    [Theory]
    [InlineData("unreadable-cache", "The token cache could not be read.")]
    [InlineData("account-without-identifiers", "The cached sign-in does not identify a tenant and user.")]
    public async Task Set_UnusableSignInWritesNothing(string failure, string expected)
    {
        var result = await InvokeAsync(CreateUnusableSignIn(failure), "context", "set", "seller-id", "1234567");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains(expected, result.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(StorePath));
    }

    /// <summary>
    /// Settings don't set context values, so a seller or title in a setting's variable neither
    /// outranks a saved value nor shows up in its place.
    /// </summary>
    [Fact]
    public async Task SettingsNeitherShadowNorReplaceSavedValues()
    {
        var originalTitle = Environment.GetEnvironmentVariable("XBOX_PLAYFAB__TITLEID");
        var originalSeller = Environment.GetEnvironmentVariable("XBOX_XBOXPARTNER__SELLERID");
        Environment.SetEnvironmentVariable("XBOX_PLAYFAB__TITLEID", "FILE12");
        Environment.SetEnvironmentVariable("XBOX_XBOXPARTNER__SELLERID", "99999999");

        try
        {
            var set = await InvokeAsync("context", "set", "playfab-title-id", "ABC12");
            var get = await InvokeAsync("context", "get", "--output", "json-compact");

            Assert.Equal(0, set.ExitCode);
            Assert.Empty(set.Stderr);
            Assert.Equal(
                """{"account":{"username":"dev@nodpublishers.com","tenantId":"tenant"},"profile":"default","values":[{"key":"playfab-title-id","value":"ABC12"},{"key":"sandbox-id","value":null},{"key":"seller-id","value":null}]}""",
                get.Stdout.TrimEnd());
        }
        finally
        {
            Environment.SetEnvironmentVariable("XBOX_PLAYFAB__TITLEID", originalTitle);
            Environment.SetEnvironmentVariable("XBOX_XBOXPARTNER__SELLERID", originalSeller);
        }
    }

    /// <summary>
    /// Mirrors <c>auth status</c>: nothing saved is a reportable state, not an error, so scripts
    /// can branch on the payload instead of on a nonzero exit code.
    /// </summary>
    [Fact]
    public async Task Get_ReportsEveryKeyWithNullsWhenNothingIsSet()
    {
        var result = await InvokeAsync("context", "get");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Stderr);
        using var output = JsonDocument.Parse(result.Stdout);
        var account = output.RootElement.GetProperty("account");
        Assert.Equal("dev@nodpublishers.com", account.GetProperty("username").GetString());
        Assert.Equal("tenant", account.GetProperty("tenantId").GetString());
        Assert.Equal(ContextProfiles.Default, output.RootElement.GetProperty("profile").GetString());
        var values = output.RootElement.GetProperty("values").EnumerateArray().ToArray();
        Assert.Equal(["playfab-title-id", "sandbox-id", "seller-id"], values.Select(value => value.GetProperty("key").GetString()));
        Assert.All(values, value =>
        {
            Assert.Equal(["key", "value"], value.EnumerateObject().Select(property => property.Name));
            Assert.Equal(JsonValueKind.Null, value.GetProperty("value").ValueKind);
        });
    }

    [Fact]
    public async Task Get_ReportsSavedValuesWithoutANetworkRequest()
    {
        await SaveAsync(Identity, GatewayBaseUrl, "1234567", PlayFabEndpoint, "ABC12");

        var result = await InvokeAsync("context", "get", "--output", "json-compact");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            """{"account":{"username":"dev@nodpublishers.com","tenantId":"tenant"},"profile":"default","values":[{"key":"playfab-title-id","value":"ABC12"},{"key":"sandbox-id","value":null},{"key":"seller-id","value":"1234567"}]}""",
            result.Stdout.TrimEnd());
        Assert.Empty(result.Stderr);

        // One read of the token cache serves both keys, and no token is requested.
        Assert.Equal(1, _signedIn.AccountRequests);
        Assert.Equal(0, _signedIn.TokenRequests);
    }

    /// <summary>
    /// <c>get</c> shows what is saved, which is what a command uses when it isn't given a flag. A
    /// flag applies only to the command it's given to, so one given to <c>get</c> changes nothing
    /// it shows.
    /// </summary>
    [Fact]
    public async Task Get_ShowsSavedValuesNotTheTitleFlag()
    {
        await SaveAsync(Identity, GatewayBaseUrl, "7654321", PlayFabEndpoint, "SAVED1");

        var result = await InvokeAsync(
            "context", "get", "--playfab-title-id", "FLAG1", "--output", "json-compact");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            """{"account":{"username":"dev@nodpublishers.com","tenantId":"tenant"},"profile":"default","values":[{"key":"playfab-title-id","value":"SAVED1"},{"key":"sandbox-id","value":null},{"key":"seller-id","value":"7654321"}]}""",
            result.Stdout.TrimEnd());
        Assert.Empty(result.Stderr);
    }

    /// <summary>
    /// <c>get</c> is how a script or agent checks its target, so being signed out is a state to
    /// report, not a failure: every value is null, even with a flag given, and stderr says why.
    /// </summary>
    [Fact]
    public async Task Get_SignedOutReportsNoValuesAndHowToSeeThem()
    {
        var result = await InvokeAsync(
            new FakeAuthenticationService(token: null),
            "context", "get", "--playfab-title-id", "FLAG1", "--output", "json-compact");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            """{"account":null,"profile":"default","values":[{"key":"playfab-title-id","value":null},{"key":"sandbox-id","value":null},{"key":"seller-id","value":null}]}""",
            result.Stdout.TrimEnd());
        Assert.Equal(SignInHint + Environment.NewLine, result.Stderr);
        Assert.False(File.Exists(StorePath));
    }

    /// <summary>
    /// A token cache that can't be read, or holds an account without its identifiers, is
    /// reported and then treated as signed out, so <c>get</c> still succeeds.
    /// </summary>
    [Theory]
    [InlineData("unreadable-cache", "The token cache could not be read.")]
    [InlineData("invalid-cache", "The token cache is in an unexpected state.")]
    [InlineData("account-without-identifiers", "The cached sign-in does not identify a tenant and user.")]
    public async Task Get_UnusableSignInIsReportedAndTreatedAsSignedOut(string failure, string expected)
    {
        await SaveAsync(Identity, GatewayBaseUrl, "1234567");

        var result = await InvokeAsync(CreateUnusableSignIn(failure), "context", "get", "--output", "json-compact");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            """{"account":null,"profile":"default","values":[{"key":"playfab-title-id","value":null},{"key":"sandbox-id","value":null},{"key":"seller-id","value":null}]}""",
            result.Stdout.TrimEnd());
        var lines = result.Stderr.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith(expected, lines[0], StringComparison.Ordinal);
        Assert.Equal(SignInHint, lines[1]);
    }

    /// <summary>
    /// An explicitly blank flag, such as <c>--playfab-title-id "$TITLE"</c> with the variable
    /// unset, is a mistake rather than "no flag": it fails to parse instead of quietly falling
    /// back to the saved title, so neither the sign-in nor saved context is read.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankTitleFlag_IsAParseErrorEvenWithASavedTitle(string title)
    {
        await SaveAsync(Identity, GatewayBaseUrl, "1234567", PlayFabEndpoint, "SAVED1");

        var result = await InvokeAsync("context", "get", "--playfab-title-id", title);

        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("\"values\"", result.Stdout, StringComparison.Ordinal);
        Assert.Contains(
            "--playfab-title-id must contain at least one non-whitespace character.",
            result.Stderr,
            StringComparison.Ordinal);
        Assert.Equal(0, _signedIn.AccountRequests);
        Assert.Equal(0, _storeRequests);
    }

    /// <summary>A seller saved against a test gateway must not leak into production output.</summary>
    [Fact]
    public async Task Get_ScopesTheSavedSellerByGatewayAuthority()
    {
        await SaveAsync(Identity, LoopbackGateway, "11111111");

        var production = await InvokeAsync("context", "get", "--output", "json-compact");
        var loopback = await InvokeAsync(
            "context", "get", "--base-url", LoopbackGateway, "--output", "json-compact");

        using var productionOutput = JsonDocument.Parse(production.Stdout);
        Assert.Equal(
            JsonValueKind.Null,
            productionOutput.RootElement.GetProperty("values")[2].GetProperty("value").ValueKind);
        using var loopbackOutput = JsonDocument.Parse(loopback.Stdout);
        Assert.Equal(
            "11111111",
            loopbackOutput.RootElement.GetProperty("values")[2].GetProperty("value").GetString());
    }

    /// <summary>
    /// <c>context key list</c> lists every key the other context commands accept, with how to
    /// supply each. It reads the registry, so it needs no sign-in and never opens saved context.
    /// </summary>
    [Fact]
    public async Task KeyList_ListsEveryKeyWithoutSignInOrSavedContext()
    {
        var result = await InvokeAsync(
            new FakeAuthenticationService(token: null),
            "context", "key", "list", "--output", "json-compact");

        Assert.Equal(0, result.ExitCode);
        using var doc = JsonDocument.Parse(result.Stdout);
        Assert.Equal(
            """{"keys":[{"name":"playfab-title-id","usedBy":"playfab"},{"name":"sandbox-id","usedBy":"xbl"},{"name":"seller-id","usedBy":"game, sandbox, test-account, xbl"}]}""",
            result.Stdout.TrimEnd());
        Assert.Empty(result.Stderr);
        Assert.Equal(0, _storeRequests);
    }

    [Fact]
    public async Task KeyList_TableShowsEachKeyAndTheGroupsThatUseIt()
    {
        var result = await InvokeAsync("context", "key", "list", "--output", "table");

        Assert.Equal(0, result.ExitCode);
        var lines = result.Stdout.TrimEnd().Split(Environment.NewLine);
        Assert.Equal(5, lines.Length);
        Assert.Matches(@"^KEY\s+USED BY$", lines[0]);
        Assert.Matches(@"^playfab-title-id\s+playfab$", lines[2]);
        Assert.Matches(@"^sandbox-id\s+xbl$", lines[3]);
        Assert.Matches(@"^seller-id\s+game, sandbox, test-account, xbl$", lines[4]);
    }

    [Fact]
    public async Task Get_TableHasKeyAndValueColumns()
    {
        await SaveAsync(Identity, GatewayBaseUrl, "1234567");

        var result = await InvokeAsync("context", "get", "--output", "table");

        Assert.Equal(0, result.ExitCode);
        var lines = result.Stdout.TrimEnd().Split(Environment.NewLine);
        Assert.Equal(5, lines.Length);
        Assert.Matches(@"^KEY\s+VALUE$", lines[0]);
        Assert.Matches(@"^seller-id\s+1234567$", lines[4]);

        // An unset value shows as '-', like any empty cell, rather than as a blank.
        Assert.Matches(@"^playfab-title-id\s+-$", lines[2]);
        Assert.DoesNotContain("\"values\"", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unset_TableShowsTheRemovedKeysWithNoValue()
    {
        await SaveAsync(Identity, GatewayBaseUrl, "1234567");

        var result = await InvokeAsync("context", "unset", "seller-id", "--output", "table");

        Assert.Equal(0, result.ExitCode);
        var lines = result.Stdout.TrimEnd().Split(Environment.NewLine);
        Assert.Equal(3, lines.Length);
        Assert.Matches(@"^seller-id\s+-$", lines[2]);
    }

    /// <summary>
    /// The view is declared once for every context command and has no generated model to be
    /// checked against, so it is checked against the JSON rows the commands write.
    /// </summary>
    [Fact]
    public async Task TableView_ReadsFieldsTheJsonRowsCarry()
    {
        var result = await InvokeAsync("context", "get", "--output", "json-compact");

        using var output = JsonDocument.Parse(result.Stdout);
        var view = ContextCommandFactory.TableView;
        var rows = output.RootElement.GetProperty(view.Collection).EnumerateArray().ToArray();
        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.All(view.Columns, column =>
            Assert.True(row.TryGetProperty(column.Property, out _), $"No '{column.Property}' in {row}.")));
    }

    /// <summary>
    /// A malformed file fails every key the same way, so the problem is said once, <c>get</c>
    /// still exits 0, and the file is left as it is for the user to fix.
    /// </summary>
    [Theory]
    [InlineData("not-json")]
    [InlineData("""{"version":1,"contexts":{"tenant:other@gateway.example":null}}""")]
    public async Task Get_MalformedContextFileIsReportedOnceAndLeftAsItIs(string content)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(StorePath, content);

        var result = await InvokeAsync("context", "get", "--output", "json-compact");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            """{"account":{"username":"dev@nodpublishers.com","tenantId":"tenant"},"profile":"default","values":[{"key":"playfab-title-id","value":null},{"key":"sandbox-id","value":null},{"key":"seller-id","value":null}]}""",
            result.Stdout.TrimEnd());
        var line = Assert.Single(result.Stderr.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains(StorePath, line, StringComparison.Ordinal);
        Assert.Contains("is not valid", line, StringComparison.Ordinal);
        Assert.Equal(content, await File.ReadAllTextAsync(StorePath));
    }

    [Fact]
    public async Task Get_MalformedContextFileIsReportedOnceForEveryKeyThatReadsIt()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(StorePath, "not-json");

        var result = await InvokeAsync("context", "get", "--output", "table");

        Assert.Equal(0, result.ExitCode);
        Assert.Single(result.Stderr.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains(StorePath, result.Stderr, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^seller-id\s+-\s*$", result.Stdout);
        Assert.Matches(@"(?m)^playfab-title-id\s+-\s*$", result.Stdout);
    }

    /// <summary>
    /// A context file another process holds open exclusively can't be read. That is reported once
    /// like a malformed file, and <c>get</c> still exits 0.
    /// </summary>
    [Fact]
    public async Task Get_UnreadableContextFileIsReportedOnce()
    {
        await SaveAsync(Identity, GatewayBaseUrl, "1234567", PlayFabEndpoint, "ABC12");

        CapturedInvocation result;
        await using (new FileStream(StorePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = await InvokeAsync("context", "get", "--output", "json-compact");
        }

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            """{"account":{"username":"dev@nodpublishers.com","tenantId":"tenant"},"profile":"default","values":[{"key":"playfab-title-id","value":null},{"key":"sandbox-id","value":null},{"key":"seller-id","value":null}]}""",
            result.Stdout.TrimEnd());
        var line = Assert.Single(result.Stderr.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains(StorePath, line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unset_RemovesOnlyTheNamedKey()
    {
        await SaveAsync(Identity, GatewayBaseUrl, "1234567", PlayFabEndpoint, "ABC12");

        var result = await InvokeAsync("context", "unset", "seller-id", "--output", "json-compact");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            """{"profile":"default","values":[{"key":"seller-id","value":null}]}""",
            result.Stdout.TrimEnd());
        Assert.Null(await _store.GetAsync(Identity, "seller-id", GatewayBaseUrl));
        Assert.Equal("ABC12", await _store.GetAsync(Identity, "playfab-title-id", PlayFabEndpoint));
        Assert.Equal(0, _signedIn.TokenRequests);
    }

    /// <summary>
    /// <c>--all</c> forgets the account's values in the profile, here the default one, everywhere:
    /// every endpoint, including keys this version doesn't know. Another account's context stays.
    /// </summary>
    [Fact]
    public async Task Unset_AllRemovesEverythingSavedForTheAccount()
    {
        var otherScope = ContextStore.CreateScope(new SignedInIdentity("tenant", "other-user"), GatewayBaseUrl);
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(StorePath, JsonSerializer.Serialize(new
        {
            version = 1,
            contexts = new Dictionary<string, Dictionary<string, string>>
            {
                [ContextStore.CreateScope(Identity, GatewayBaseUrl)] =
                    new() { ["seller-id"] = "1234567", ["future-key"] = "x" },
                [ContextStore.CreateScope(Identity, LoopbackGateway)] = new() { ["seller-id"] = "7654321" },
                [ContextStore.CreateScope(Identity, PlayFabEndpoint)] = new() { ["playfab-title-id"] = "ABC12" },
                [otherScope] = new() { ["seller-id"] = "2345678" },
            },
        }));

        var result = await InvokeAsync("context", "unset", "--all", "--output", "json-compact");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            """{"profile":"default","values":[{"key":"playfab-title-id","value":null},{"key":"sandbox-id","value":null},{"key":"seller-id","value":null}]}""",
            result.Stdout.TrimEnd());
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(StorePath));
        Assert.Equal(
            [otherScope],
            saved.RootElement.GetProperty("contexts").EnumerateObject().Select(entry => entry.Name));
        Assert.Equal(0, _signedIn.TokenRequests);
    }

    [Fact]
    public async Task Unset_KeyNamesAreCaseInsensitiveAndListedOnce()
    {
        await SaveAsync(Identity, GatewayBaseUrl, "1234567");

        var result = await InvokeAsync(
            "context", "unset", "SELLER-ID", "seller-id", "--output", "json-compact");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            """{"profile":"default","values":[{"key":"seller-id","value":null}]}""",
            result.Stdout.TrimEnd());
        Assert.Null(await _store.GetAsync(Identity, "seller-id", GatewayBaseUrl));
    }

    [Fact]
    public async Task Unset_RemovesTheSellerOnlyForTheNamedGateway()
    {
        await SaveAsync(Identity, GatewayBaseUrl, "1234567");
        await SaveAsync(Identity, LoopbackGateway, "7654321");

        var result = await InvokeAsync("context", "unset", "seller-id", "--base-url", LoopbackGateway);

        Assert.Equal(0, result.ExitCode);
        Assert.Null(await _store.GetAsync(Identity, "seller-id", LoopbackGateway));
        Assert.Equal("1234567", await _store.GetAsync(Identity, "seller-id", GatewayBaseUrl));
    }

    /// <summary>
    /// Removing a value that isn't saved succeeds and changes nothing, so scripts can run it
    /// unconditionally. It also leaves no context file behind where there was none.
    /// </summary>
    [Fact]
    public async Task Unset_IsIdempotent()
    {
        var nothingSaved = await InvokeAsync("context", "unset", "--all");

        Assert.Equal(0, nothingSaved.ExitCode);
        Assert.False(File.Exists(StorePath));

        await SaveAsync(Identity, GatewayBaseUrl, "1234567");
        var first = await InvokeAsync("context", "unset", "seller-id", "--output", "json-compact");
        var second = await InvokeAsync("context", "unset", "seller-id", "--output", "json-compact");

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        Assert.Equal(first.Stdout, second.Stdout);
        Assert.Empty(second.Stderr);
        Assert.Null(await _store.GetAsync(Identity, "seller-id", GatewayBaseUrl));
    }

    [Theory]
    [InlineData("Name a key to remove (playfab-title-id, sandbox-id, seller-id), or pass --all.")]
    [InlineData("Pass key names or --all, not both.", "seller-id", "--all")]
    [InlineData("Unknown context key 'title-id'. Valid keys: playfab-title-id, sandbox-id, seller-id.", "title-id")]
    [InlineData("Unknown context key 'title-id'. Valid keys: playfab-title-id, sandbox-id, seller-id.", "seller-id", "title-id")]
    public async Task Unset_InvalidKeysFailBeforeTheSignInIsRead(string expected, params string[] arguments)
    {
        await SaveAsync(Identity, GatewayBaseUrl, "1234567");

        var result = await InvokeAsync(["context", "unset", .. arguments]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Equal(expected + Environment.NewLine, result.Stderr);
        Assert.Equal(0, _signedIn.AccountRequests);
        Assert.Equal("1234567", await _store.GetAsync(Identity, "seller-id", GatewayBaseUrl));
    }

    [Theory]
    [InlineData("seller-id")]
    [InlineData("--all")]
    public async Task Unset_NotSignedInChangesNothing(string argument)
    {
        await SaveAsync(Identity, GatewayBaseUrl, "1234567");
        var before = await File.ReadAllTextAsync(StorePath);

        var result = await InvokeAsync(new FakeAuthenticationService(token: null), "context", "unset", argument);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Equal("Not signed in. Run 'xbox auth login' first." + Environment.NewLine, result.Stderr);
        Assert.Equal(before, await File.ReadAllTextAsync(StorePath));
    }

    /// <summary>
    /// <c>--profile</c> selects the saved context that <c>set</c>, <c>get</c>, and <c>unset</c> act
    /// on, and their output names it. Saving to a profile creates it, its <c>get</c> doesn't show
    /// the default profile's values, and the default profile is untouched throughout.
    /// </summary>
    [Fact]
    public async Task Profile_SetGetAndUnsetActOnlyOnTheSelectedProfile()
    {
        await SaveAsync(Identity, GatewayBaseUrl, "1234567", PlayFabEndpoint, "ABC12");

        var set = await InvokeAsync(
            "--profile", "Test", "context", "set", "playfab-title-id", "TEST1", "--output", "json-compact");
        var get = await InvokeAsync("context", "get", "--profile", "test", "--output", "json-compact");
        var unset = await InvokeAsync(
            "--profile", "test", "context", "unset", "playfab-title-id", "--output", "json-compact");

        Assert.Equal(
            """{"profile":"test","values":[{"key":"playfab-title-id","value":"TEST1"}]}""",
            set.Stdout.TrimEnd());
        Assert.Equal(
            """{"account":{"username":"dev@nodpublishers.com","tenantId":"tenant"},"profile":"test","values":[{"key":"playfab-title-id","value":"TEST1"},{"key":"sandbox-id","value":null},{"key":"seller-id","value":null}]}""",
            get.Stdout.TrimEnd());
        Assert.Equal(
            """{"profile":"test","values":[{"key":"playfab-title-id","value":null}]}""",
            unset.Stdout.TrimEnd());
        Assert.All(new[] { set, get, unset }, result =>
        {
            Assert.Equal(0, result.ExitCode);
            Assert.Empty(result.Stderr);
        });

        // Removing the profile's only value removed the profile.
        Assert.False(await _store.HasProfileAsync(Identity, "test"));
        Assert.Equal("1234567", await _store.GetAsync(Identity, "seller-id", GatewayBaseUrl));
        Assert.Equal("ABC12", await _store.GetAsync(Identity, "playfab-title-id", PlayFabEndpoint));
    }

    /// <summary>
    /// A profile the account has nothing saved in doesn't exist. Reading or clearing one is almost
    /// always a misspelling, so it fails instead of showing nothing or removing nothing as if that
    /// had worked.
    /// </summary>
    [Theory]
    [InlineData("context", "get")]
    [InlineData("context", "unset", "seller-id")]
    [InlineData("context", "unset", "--all")]
    public async Task MissingProfile_FailsAndChangesNothing(params string[] command)
    {
        await SaveAsync(Identity, GatewayBaseUrl, "1234567");
        var before = await File.ReadAllTextAsync(StorePath);

        var result = await InvokeAsync(["--profile", "missing", .. command]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Equal(ContextProfiles.NotFoundMessage("missing") + Environment.NewLine, result.Stderr);
        Assert.Equal(before, await File.ReadAllTextAsync(StorePath));
    }

    /// <summary>
    /// Without <c>--profile</c>, <c>XBOX_PROFILE</c> selects the profile that <c>set</c> and
    /// <c>get</c> act on; <c>--profile</c> still wins over it.
    /// </summary>
    [Fact]
    public async Task ProfileVariable_SelectsTheProfileUnlessTheOptionDoes()
    {
        await SaveAsync(Identity, GatewayBaseUrl, "1234567", PlayFabEndpoint, "ABC12");
        Environment.SetEnvironmentVariable(ProfileVariable, "Test");

        var set = await InvokeAsync("context", "set", "playfab-title-id", "TEST1", "--output", "json-compact");
        var get = await InvokeAsync("context", "get", "--output", "json-compact");
        var flag = await InvokeAsync("--profile", "default", "context", "get", "--output", "json-compact");

        Assert.Equal(
            """{"profile":"test","values":[{"key":"playfab-title-id","value":"TEST1"}]}""",
            set.Stdout.TrimEnd());
        Assert.Equal(
            """{"account":{"username":"dev@nodpublishers.com","tenantId":"tenant"},"profile":"test","values":[{"key":"playfab-title-id","value":"TEST1"},{"key":"sandbox-id","value":null},{"key":"seller-id","value":null}]}""",
            get.Stdout.TrimEnd());
        Assert.Equal(
            """{"account":{"username":"dev@nodpublishers.com","tenantId":"tenant"},"profile":"default","values":[{"key":"playfab-title-id","value":"ABC12"},{"key":"sandbox-id","value":null},{"key":"seller-id","value":"1234567"}]}""",
            flag.Stdout.TrimEnd());
        Assert.All(new[] { set, get, flag }, result =>
        {
            Assert.Equal(0, result.ExitCode);
            Assert.Empty(result.Stderr);
        });
    }

    /// <summary>
    /// An <c>XBOX_PROFILE</c> that isn't a valid name, or names a profile that doesn't exist, fails
    /// like the same <c>--profile</c> would, rather than quietly using the default profile.
    /// </summary>
    [Theory]
    [InlineData("a_b", "XBOX_PROFILE must be letters, digits, and hyphens, starting with a letter or digit, and at most 64 characters.")]
    [InlineData("missing", null)]
    public async Task ProfileVariable_ThatCantBeUsedFailsAndChangesNothing(string profile, string? expected)
    {
        await SaveAsync(Identity, GatewayBaseUrl, "1234567");
        var before = await File.ReadAllTextAsync(StorePath);
        Environment.SetEnvironmentVariable(ProfileVariable, profile);

        var get = await InvokeAsync("context", "get");
        var unset = await InvokeAsync("context", "unset", "seller-id");

        Assert.All(new[] { get, unset }, result =>
        {
            Assert.Equal(1, result.ExitCode);
            Assert.Empty(result.Stdout);
            Assert.Equal((expected ?? ContextProfiles.NotFoundMessage(profile)) + Environment.NewLine, result.Stderr);
        });
        Assert.Equal(before, await File.ReadAllTextAsync(StorePath));
    }

    /// <summary>
    /// The profile is what the context commands act on, so a malformed <c>XBOX_PROFILE</c> fails
    /// first and alone: before the sign-in is read, and before the sign-in hint.
    /// </summary>
    [Theory]
    [InlineData("context", "get")]
    [InlineData("context", "set", "seller-id", "1234567")]
    [InlineData("context", "unset", "seller-id")]
    public async Task ProfileVariable_MalformedFailsFirstAndAlone(params string[] command)
    {
        Environment.SetEnvironmentVariable(ProfileVariable, "a_b");

        var result = await InvokeAsync(new FakeAuthenticationService(token: null), command);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Equal(
            "XBOX_PROFILE must be letters, digits, and hyphens, starting with a letter or digit, "
            + "and at most 64 characters." + Environment.NewLine,
            result.Stderr);
        Assert.False(File.Exists(StorePath));
    }

    /// <summary>
    /// <c>unset --all</c> clears the selected profile on every endpoint and nothing else: the
    /// default profile and other profiles stay.
    /// </summary>
    [Fact]
    public async Task UnsetAll_ClearsOnlyTheSelectedProfile()
    {
        await SaveAsync(Identity, GatewayBaseUrl, "1234567");
        await SaveAsync(Identity, LoopbackGateway, "2222222", PlayFabEndpoint, "TEST1", "test");
        await SaveAsync(Identity, GatewayBaseUrl, "3333333", profile: "prod");

        var result = await InvokeAsync(
            "--profile", "test", "context", "unset", "--all", "--output", "json-compact");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            """{"profile":"test","values":[{"key":"playfab-title-id","value":null},{"key":"sandbox-id","value":null},{"key":"seller-id","value":null}]}""",
            result.Stdout.TrimEnd());
        Assert.False(await _store.HasProfileAsync(Identity, "test"));
        Assert.Equal("1234567", await _store.GetAsync(Identity, "seller-id", GatewayBaseUrl));
        Assert.Equal("3333333", await _store.GetAsync(Identity, "seller-id", GatewayBaseUrl, "prod"));
    }

    /// <summary>
    /// <c>profile list</c> shows the account's profiles: the default one first, which always
    /// exists, then each named one. Another account's profiles aren't listed.
    /// </summary>
    [Fact]
    public async Task ProfileList_ListsTheDefaultFirstThenTheAccountsNamedProfiles()
    {
        await SaveAsync(Identity, GatewayBaseUrl, "1234567", profile: "test");
        await SaveAsync(Identity, GatewayBaseUrl, "1234567", profile: "alpha");
        await SaveAsync(new SignedInIdentity("tenant", "other-user"), GatewayBaseUrl, "1234567", profile: "other");

        var json = await InvokeAsync("context", "profile", "list", "--output", "json-compact");
        var table = await InvokeAsync("context", "profile", "list", "--output", "table");

        Assert.Equal(0, json.ExitCode);
        Assert.Equal(
            """{"profiles":[{"name":"default"},{"name":"alpha"},{"name":"test"}]}""",
            json.Stdout.TrimEnd());
        Assert.Empty(json.Stderr);
        Assert.Equal(0, table.ExitCode);
        var lines = table.Stdout.TrimEnd().Split(Environment.NewLine);
        Assert.Matches(@"^NAME\s*$", lines[0]);
        Assert.Equal(["default", "alpha", "test"], lines[2..].Select(line => line.TrimEnd()));
        Assert.Equal(0, _signedIn.TokenRequests);
    }

    /// <summary>Listing and deleting profiles reads the account's saved context, so both need a sign-in.</summary>
    [Theory]
    [InlineData("list")]
    [InlineData("delete", "test")]
    public async Task ProfileCommands_NotSignedInAskForASignIn(params string[] command)
    {
        await SaveAsync(Identity, GatewayBaseUrl, "1234567", profile: "test");

        var result = await InvokeAsync(
            new FakeAuthenticationService(token: null),
            ["context", "profile", .. command]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Equal("Not signed in. Run 'xbox auth login' first." + Environment.NewLine, result.Stderr);
        Assert.True(await _store.HasProfileAsync(Identity, "test"));
    }

    /// <summary>
    /// <c>profile delete</c> removes the account's values in the profile on every endpoint, and
    /// only those: the default profile and another account's profile of the same name stay.
    /// </summary>
    [Fact]
    public async Task ProfileDelete_RemovesTheProfileOnEveryEndpoint()
    {
        var other = new SignedInIdentity("tenant", "other-user");
        await SaveAsync(Identity, GatewayBaseUrl, "1234567");
        await SaveAsync(Identity, LoopbackGateway, "2222222", PlayFabEndpoint, "TEST1", "test");
        await SaveAsync(other, GatewayBaseUrl, "3333333", profile: "test");

        var result = await InvokeAsync("context", "profile", "delete", "Test", "--output", "json-compact");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("""{"profiles":[{"name":"test"}]}""", result.Stdout.TrimEnd());
        Assert.Empty(result.Stderr);
        Assert.False(await _store.HasProfileAsync(Identity, "test"));
        Assert.True(await _store.HasProfileAsync(other, "test"));
        Assert.Equal("1234567", await _store.GetAsync(Identity, "seller-id", GatewayBaseUrl));
    }

    /// <summary>
    /// The default profile always exists, so it can't be deleted; the refusal says how to clear it
    /// instead, before the sign-in is read.
    /// </summary>
    [Theory]
    [InlineData("default")]
    [InlineData("DEFAULT")]
    public async Task ProfileDelete_RefusesTheDefaultProfile(string name)
    {
        await SaveAsync(Identity, GatewayBaseUrl, "1234567");

        var result = await InvokeAsync("context", "profile", "delete", name);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Equal(
            "The default profile can't be deleted. Run 'xbox context unset --all' to clear it."
            + Environment.NewLine,
            result.Stderr);
        Assert.Equal(0, _signedIn.AccountRequests);
        Assert.Equal("1234567", await _store.GetAsync(Identity, "seller-id", GatewayBaseUrl));
    }

    /// <summary>
    /// With <c>XBOX_PROFILE</c> picking another profile, <c>context unset --all</c> on its own
    /// would clear that one, so the suggestion names the default profile.
    /// </summary>
    [Fact]
    public async Task ProfileDelete_DefaultRefusalNamesTheProfileWhenXboxProfilePicksAnother()
    {
        Environment.SetEnvironmentVariable(ProfileVariable, "prod");

        var result = await InvokeAsync("context", "profile", "delete", "default");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(
            "The default profile can't be deleted. Run 'xbox --profile default context unset --all' "
            + "to clear it." + Environment.NewLine,
            result.Stderr);
    }

    /// <summary>A name that can't be a profile is rejected before the sign-in is read.</summary>
    [Theory]
    [InlineData("a_b", "The profile name must be letters, digits, and hyphens, starting with a letter or digit, and at most 64 characters.")]
    [InlineData(" ", "The profile name must not be blank.")]
    public async Task ProfileDelete_InvalidNameFailsBeforeTheSignInIsRead(string name, string expected)
    {
        var result = await InvokeAsync("context", "profile", "delete", name);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Equal(expected + Environment.NewLine, result.Stderr);
        Assert.Equal(0, _signedIn.AccountRequests);
    }

    [Fact]
    public async Task ProfileDelete_MissingProfileIsReportedAndNothingChanges()
    {
        await SaveAsync(Identity, GatewayBaseUrl, "1234567");
        var before = await File.ReadAllTextAsync(StorePath);

        var result = await InvokeAsync("context", "profile", "delete", "missing");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Equal(ContextProfiles.NotFoundMessage("missing") + Environment.NewLine, result.Stderr);
        Assert.Equal(before, await File.ReadAllTextAsync(StorePath));
    }

    /// <summary>A token cache that can't be used, built the way each failure shows up.</summary>
    private static FakeAuthenticationService CreateUnusableSignIn(string failure) => failure switch
    {
        "unreadable-cache" => new FakeAuthenticationService(FakeAuthenticationService.CreateToken())
        {
            CachedAccountException = new MsalClientException(
                "cache_read_failed",
                "The token cache could not be read."),
        },
        "invalid-cache" => new FakeAuthenticationService(FakeAuthenticationService.CreateToken())
        {
            CachedAccountException = new InvalidOperationException("The token cache is in an unexpected state."),
        },
        _ => new FakeAuthenticationService(token: null) { CachedAccount = Mock.Of<IAccount>() },
    };

    private async Task SaveAsync(
        SignedInIdentity identity,
        string sellerEndpoint,
        string sellerId,
        string? titleEndpoint = null,
        string? titleId = null,
        string profile = ContextProfiles.Default)
    {
        List<ContextChange> changes = [new ContextChange("seller-id", sellerEndpoint, sellerId)];
        if (titleEndpoint is not null)
        {
            changes.Add(new ContextChange("playfab-title-id", titleEndpoint, titleId));
        }

        await _store.UpdateAsync(identity, changes, profile);
    }

    /// <summary>
    /// A service principal has no saved context, so the saved-context commands say so instead of
    /// asking for a user sign-in, and never open the store. Listing keys stays available.
    /// </summary>
    [Theory]
    [InlineData("context", "get")]
    [InlineData("context", "set", "seller-id", "1234567")]
    [InlineData("context", "unset", "seller-id")]
    [InlineData("context", "profile", "list")]
    [InlineData("context", "profile", "delete", "test")]
    public async Task SavedContextCommands_AreRefusedInAutomation(params string[] args)
    {
        using var automation = TestEnvironment.UseAuthenticationMode("managedidentity");

        var result = await InvokeAsync(args);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains(
            "Saved context doesn't apply when XBOX_AUTHENTICATION__MODE is 'managedidentity'",
            result.Stderr,
            StringComparison.Ordinal);
        Assert.DoesNotContain("auth login", result.Stderr, StringComparison.Ordinal);
        Assert.Equal(0, _storeRequests);
        Assert.Equal(0, _signedIn.AccountRequests);
    }

    [Fact]
    public async Task KeyList_WorksInAutomation()
    {
        using var automation = TestEnvironment.UseAuthenticationMode("managedidentity");

        var result = await InvokeAsync("context", "key", "list", "--output", "json-compact");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("playfab-title-id", result.Stdout, StringComparison.Ordinal);
    }

    private Task<CapturedInvocation> InvokeAsync(params string[] args) => InvokeAsync(_signedIn, args);

    /// <summary>
    /// Runs a context command signed in through <paramref name="auth"/>, against the temporary
    /// store, counting how often the command asks for the store.
    /// </summary>
    private async Task<CapturedInvocation> InvokeAsync(FakeAuthenticationService auth, params string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);

        try
        {
            var root = Program.BuildRootCommand(
                contextCommandFactory: () => ContextCommandFactory.Create(
                    _ => auth,
                    () =>
                    {
                        _storeRequests++;
                        return _store;
                    }));
            var exitCode = await root.Parse(args, Program.NoResponseFileConfig).InvokeAsync();
            return new CapturedInvocation(exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private sealed record CapturedInvocation(int ExitCode, string Stdout, string Stderr);
}
