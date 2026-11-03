// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;
using Moq;
using XboxDevCLI.Safety;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

/// <summary>
/// The context resolver: between parsing and running, each marked option a command takes but
/// wasn't given gets <c>XBOX_&lt;NAME&gt;</c>, else the value saved under its name in the selected
/// profile, parsed as if typed. Commands that change something name every such value on stderr.
/// </summary>
/// <remarks>
/// Environment variables and stderr are process-global, so this shares the collection that
/// serializes every test that sets or redirects them.
/// </remarks>
[Collection("ConsoleRedirect")]
public sealed class ContextResolverTests : IDisposable
{
    private const string Scid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private static readonly SignedInIdentity Identity = new("tenant", "user");
    private static readonly string Gateway = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false)
        .Build()["XboxPartner:BaseUrl"]!;

    private static readonly string[] Variables =
    [
        "XBOX_SELLER_ID", "XBOX_PLAYFAB_TITLE_ID", "XBOX_SANDBOX_ID", "XBOX_PROFILE",
        "XBOX_CONFIRM", "XBOX_YES", "XBOX_BODY", "XBOX_BODY_FILE", "XBOX_PLAYFAB__BASEURLTEMPLATE",
        "XBOX_PLAYFAB__TITLEID",
    ];

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"xbox-context-resolver-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string?> _originalVariables =
        Variables.ToDictionary(name => name, name => Environment.GetEnvironmentVariable(name));
    private SignedInIdentity? _signedIn = Identity;
    private int _identityReads;

    public ContextResolverTests()
    {
        foreach (var name in Variables)
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        ContextStore.DefaultDirectory = () => _directory;
        ContextResolver.ReadIdentityAsync = _ =>
        {
            _identityReads++;
            return Task.FromResult<SignedInIdentity?>(_signedIn);
        };
    }

    public void Dispose()
    {
        foreach (var (name, value) in _originalVariables)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        Program.ApiClientFactory = null;
        TestEnvironment.Reset();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Variable_FillsAMissingOptionAsIfTyped()
    {
        Environment.SetEnvironmentVariable("XBOX_SELLER_ID", " 1234567 ");

        var (original, resolved, stderr) = await ResolveAsync("game", "list");

        Assert.NotSame(original, resolved);
        Assert.Equal("1234567", resolved.GetValue<string>("--seller-id"));
        Assert.Equal("", stderr);
        Assert.Equal(0, _identityReads);
    }

    /// <summary>A typed value wins, and nothing else is looked up.</summary>
    [Fact]
    public async Task Flag_WinsAndNothingIsLookedUp()
    {
        Environment.SetEnvironmentVariable("XBOX_SELLER_ID", "1234567");
        await SaveAsync("seller-id", Gateway, "7654321");

        var (original, resolved, _) = await ResolveAsync("game", "list", "--seller-id", "1111111");

        Assert.Same(original, resolved);
        Assert.Equal("1111111", resolved.GetValue<string>("--seller-id"));
        Assert.Equal(0, _identityReads);
    }

    [Theory]
    [InlineData("configuration", "get")]
    [InlineData("publish", "start")]
    [InlineData("publish", "status")]
    public async Task SandboxId_UsesFlagThenVariableThenSavedContext(string group, string action)
    {
        await SaveAsync("sandbox-id", Gateway, "TESTSB.42");
        string[] args = ["xbl", group, action, "--scid", Scid];

        var (_, saved, _) = await ResolveAsync(args);
        Assert.Empty(saved.Errors);
        Assert.Equal("TESTSB.42", saved.GetValue<string>("--sandbox-id"));

        Environment.SetEnvironmentVariable("XBOX_SANDBOX_ID", "XDKS.1");
        var (_, variable, _) = await ResolveAsync(args);
        Assert.Empty(variable.Errors);
        Assert.Equal("XDKS.1", variable.GetValue<string>("--sandbox-id"));

        var (_, explicitValue, _) = await ResolveAsync([.. args, "--sandbox-id", "ABCDEF.1"]);
        Assert.Empty(explicitValue.Errors);
        Assert.Equal("ABCDEF.1", explicitValue.GetValue<string>("--sandbox-id"));
    }

    /// <summary>
    /// A saved value belongs to the signed-in account and the endpoint the command talks to: the
    /// gateway, which <c>--base-url</c> moves, or the PlayFab endpoint. The account is read once.
    /// </summary>
    [Fact]
    public async Task SavedValue_BelongsToTheAccountAndEndpoint()
    {
        await SaveAsync("seller-id", Gateway, "7654321");
        await SaveAsync("sandbox-id", Gateway, "Dev.1");
        await SaveAsync("playfab-title-id", EndpointOverrideWarning.DefaultBaseUrlTemplate, "SAVED1");

        var (_, publish, _) = await ResolveAsync("xbl", "publish", "status", "--scid", Scid);
        var (_, title, _) = await ResolveAsync("playfab", "title-data", "get");
        var (_, moved, _) = await ResolveAsync("game", "list", "--base-url", "https://other.example");

        Assert.Empty(publish.Errors);
        Assert.Equal(7654321L, publish.GetValue<long?>("--seller-id"));
        Assert.Equal("Dev.1", publish.GetValue<string>("--sandbox-id"));
        Assert.Equal("SAVED1", title.GetValue(Program.PlayFabTitleIdOption));
        Assert.Null(moved.GetValue<string>("--seller-id"));
        Assert.Equal(3, _identityReads);
    }

    /// <summary>
    /// The account is read only when saved context holds one of the keys, and nothing is looked
    /// up for an invocation that can't run, or one with nothing to fill in.
    /// </summary>
    [Theory]
    [InlineData("game", "list")]
    [InlineData("game", "list", "--bogus")]
    [InlineData("auth", "status")]
    public async Task NothingToFillIn_ReadsNothing(params string[] args)
    {
        Environment.SetEnvironmentVariable("XBOX_PLAYFAB_TITLE_ID", "ABC12");
        await SaveAsync("playfab-title-id", EndpointOverrideWarning.DefaultBaseUrlTemplate, "SAVED1");
        if (args.Contains("--bogus"))
        {
            Environment.SetEnvironmentVariable("XBOX_SELLER_ID", "1234567");
        }

        var (original, resolved, stderr) = await ResolveAsync(args);

        Assert.Same(original, resolved);
        Assert.Equal("", stderr);
        Assert.Equal(0, _identityReads);
    }

    /// <summary>Values go ahead of <c>--</c>, after which everything is an argument.</summary>
    [Fact]
    public async Task Values_GoAheadOfDoubleDash()
    {
        Environment.SetEnvironmentVariable("XBOX_PLAYFAB_TITLE_ID", "ABC12");

        var (_, resolved, _) = await ResolveAsync("playfab", "api", "--", "/Admin/GetTitleData");

        Assert.Empty(resolved.Errors);
        Assert.Equal("ABC12", resolved.GetValue(Program.PlayFabTitleIdOption));
    }

    /// <summary>A value is parsed and validated as if typed, and fails naming where it came from.</summary>
    [Theory]
    [InlineData("XBOX_PLAYFAB_TITLE_ID", "ABC-12", "playfab title-data get", "XBOX_PLAYFAB_TITLE_ID: --playfab-title-id must contain only letters and digits.")]
    [InlineData("XBOX_SANDBOX_ID", "Dev 1", "xbl publish status --scid " + Scid, "XBOX_SANDBOX_ID: --sandbox-id must be non-empty and cannot contain whitespace.")]
    [InlineData("XBOX_SELLER_ID", "0", "xbl publish status --sandbox-id Dev.1 --scid " + Scid, "XBOX_SELLER_ID: --seller-id")]
    public async Task BadValue_FailsNamingItsSource(string variable, string value, string command, string expected)
    {
        Environment.SetEnvironmentVariable(variable, value);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ResolveAsync(command.Split(' ')));

        Assert.StartsWith(expected, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>context.json</c> can be edited by hand, so a saved value the option would reject fails
    /// the same way, before the command has a client to send anything with.
    /// </summary>
    [Fact]
    public async Task BadSavedValue_FailsNamingItsSource()
    {
        await SaveAsync("playfab-title-id", EndpointOverrideWarning.DefaultBaseUrlTemplate, "ABC-12");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ResolveAsync("playfab", "title-data", "get"));

        Assert.Equal("saved context: --playfab-title-id must contain only letters and digits.", exception.Message);
    }

    /// <summary>
    /// An explicitly blank or malformed <c>--playfab-title-id</c>, such as one from an unset
    /// <c>$TITLE</c>, is a parse error rather than no flag: nothing falls back to a saved title and
    /// the command never runs. A title with a line break would otherwise become a request header.
    /// </summary>
    [Theory]
    [InlineData("", "--playfab-title-id must contain at least one non-whitespace character.")]
    [InlineData("   ", "--playfab-title-id must contain at least one non-whitespace character.")]
    [InlineData("ABC-12", "--playfab-title-id must contain only letters and digits.")]
    [InlineData("ABC\r\nX-Probe: injected", "--playfab-title-id must contain only letters and digits.")]
    public async Task MalformedTitleFlag_IsAParseError(string title, string expected)
    {
        await SaveAsync("playfab-title-id", EndpointOverrideWarning.DefaultBaseUrlTemplate, "SAVED1");
        Program.ApiClientFactory = _ => throw new InvalidOperationException("The command ran.");

        var (exitCode, _, stderr) = await MainAsync("playfab", "title-data", "get", "--playfab-title-id", title);

        Assert.NotEqual(0, exitCode);
        Assert.Contains(expected, stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("The command ran.", stderr, StringComparison.Ordinal);
        Assert.Equal(0, _identityReads);
    }

    /// <summary>
    /// A command that calls PlayFab without a title fails before it runs, saying how to supply one
    /// in the profile it looked in; signed out, it asks for a sign-in, since saved values need an
    /// account.
    /// </summary>
    [Theory]
    [InlineData(true, null, "No playfab-title-id is set. Pass --playfab-title-id, set XBOX_PLAYFAB_TITLE_ID, or run 'xbox context set playfab-title-id <value>'.")]
    [InlineData(true, "test", "No playfab-title-id is set in profile test. Pass --playfab-title-id, set XBOX_PLAYFAB_TITLE_ID, or run 'xbox --profile test context set playfab-title-id <value>'.")]
    [InlineData(false, null, "Not signed in. Run 'xbox auth login' first.")]
    public async Task MissingTitle_SaysHowToSupplyOne(bool signedIn, string? profile, string expected)
    {
        if (profile is not null)
        {
            await SaveAsync("seller-id", Gateway, "1234567", profile);
            Environment.SetEnvironmentVariable("XBOX_PROFILE", profile);
        }

        _signedIn = signedIn ? Identity : null;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ResolveAsync("playfab", "title-data", "get"));

        Assert.Equal(expected, exception.Message);
    }

    /// <summary>
    /// A service principal has no saved context, so a missing title is reported with only the flag
    /// and variable, never a sign-in it can't do.
    /// </summary>
    [Fact]
    public async Task MissingTitle_InAutomationMode_NamesOnlyTheFlagAndVariable()
    {
        using var automation = TestEnvironment.UseAuthenticationMode("clientsecret");
        _signedIn = null;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ResolveAsync("playfab", "title-data", "get"));

        Assert.Equal(
            "No playfab-title-id is set. Pass --playfab-title-id or set XBOX_PLAYFAB_TITLE_ID.",
            exception.Message);
    }

    /// <summary>
    /// The title reaches the PlayFab client the way <c>--playfab-title-id</c> always has, through
    /// configuration, however it was supplied. appsettings.json and the setting's variable,
    /// <c>XBOX_PLAYFAB__TITLEID</c>, don't set it.
    /// </summary>
    [Theory]
    [InlineData("flag", "TITLE1")]
    [InlineData("variable", "TITLE1")]
    [InlineData("saved", "TITLE1")]
    [InlineData("configuration", null)]
    public async Task Title_ReachesTheClientThroughConfigurationOnly(string from, string? expected)
    {
        Environment.SetEnvironmentVariable("XBOX_PLAYFAB__TITLEID", "CONFIG1");
        Environment.SetEnvironmentVariable("XBOX_PLAYFAB_TITLE_ID", from == "variable" ? "TITLE1" : null);
        if (from == "saved")
        {
            await SaveAsync("playfab-title-id", EndpointOverrideWarning.DefaultBaseUrlTemplate, "TITLE1");
        }

        var resolved = from == "configuration"
            ? Program.BuildRootCommand().Parse(["playfab", "title-data", "get"], Program.NoResponseFileConfig)
            : (await ResolveAsync(from == "flag"
                ? new[] { "playfab", "title-data", "get", "--playfab-title-id", " TITLE1 " }
                : ["playfab", "title-data", "get"])).Resolved;

        Assert.Equal(expected, Program.BuildConfiguration(resolved)["PlayFab:TitleId"]);
    }

    /// <summary>Main stops with the reason, before the command runs, when a value can't be used.</summary>
    [Fact]
    public async Task Main_FailsBeforeTheCommandWhenAValueCantBeUsed()
    {
        Environment.SetEnvironmentVariable("XBOX_PLAYFAB_TITLE_ID", "ABC-12");

        var (exitCode, stdout, stderr) = await MainAsync("playfab", "title-data", "get");

        Assert.Equal(1, exitCode);
        Assert.Equal(
            "XBOX_PLAYFAB_TITLE_ID: --playfab-title-id must contain only letters and digits." + Environment.NewLine,
            stderr);
        Assert.Empty(stdout);
    }

    /// <summary>
    /// Reading the signed-in account needs valid sign-in settings. A malformed tenant, such as an
    /// empty <c>--tenant-id</c>, fails the command with the reason wherever the account is read:
    /// for an explicit <c>--profile</c>, for a saved value, and in <c>context</c> itself.
    /// </summary>
    [Theory]
    [InlineData("--profile", "test", "playfab", "title-data", "get")]
    [InlineData("game", "list")]
    [InlineData("context", "get")]
    public async Task MalformedTenant_FailsWithTheReasonWhereTheAccountIsRead(params string[] command)
    {
        await SaveAsync("seller-id", Gateway, "7654321");
        ContextResolver.ReadIdentityAsync = ContextResolver.ReadCachedIdentityAsync;

        var (exitCode, stdout, stderr) = await MainAsync(["--tenant-id", "", .. command]);

        Assert.Equal(1, exitCode);
        Assert.Empty(stdout);
        Assert.StartsWith("AzureAd:TenantId or AzureAd:ClientId is not valid: ", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// The title is filled in only for commands that call PlayFab, so the ones that don't work
    /// whatever a variable or the context file holds.
    /// </summary>
    [Theory]
    [InlineData("variable")]
    [InlineData("unreadable file")]
    [InlineData("invalid saved title")]
    public async Task CommandsThatDontCallPlayFab_IgnoreABadTitle(string problem)
    {
        if (problem == "variable")
        {
            Environment.SetEnvironmentVariable("XBOX_PLAYFAB_TITLE_ID", "ABC-12");
        }
        else if (problem == "unreadable file")
        {
            Directory.CreateDirectory(_directory);
            await File.WriteAllTextAsync(Path.Combine(_directory, ContextStore.FileName), "not-json");
        }
        else
        {
            await SaveAsync("playfab-title-id", EndpointOverrideWarning.DefaultBaseUrlTemplate, "ABC-12");
        }

        string[][] commands =
        [
            ["info"],
            ["context", "key", "list"],
        ];
        foreach (var command in commands)
        {
            var (exitCode, stdout, stderr) = await MainAsync(command);

            Assert.True(exitCode == 0, $"{string.Join(' ', command)}: {stderr}");
            Assert.NotEmpty(stdout);
            Assert.Equal("", stderr);
        }

        Assert.Equal(0, _identityReads);
    }

    /// <summary>A configuration that can't be read is left to the command to report.</summary>
    [Fact]
    public async Task UnreadableConfiguration_IsLeftToTheCommand()
    {
        var (original, resolved, stderr) = await ResolveAsync("--config-env", "missing", "playfab", "title-data", "get");

        Assert.Same(original, resolved);
        Assert.Equal("", stderr);
        Assert.Equal(0, _identityReads);
    }

    /// <summary>
    /// A command that changes something names each value it didn't get from its command line; a
    /// read does with <c>--verbose</c>. A variable that wins over the profile <c>--profile</c> asked
    /// for is named either way.
    /// </summary>
    [Theory]
    [InlineData("/Admin/SetTitleData", false, null, "Using --playfab-title-id ABC12 from XBOX_PLAYFAB_TITLE_ID.")]
    [InlineData("/Admin/GetTitleData", false, null, "")]
    [InlineData("/Admin/GetTitleData", true, null, "[verbose] Using --playfab-title-id ABC12 from XBOX_PLAYFAB_TITLE_ID.")]
    [InlineData("/Admin/GetTitleData", false, "test", "Using --playfab-title-id ABC12 from XBOX_PLAYFAB_TITLE_ID, not profile test.")]
    public async Task Sources_AreNamedOnStderr(string apiPath, bool verbose, string? profile, string expected)
    {
        Environment.SetEnvironmentVariable("XBOX_PLAYFAB_TITLE_ID", "ABC12");
        await SaveAsync("seller-id", Gateway, "7654321", "test");
        string[] options = [.. verbose ? ["--verbose"] : Array.Empty<string>(), .. profile is null ? [] : new[] { "--profile", profile }];

        var (_, _, stderr) = await ResolveAsync([.. options, "playfab", "api", apiPath]);

        Assert.Equal(expected.Length == 0 ? "" : expected + Environment.NewLine, stderr);
    }

    /// <summary>
    /// The profile is <c>--profile</c>, else <c>XBOX_PROFILE</c>, else the default, and a named
    /// profile never falls back to the default one.
    /// </summary>
    [Theory]
    [InlineData(null, "test", "TEST1", "Using --playfab-title-id TEST1 from profile test.")]
    [InlineData("test", "prod", "TEST1", "Using --playfab-title-id TEST1 from profile test.")]
    [InlineData(null, null, "DEF1", "Using --playfab-title-id DEF1 from saved context.")]
    public async Task Profile_SelectsTheSavedValue(string? flag, string? variable, string expected, string notice)
    {
        await SaveAsync("playfab-title-id", EndpointOverrideWarning.DefaultBaseUrlTemplate, "DEF1");
        await SaveAsync("playfab-title-id", EndpointOverrideWarning.DefaultBaseUrlTemplate, "TEST1", "test");
        await SaveAsync("playfab-title-id", EndpointOverrideWarning.DefaultBaseUrlTemplate, "PROD1", "prod");
        Environment.SetEnvironmentVariable("XBOX_PROFILE", variable);

        var (_, resolved, stderr) = await ResolveAsync(
            [.. flag is null ? [] : new[] { "--profile", flag }, "playfab", "api", "/Admin/SetTitleData"]);

        Assert.Equal(expected, resolved.GetValue(Program.PlayFabTitleIdOption));
        Assert.Equal(notice + Environment.NewLine, stderr);
    }

    [Theory]
    [InlineData(new[] { "playfab", "title-data", "get" }, "test", "No profile named 'test'.")]
    [InlineData(new[] { "--profile", "test", "playfab", "title-data", "get", "--playfab-title-id", "ABC12" }, null, "No profile named 'test'.")]
    [InlineData(new[] { "playfab", "title-data", "get" }, "-bad", "XBOX_PROFILE must be")]
    public async Task Profile_ThatCantBeUsedFails(string[] args, string? variable, string expected)
    {
        await SaveAsync("playfab-title-id", EndpointOverrideWarning.DefaultBaseUrlTemplate, "DEF1");
        Environment.SetEnvironmentVariable("XBOX_PROFILE", variable);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ResolveAsync(args));

        Assert.StartsWith(expected, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Signed out, there's no account to have profiles; the command fails on its sign-in.</summary>
    [Fact]
    public async Task Profile_IsNotCheckedWhenSignedOut()
    {
        _signedIn = null;

        var (original, resolved, _) = await ResolveAsync("--profile", "test", "playfab", "title-data", "get", "--playfab-title-id", "ABC12");

        Assert.Same(original, resolved);
    }

    /// <summary>
    /// A profile belongs to the account that saved to it, so another account's doesn't exist for
    /// this one, even when it holds none of the keys the command looks up.
    /// </summary>
    [Theory]
    [InlineData("--profile")]
    [InlineData("XBOX_PROFILE")]
    public async Task Profile_OfAnotherAccountFails(string selectedWith)
    {
        await new ContextStore().UpdateAsync(
            new SignedInIdentity("tenant", "other"),
            [new ContextChange("playfab-title-id", EndpointOverrideWarning.DefaultBaseUrlTemplate, "OTHER1")],
            "test");
        Environment.SetEnvironmentVariable("XBOX_PROFILE", selectedWith == "XBOX_PROFILE" ? "test" : null);
        string[] args = selectedWith == "--profile" ? ["--profile", "test", "sandbox", "list"] : ["sandbox", "list"];

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ResolveAsync(args));

        Assert.StartsWith("No profile named 'test'.", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An invocation acts as one account: the authentication service that read the account to
    /// fill in a saved value is the one the command gets its token from.
    /// </summary>
    [Fact]
    public async Task SavedValue_ResolutionAndCommandShareOneService()
    {
        await SaveAsync("playfab-title-id", EndpointOverrideWarning.DefaultBaseUrlTemplate, "SAVED1");
        AuthenticationService? reader = null;
        ContextResolver.ReadIdentityAsync = parseResult =>
        {
            reader = SignedInAs(() => "user", parseResult);
            return SignedInIdentity.FromCacheAsync(reader, CancellationToken.None);
        };

        var (original, resolved, _) = await ResolveAsync("playfab", "title-data", "get");

        Assert.Equal("SAVED1", resolved.GetValue(Program.PlayFabTitleIdOption));
        Assert.NotNull(reader);
        Assert.Same(reader, Program.GetAuthentication(original));
        Assert.Same(reader, Program.GetAuthentication(resolved));
    }

    /// <summary>
    /// Saved values belong to the account that read them. If another account signs in before the
    /// command gets its token, say while it waits for confirmation, the token request fails rather
    /// than send that account's token with them.
    /// </summary>
    [Fact]
    public async Task AccountSwitchAfterResolving_FailsTheTokenRequest()
    {
        await SaveAsync("playfab-title-id", EndpointOverrideWarning.DefaultBaseUrlTemplate, "SAVED1");
        var objectId = "user";
        ContextResolver.ReadIdentityAsync = parseResult =>
            SignedInIdentity.FromCacheAsync(SignedInAs(() => objectId, parseResult), CancellationToken.None);

        var (_, resolved, _) = await ResolveAsync("playfab", "title-data", "get");
        objectId = "other";

        Assert.Equal("SAVED1", resolved.GetValue(Program.PlayFabTitleIdOption));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Program.GetAuthentication(resolved).AcquireTokenSilentAsync(CancellationToken.None));
        Assert.Equal("The signed-in account changed while this command ran. Run it again.", exception.Message);
    }

    [Theory]
    [InlineData("game", "list")]
    [InlineData("game", "get", "1234ABCD")]
    [InlineData("game", "create", "--name", "Nod Arena", "--supported-device", "xbox", "--confirm")]
    [InlineData("game", "job", "get", "job-1")]
    public async Task GameCommands_DefaultClientRejectsAccountSwitchAfterResolvingSavedSeller(params string[] args)
    {
        await SaveAsync("seller-id", Gateway, "1234567");
        var objectId = "user";
        var accountReads = 0;
        ContextResolver.ReadIdentityAsync = parseResult =>
            SignedInIdentity.FromCacheAsync(
                SignedInAs(() =>
                {
                    accountReads++;
                    return objectId;
                }, parseResult),
                CancellationToken.None);

        var (_, resolved, _) = await ResolveAsync(
            [.. args, "--client-id", "00000000-0000-0000-0000-000000000000"]);
        objectId = "other";

        Assert.Equal("1234567", resolved.GetValue<string>("--seller-id"));
        Assert.Equal(1, accountReads);

        var originalOut = Console.Out;
        using var stdout = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            var exitCode = 0;
            var stderr = await CaptureStderrAsync(async () => exitCode = await resolved.InvokeAsync());

            Assert.Equal(1, exitCode);
            Assert.Empty(stdout.ToString());
            Assert.Contains(
                "The signed-in account changed while this command ran. Run it again.",
                stderr, StringComparison.Ordinal);
            Assert.Equal(2, accountReads);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    /// <summary>
    /// The invocation that runs shares the configuration snapshot it was parsed with, plus the
    /// title: nothing is reloaded, and the endpoint warning is written once.
    /// </summary>
    [Fact]
    public async Task Configuration_IsSharedAndWarnsOnce()
    {
        Environment.SetEnvironmentVariable("XBOX_PLAYFAB__BASEURLTEMPLATE", "https://{0}.example.test");
        Environment.SetEnvironmentVariable("XBOX_PLAYFAB_TITLE_ID", "ABC12");
        string[] args = ["playfab", "title-data", "get"];
        var original = Program.BuildRootCommand().Parse(args, Program.NoResponseFileConfig);
        var stderr = await CaptureStderrAsync(async () =>
        {
            Assert.Null(Program.BuildConfiguration(original)["PlayFab:TitleId"]);
            Environment.SetEnvironmentVariable("XBOX_PLAYFAB__BASEURLTEMPLATE", "https://{0}.changed.test");
            var configuration = Program.BuildConfiguration(await ContextResolver.ResolveAsync(original, args));
            Assert.Equal("https://{0}.example.test", configuration[EndpointOverrideWarning.ConfigurationKey]);
            Assert.Equal("ABC12", configuration["PlayFab:TitleId"]);
        });

        Assert.Single(stderr.Split(Environment.NewLine), line => line.StartsWith("Warning:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeferredAuthentication_UsesConfigurationLoadedAfterContextReparse()
    {
        using var automation = TestEnvironment.UseAuthenticationMode("managedidentity");
        Environment.SetEnvironmentVariable("XBOX_PLAYFAB__BASEURLTEMPLATE", "https://{0}.example.test");
        Environment.SetEnvironmentVariable("XBOX_PLAYFAB_TITLE_ID", "ABC123");
        string[] args = ["playfab", "title-data", "get", "--scopes", "api://nodpublishers.com/.default"];
        var original = Program.BuildRootCommand().Parse(args, Program.NoResponseFileConfig);
        var auth = Program.GetAuthentication(original);
        var stderr = await CaptureStderrAsync(async () =>
        {
            var resolved = await ContextResolver.ResolveAsync(original, args);
            Assert.NotSame(original, resolved);
            Assert.Same(auth, Program.GetAuthentication(resolved));
            Assert.Equal("ABC123", Program.BuildConfiguration(resolved)["PlayFab:TitleId"]);
            Environment.SetEnvironmentVariable("XBOX_PLAYFAB__BASEURLTEMPLATE", "https://{0}.changed.test");

            Assert.True(auth.IsAutomation);
            Assert.Equal("https://{0}.example.test",
                Program.BuildConfiguration(original)[EndpointOverrideWarning.ConfigurationKey]);
        });

        Assert.Single(stderr.Split(Environment.NewLine), line => line.StartsWith("Warning:", StringComparison.Ordinal));
    }

    /// <summary>
    /// Only the seller, PlayFab title and XBOX sandbox resolve, so nothing fills in
    /// <c>--confirm</c>, a request body, or any option that hasn't opted in.
    /// </summary>
    [Fact]
    public async Task OnlyMarkedOptionsResolve()
    {
        Assert.Equal(
            ["--playfab-title-id", "--sandbox-id", "--seller-id"],
            ContextResolver.FindAll(Program.BuildRootCommand()).Select(found => found.Option.Name).Distinct().Order());
        Assert.All(
            ContextResolver.FindAll(Program.BuildRootCommand()).Where(found => found.Option.Name == "--sandbox-id"),
            found => Assert.Contains(found.Command.Parents.OfType<Command>(), parent => parent.Name == "publish" || parent.Name == "configuration"));

        Environment.SetEnvironmentVariable("XBOX_CONFIRM", "true");
        Environment.SetEnvironmentVariable("XBOX_YES", "true");
        Environment.SetEnvironmentVariable("XBOX_BODY", "{}");
        Environment.SetEnvironmentVariable("XBOX_BODY_FILE", "body.json");
        var (original, resolved, _) = await ResolveAsync(
            "playfab", "api", "/Admin/DeleteTitle", "--playfab-title-id", "ABC12");

        Assert.Same(original, resolved);
        Assert.False(resolved.GetValue<bool>("--confirm"));
        Assert.Null(resolved.GetValue<string>("--body"));
    }

    [Theory]
    [InlineData("clientsecret", "--client-id", "XBOX_AUTHENTICATION__CLIENTID")]
    [InlineData("clientsecret", "--tenant-id", "XBOX_AUTHENTICATION__TENANTID")]
    [InlineData("certificate", "--client-id", "XBOX_AUTHENTICATION__CLIENTID")]
    [InlineData("certificate", "--tenant-id", "XBOX_AUTHENTICATION__TENANTID")]
    [InlineData("federatedtoken", "--client-id", "XBOX_AUTHENTICATION__CLIENTID")]
    [InlineData("federatedtoken", "--tenant-id", "XBOX_AUTHENTICATION__TENANTID")]
    [InlineData("githuboidc", "--client-id", "XBOX_AUTHENTICATION__CLIENTID")]
    [InlineData("githuboidc", "--tenant-id", "XBOX_AUTHENTICATION__TENANTID")]
    [InlineData("azurecli", "--client-id", "signing in to the Azure CLI")]
    [InlineData("azurecli", "--tenant-id", "signing in to the Azure CLI")]
    [InlineData("managedidentity", "--client-id", "XBOX_AUTHENTICATION__CLIENTID")]
    [InlineData("managedidentity", "--tenant-id", "managed identity determines the tenant")]
    public async Task Automation_RefusesUserSignInOptions(string mode, string option, string guidance)
    {
        using var automation = TestEnvironment.UseAuthenticationMode(mode);

        var (exitCode, stdout, stderr) = await MainAsync(option, "ignored-value", "info");

        Assert.Equal(1, exitCode);
        Assert.Empty(stdout);
        Assert.StartsWith(
            $"{option} doesn't apply when XBOX_AUTHENTICATION__MODE is '{mode}'. Remove {option}. ",
            stderr,
            StringComparison.Ordinal);
        Assert.Contains(guidance, stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("ignored-value", stderr, StringComparison.Ordinal);
        Assert.Equal(0, _identityReads);
    }

    [Theory]
    [InlineData("--client-id", "json")]
    [InlineData("--client-id", "json-compact")]
    [InlineData("--tenant-id", "json")]
    [InlineData("--tenant-id", "json-compact")]
    public async Task Automation_AuthStatusRefusesUserSignInOptionsAsJson(string option, string format)
    {
        using var automation = TestEnvironment.UseAuthenticationMode("clientsecret");

        var (exitCode, stdout, stderr) = await MainAsync(
            "auth", "status", option, "", "--output", format);

        Assert.Equal(1, exitCode);
        Assert.Empty(stdout);
        using var json = JsonDocument.Parse(stderr);
        Assert.False(json.RootElement.GetProperty("signedIn").GetBoolean());
        Assert.StartsWith(
            $"{option} doesn't apply when XBOX_AUTHENTICATION__MODE is 'clientsecret'",
            json.RootElement.GetProperty("error").GetString(),
            StringComparison.Ordinal);
        Assert.Equal(0, _identityReads);
    }

    [Theory]
    [InlineData("--client-id ignored-value --help", 0)]
    [InlineData("--tenant-id ignored-value help auth status", 0)]
    [InlineData("--client-id ignored-value auth", 0)]
    [InlineData("--tenant-id ignored-value", 0)]
    [InlineData("--client-id ignored-value --version", 0)]
    [InlineData("--tenant-id ignored-value auth status --nope", 1)]
    [InlineData("--client-id ignored-value auth status --output invalid", 1)]
    [InlineData("auth status --tenant-id", 1)]
    public async Task Automation_UserSignInOptionRefusalYieldsToHelpVersionAndParseErrors(
        string command, int expectedExit)
    {
        using var automation = TestEnvironment.UseAuthenticationMode("azurecli");

        var (exitCode, stdout, stderr) = await MainAsync(command.Split(' '));

        Assert.Equal(expectedExit, exitCode);
        Assert.DoesNotContain("doesn't apply when", stderr, StringComparison.Ordinal);
        Assert.True(expectedExit == 0 ? !string.IsNullOrEmpty(stdout) : !string.IsNullOrEmpty(stderr));
        Assert.Equal(0, _identityReads);
    }

    [Fact]
    public async Task UserSignIn_KeepsClientAndTenantOverrides()
    {
        var (_, resolved, _) = await ResolveAsync(
            "--client-id", "custom-client", "--tenant-id", "custom-tenant", "info");

        var configuration = Program.BuildConfiguration(resolved);
        Assert.Equal("custom-client", configuration["AzureAd:ClientId"]);
        Assert.Equal("custom-tenant", configuration["AzureAd:TenantId"]);
    }

    [Fact]
    public async Task Automation_KeepsScopeOverrides()
    {
        using var automation = TestEnvironment.UseAuthenticationMode("azurecli");

        var (_, resolved, _) = await ResolveAsync("--scopes", "api://example/.default", "info");

        Assert.Equal("api://example/.default", Program.BuildConfiguration(resolved)["AzureAd:Scopes"]);
    }

    /// <summary>
    /// A service principal has no saved context, so a profile from <c>--profile</c> or
    /// <c>XBOX_PROFILE</c> is refused rather than silently ignored, even by a command that takes
    /// no context value.
    /// </summary>
    [Theory]
    [InlineData("--profile", "Remove --profile.", "playfab title-data get --playfab-title-id ABC12")]
    [InlineData("XBOX_PROFILE", "Unset XBOX_PROFILE.", "playfab title-data get --playfab-title-id ABC12")]
    [InlineData("--profile", "Remove --profile.", "info")]
    [InlineData("XBOX_PROFILE", "Unset XBOX_PROFILE.", "info")]
    public async Task Automation_RefusesAProfile(string selectedWith, string guidance, string command)
    {
        using var automation = TestEnvironment.UseAuthenticationMode("azurecli");
        Environment.SetEnvironmentVariable("XBOX_PROFILE", selectedWith == "XBOX_PROFILE" ? "test" : null);
        string[] args = selectedWith == "--profile"
            ? ["--profile", "test", .. command.Split(' ')]
            : command.Split(' ');

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ResolveAsync(args));

        Assert.StartsWith(
            $"{selectedWith} doesn't apply when XBOX_AUTHENTICATION__MODE is 'azurecli'",
            exception.Message,
            StringComparison.Ordinal);
        Assert.EndsWith(guidance, exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, _identityReads);
    }

    /// <summary>
    /// <c>auth status</c> refuses a profile in a service-principal mode from its own action, so
    /// through <c>Program.Main</c> JSON output keeps its error shape on stderr.
    /// </summary>
    [Theory]
    [InlineData("--profile", "Remove --profile.")]
    [InlineData("XBOX_PROFILE", "Unset XBOX_PROFILE.")]
    public async Task Automation_AuthStatusRefusesAProfileAsJson(string selectedWith, string guidance)
    {
        using var automation = TestEnvironment.UseAuthenticationMode("azurecli");
        Environment.SetEnvironmentVariable("XBOX_PROFILE", selectedWith == "XBOX_PROFILE" ? "test" : null);
        string[] args = selectedWith == "--profile"
            ? ["--profile", "test", "auth", "status", "--output", "json-compact"]
            : ["auth", "status", "--output", "json-compact"];

        var (exitCode, stdout, stderr) = await MainAsync(args);

        Assert.Equal(1, exitCode);
        Assert.Empty(stdout);
        using var json = JsonDocument.Parse(stderr);
        Assert.False(json.RootElement.GetProperty("signedIn").GetBoolean());
        var error = json.RootElement.GetProperty("error").GetString();
        Assert.StartsWith(
            $"{selectedWith} doesn't apply when XBOX_AUTHENTICATION__MODE is 'azurecli'",
            error,
            StringComparison.Ordinal);
        Assert.EndsWith(guidance, error, StringComparison.Ordinal);
        Assert.Equal(0, _identityReads);
    }

    /// <summary>
    /// In a service-principal mode a saved value is never read; a missing title names only the
    /// flag and variable, and the mode variable matches case-insensitively.
    /// </summary>
    [Fact]
    public async Task Automation_NeverReadsSavedContext()
    {
        await SaveAsync("playfab-title-id", EndpointOverrideWarning.DefaultBaseUrlTemplate, "SAVED1");
        using var automation = TestEnvironment.UseAuthenticationMode("AzureCli");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ResolveAsync("playfab", "title-data", "get"));

        Assert.Equal(
            "No playfab-title-id is set. Pass --playfab-title-id or set XBOX_PLAYFAB_TITLE_ID.",
            exception.Message);
        Assert.Equal(0, _identityReads);
    }

    /// <summary>
    /// Flags and variables still resolve in a service-principal mode, their source is still
    /// named, and a destructive operation still refuses a target its command line didn't name.
    /// </summary>
    [Fact]
    public async Task Automation_KeepsVariablesSourcesAndTargetSafeguards()
    {
        using var automation = TestEnvironment.UseAuthenticationMode("azurecli");
        Environment.SetEnvironmentVariable("XBOX_PLAYFAB_TITLE_ID", "ABC12");

        var (_, resolved, stderr) = await ResolveAsync("playfab", "api", "/Admin/SetTitleData");
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ResolveAsync("playfab", "api", "/Admin/DeleteTitle", "--confirm"));

        Assert.Equal("ABC12", resolved.GetValue(Program.PlayFabTitleIdOption));
        Assert.Equal("Using --playfab-title-id ABC12 from XBOX_PLAYFAB_TITLE_ID." + Environment.NewLine, stderr);
        Assert.Contains("--playfab-title-id", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, _identityReads);
    }

    [Fact]
    public void Describe_AddsWhereAMarkedOptionDefaultsFrom()
    {
        Assert.Equal(
            "Target this PlayFab title. Defaults to XBOX_PLAYFAB_TITLE_ID, then the value saved by 'context set playfab-title-id'.",
            ContextResolver.Describe(Program.PlayFabTitleIdOption));
        Assert.Equal(Program.VerboseOption.Description, ContextResolver.Describe(Program.VerboseOption));
    }

    /// <summary>
    /// The account comes from the token cache, never from a token, so a session whose refresh
    /// token has expired still finds its saved context, without a network request.
    /// </summary>
    [Fact]
    public async Task SignedInIdentity_FromCacheAsyncNeedsNoToken()
    {
        var auth = new FakeAuthenticationService(token: null)
        {
            CachedAccount = FakeAuthenticationService.CreateAccount("home-tenant", "object-id", "dev@nodpublishers.com"),
        };

        var identity = await SignedInIdentity.FromCacheAsync(auth, CancellationToken.None);

        Assert.Equal(new SignedInIdentity("home-tenant", "object-id", "dev@nodpublishers.com"), identity);
        Assert.Equal(1, auth.AccountRequests);
        Assert.Equal(0, auth.TokenRequests);
    }

    [Fact]
    public async Task SignedInIdentity_FromCacheAsyncIsNullWhenNobodyIsSignedIn()
    {
        var auth = new FakeAuthenticationService(token: null);

        Assert.Null(await SignedInIdentity.FromCacheAsync(auth, CancellationToken.None));
        Assert.Equal(0, auth.TokenRequests);
    }

    [Theory]
    [InlineData("", "user")]
    [InlineData("tenant", " ")]
    public void SignedInIdentity_AccountWithoutATenantOrUserAsksForANewSignIn(string tenantId, string objectId)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => SignedInIdentity.FromAccount(FakeAuthenticationService.CreateAccount(tenantId, objectId)));

        Assert.Contains("auth login", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SignedInIdentity_AccountWithoutAHomeAccountIdAsksForANewSignIn()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => SignedInIdentity.FromAccount(Mock.Of<IAccount>(account => account.Username == "dev@nodpublishers.com")));

        Assert.Equal(
            "The cached sign-in does not identify a tenant and user. Run 'xbox auth login' again.",
            exception.Message);
    }

    private Task SaveAsync(string key, string endpoint, string value, string profile = ContextProfiles.Default) =>
        new ContextStore().UpdateAsync(Identity, [new ContextChange(key, endpoint, value)], profile);

    /// <summary>
    /// Makes the real authentication service the invocation of <paramref name="parseResult"/>
    /// uses, over a token cache holding one account of the test tenant, whichever
    /// <paramref name="objectId"/> names when it's read. Its MSAL client is a mock, so no token
    /// is sent for.
    /// </summary>
    private static AuthenticationService SignedInAs(Func<string> objectId, ParseResult parseResult)
    {
        var app = new Mock<IPublicClientApplication>();
        app.SetupGet(application => application.UserTokenCache).Returns(Mock.Of<ITokenCache>());
        app.Setup(application => application.GetAccountsAsync())
            .ReturnsAsync(() => [FakeAuthenticationService.CreateAccount("tenant", objectId())]);
        var service = new AuthenticationService(Program.BuildConfiguration(parseResult), app.Object);
        Program.SetAuthentication(parseResult, service);
        return service;
    }

    private static async Task<(ParseResult Original, ParseResult Resolved, string Stderr)> ResolveAsync(params string[] args)
    {
        var original = Program.BuildRootCommand().Parse(args, Program.NoResponseFileConfig);
        ParseResult? resolved = null;
        var stderr = await CaptureStderrAsync(async () => resolved = await ContextResolver.ResolveAsync(original, args));
        return (original, resolved!, stderr);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> MainAsync(params string[] args)
    {
        var originalOut = Console.Out;
        using var stdout = new StringWriter();
        Console.SetOut(stdout);
        var exitCode = 0;
        try
        {
            var stderr = await CaptureStderrAsync(async () => exitCode = await Program.Main(args));
            return (exitCode, stdout.ToString(), stderr);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    private static async Task<string> CaptureStderrAsync(Func<Task> action)
    {
        var originalError = Console.Error;
        using var stderr = new StringWriter();
        Console.SetError(stderr);
        try
        {
            await action();
        }
        finally
        {
            Console.SetError(originalError);
        }

        return stderr.ToString();
    }
}
