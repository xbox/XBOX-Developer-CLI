// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.RegularExpressions;
using XboxDevCLI.CommandLine;
using XboxDevCLI.PlayFab.Commands;
using Xunit;

namespace XboxDevCLI.UnitTest;

/// <summary>
/// Guards the canonical command surface. These tests are deliberately expressed as
/// explicit path assertions rather than an approval snapshot: a snapshot would fail as
/// one opaque blob on any change, whereas these state which path is expected and why,
/// and separate "the canonical path exists" from "the retired path is gone".
/// </summary>
[Collection("ConsoleRedirect")]
public class CommandSurfaceTests
{
    /// <summary>
    /// Every canonical path introduced or preserved by the canonicalization pass. Each
    /// entry must resolve to a real command in the tree.
    /// </summary>
    [Theory]
    [InlineData("playfab api")]
    [InlineData("playfab file list")]
    [InlineData("playfab group get")]
    [InlineData("playfab group membership opportunity list")]
    [InlineData("playfab player account get")]
    [InlineData("playfab api-policy get")]
    [InlineData("playfab cloudscript classic revision get")]
    [InlineData("playfab cloudscript classic revision create")]
    [InlineData("playfab cloudscript classic revision publish")]
    [InlineData("playfab cloudscript classic version list")]
    [InlineData("playfab cloudscript function list")]
    [InlineData("playfab cloudscript function register")]
    [InlineData("playfab cloudscript function unregister")]
    [InlineData("playfab economy inventory list")]
    [InlineData("playfab economy inventory add")]
    [InlineData("playfab economy inventory collection list")]
    [InlineData("playfab economy inventory transaction list")]
    [InlineData("playfab economy inventory operation execute")]
    [InlineData("playfab economy inventory operation status")]
    [InlineData("playfab experiment list")]
    [InlineData("playfab experiment scorecard get")]
    [InlineData("playfab experiment treatment get")]
    [InlineData("playfab experiment exclusion-group list")]
    [InlineData("playfab mps title status")]
    [InlineData("playfab mps container-registry credentials get")]
    [InlineData("playfab title-data get")]
    [InlineData("playfab player-data get")]
    [InlineData("game get")]
    [InlineData("game list")]
    [InlineData("seller list")]
    [InlineData("context get")]
    [InlineData("context set")]
    [InlineData("context unset")]
    [InlineData("sandbox list")]
    [InlineData("sandbox get")]
    [InlineData("sandbox create")]
    [InlineData("pc sandbox get")]
    [InlineData("pc sandbox set")]
    [InlineData("pc sandbox launch-apps")]
    [InlineData("test-account batch list")]
    [InlineData("test-account batch get")]
    [InlineData("xbl schema get")]
    [InlineData("xbl document download")]
    [InlineData("xbl document commit")]
    [InlineData("xbl publish start")]
    [InlineData("xbl publish status")]
    public void CanonicalPath_ResolvesToACommand(string path)
    {
        var root = Program.BuildRootCommand();
        var segments = path.Split(' ');

        Assert.True(TryResolveRegisteredPath(root, segments, out var command));
        Assert.Equal(segments[^1], command!.Name);
    }

    /// <summary>
    /// The repository is pre-public, so retired spellings are removed outright rather than
    /// aliased. Each must now fail closed: contextual error on stderr, nonzero exit, and no
    /// stdout that a script could mistake for a successful result.
    /// </summary>
    [Theory]
    [InlineData("playfab", "raw-api", "/Admin/GetTitleData")]
    [InlineData("playfab", "list-apis")]
    [InlineData("playfab", "describe-api", "/Admin/GetTitleData")]
    [InlineData("product", "list")]
    [InlineData("playfab", "describe-type", "CatalogItem")]
    [InlineData("playfab", "type", "describe", "EntityKey")]
    [InlineData("playfab", "type")]
    [InlineData("playfab", "files", "list")]
    [InlineData("playfab", "groups", "get")]
    [InlineData("playfab", "api-policy", "show")]
    [InlineData("playfab", "player", "get-account")]
    [InlineData("playfab", "files")]
    [InlineData("playfab", "groups")]
    [InlineData("playfab", "groups", "membership", "list-opportunities")]
    [InlineData("playfab", "cloudscript", "functions")]
    [InlineData("playfab", "cloudscript", "functions", "list")]
    [InlineData("playfab", "cloudscript", "functions", "register-http")]
    [InlineData("playfab", "cloudscript", "classic", "update")]
    [InlineData("playfab", "cloudscript", "classic", "publish")]
    [InlineData("playfab", "cloudscript", "classic", "get-revision")]
    [InlineData("playfab", "cloudscript", "classic", "list-versions")]
    [InlineData("playfab", "economy", "inventory", "collections")]
    [InlineData("xbox-live")]
    [InlineData("xbox-live", "schema", "get")]
    [InlineData("xbl", "sandboxes", "list")]
    [InlineData("xbl", "sandboxes")]
    [InlineData("xbl", "schemas")]
    [InlineData("xbl", "schemas", "get")]
    [InlineData("xbl", "documents")]
    [InlineData("xbl", "documents", "download")]
    [InlineData("xbl", "documents", "commit")]
    [InlineData("schema", "get")]
    [InlineData("configuration", "document", "download")]
    [InlineData("configuration", "document", "commit")]
    [InlineData("product", "publish")]
    [InlineData("product", "status")]
    [InlineData("playfab", "economy", "inventory", "history")]
    [InlineData("playfab", "economy", "inventory", "ops")]
    [InlineData("playfab", "economy", "inventory", "status")]
    [InlineData("playfab", "mps", "title", "get-status")]
    [InlineData("seller", "set", "1234567")]
    [InlineData("seller", "show")]
    public async Task RetiredPath_FailsClosed(params string[] args)
    {
        var result = await InvokeAsync(args);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Contains("unknown command", result.Stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>playfab api</c> takes the API path directly, like <c>gh api</c>, so it has no
    /// subcommands. A path after one of its former action words is an extra argument, which the
    /// parser rejects before anything is called.
    /// </summary>
    [Theory]
    [InlineData("playfab", "api", "call", "/Admin/GetTitleData")]
    [InlineData("playfab", "api", "describe", "/Admin/GetTitleData")]
    [InlineData("playfab", "api", "list", "Admin")]
    public async Task ApiActionWords_AreNotCommands(params string[] args)
    {
        var root = Program.BuildRootCommand();
        Assert.True(TryResolveRegisteredPath(root, ["playfab", "api"], out var api));
        Assert.Empty(api!.Subcommands);

        var result = await InvokeAsync(args);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains($"Unrecognized command or argument '{args[^1]}'", result.Stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// A former action word on its own would otherwise be sent to PlayFab as a path. The path
    /// must start with '/', so these fail at parse instead.
    /// </summary>
    [Theory]
    [InlineData("list")]
    [InlineData("call")]
    [InlineData("describe")]
    [InlineData("Admin/GetTitleData")]
    public async Task ApiPath_WithoutLeadingSlash_IsRefusedAtParse(string path)
    {
        var parsed = Program.BuildRootCommand().Parse(["playfab", "api", path], Program.NoResponseFileConfig);
        Assert.Contains(parsed.Errors, error => error.Message.Contains("must start with '/'", StringComparison.Ordinal));

        var result = await InvokeAsync(["playfab", "api", path]);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("must start with '/'", result.Stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// Git Bash converts <c>/Catalog/GetCatalogConfig</c> to a path under its install directory
    /// before the CLI starts. The refusal names the path the user typed and how to keep it, and
    /// is still a refusal, so a converted Delete path can't reach the client either.
    /// </summary>
    [Theory]
    [InlineData("C:/Program Files/Git/Catalog/GetCatalogConfig", "/Catalog/GetCatalogConfig", "/Catalog/GetCatalogConfig")]
    [InlineData("C:/Program Files/Git/Admin/DeleteTitle", "/Admin/DeleteTitle", "/Admin/DeleteTitle")]
    [InlineData("C:/Program Files/Git/Catalog/Get Catalog", "/Catalog/Get Catalog", "'/Catalog/Get Catalog'")]
    public void ApiPath_ConvertedByGitBash_IsRefusedWithTheOriginalPath(string argument, string originalPath, string quoted)
    {
        var api = ApiCommandFactory.Create(directory => directory == "C:/Program Files/Git");

        var parsed = api.Parse([argument]);

        var error = Assert.Single(parsed.Errors);
        Assert.Contains($"must start with '/', such as /Admin/GetTitleData; got '{argument}'.", error.Message, StringComparison.Ordinal);
        Assert.Contains($"That looks like {originalPath} after Git Bash converted it", error.Message, StringComparison.Ordinal);
        Assert.Contains($"MSYS_NO_PATHCONV=1 xbox playfab api {quoted}", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ApiPath_DrivePathOutsideAnMsysRoot_KeepsThePlainRefusal()
    {
        var api = ApiCommandFactory.Create(_ => false);

        var parsed = api.Parse(["C:/Users/dev/AppData/Local/Temp/foo"]);

        var error = Assert.Single(parsed.Errors);
        Assert.Equal(
            "The API path must start with '/', such as /Admin/GetTitleData; got 'C:/Users/dev/AppData/Local/Temp/foo'.",
            error.Message);
    }

    [Fact]
    public async Task ApiPath_UnderARealMsysRoot_ExplainsTheConversionEndToEnd()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            File.WriteAllBytes(
                Path.Combine(Directory.CreateDirectory(Path.Combine(root, "usr", "bin")).FullName, "msys-2.0.dll"),
                []);
            var converted = root.Replace('\\', '/') + "/Catalog/GetCatalogConfig";

            var result = await InvokeAsync(["--playfab-title-id", "ABC123", "playfab", "api", converted]);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains(
                "MSYS_NO_PATHCONV=1 xbox playfab api /Catalog/GetCatalogConfig",
                result.Stderr,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Renames must not re-target any call. Safety classification and the PlayFab request
    /// URI both derive from the API path, so a rename that silently changed a path would be
    /// a correctness and a safety regression at once.
    /// </summary>
    [Theory]
    [InlineData("playfab mps title status", "/MultiplayerServer/GetTitleEnabledForMultiplayerServersStatus")]
    [InlineData("playfab economy inventory list", "/Inventory/GetInventoryItems")]
    [InlineData("playfab economy inventory add", "/Inventory/AddInventoryItems")]
    [InlineData("playfab economy inventory collection list", "/Inventory/GetInventoryCollectionIds")]
    [InlineData("playfab economy inventory transaction list", "/Inventory/GetTransactionHistory")]
    [InlineData("playfab economy inventory operation execute", "/Inventory/ExecuteInventoryOperations")]
    [InlineData("playfab economy inventory operation status", "/Inventory/GetInventoryOperationStatus")]
    [InlineData("playfab experiment list", "/Experimentation/GetExperiments")]
    [InlineData("playfab experiment scorecard get", "/Experimentation/GetLatestScorecard")]
    [InlineData("playfab experiment treatment get", "/Experimentation/GetTreatmentAssignment")]
    [InlineData("playfab experiment exclusion-group list", "/Experimentation/GetExclusionGroups")]
    public void RenamedCommand_StillMapsToItsOriginalApiPath(string path, string expectedApiPath)
    {
        var actualApiPath = path switch
        {
            "playfab mps title status" => MpsCommandFactory.ApiCommands
                .Single(command => command.Path.SequenceEqual(["title", "status"]))
                .ApiPath,
            "playfab economy inventory list" => EconomyCommandFactory.GetInventoryItemsPath,
            "playfab economy inventory add" => EconomyCommandFactory.AddInventoryItemsPath,
            "playfab economy inventory collection list" =>
                EconomyCommandFactory.GetInventoryCollectionIdsPath,
            "playfab economy inventory transaction list" =>
                EconomyCommandFactory.GetTransactionHistoryPath,
            "playfab economy inventory operation execute" =>
                EconomyCommandFactory.ExecuteInventoryOperationsPath,
            "playfab economy inventory operation status" =>
                EconomyCommandFactory.GetInventoryOperationStatusPath,
            "playfab experiment list" => ExperimentCommandFactory.GetExperimentsPath,
            "playfab experiment scorecard get" => ExperimentCommandFactory.GetLatestScorecardPath,
            "playfab experiment treatment get" => ExperimentCommandFactory.GetTreatmentAssignmentPath,
            "playfab experiment exclusion-group list" => ExperimentCommandFactory.GetExclusionGroupsPath,
            _ => throw new InvalidOperationException($"Unknown test path '{path}'."),
        };

        Assert.Equal(expectedApiPath, actualApiPath);
    }

    [Theory]
    [InlineData("playfab mps title status", "/MultiplayerServer/GetTitleEnabledForMultiplayerServersStatus")]
    [InlineData("playfab economy inventory list", "/Inventory/GetInventoryItems")]
    [InlineData("playfab economy inventory add", "/Inventory/AddInventoryItems")]
    [InlineData("playfab economy inventory collection list", "/Inventory/GetInventoryCollectionIds")]
    [InlineData("playfab economy inventory transaction list", "/Inventory/GetTransactionHistory")]
    [InlineData("playfab economy inventory operation execute", "/Inventory/ExecuteInventoryOperations")]
    [InlineData("playfab economy inventory operation status", "/Inventory/GetInventoryOperationStatus")]
    [InlineData("playfab experiment list", "/Experimentation/GetExperiments")]
    [InlineData("playfab experiment scorecard get", "/Experimentation/GetLatestScorecard")]
    [InlineData("playfab experiment treatment get", "/Experimentation/GetTreatmentAssignment")]
    [InlineData("playfab experiment exclusion-group list", "/Experimentation/GetExclusionGroups")]
    public void HelpMetadata_RetainsOriginalApiPath(string path, string expectedApiPath)
    {
        var root = Program.BuildRootCommand();

        Assert.True(TryResolveRegisteredPath(root, path.Split(' '), out var command));

        Assert.Contains(
            expectedApiPath,
            CommandMetadataRegistry.GetMetadata(command!).ApiPaths ?? []);
    }

    [Theory]
    [InlineData("playfab mps title status", "Read-only")]
    [InlineData("playfab mps title enable", "Mutates")]
    [InlineData("playfab mps build delete", "Destructive")]
    public void HelpSafety_IsDerivedFromTheApiPath(string path, string expected)
    {
        var root = Program.BuildRootCommand();

        Assert.True(TryResolveRegisteredPath(root, path.Split(' '), out var command));

        var safety = CommandMetadataRegistry.DescribeSafety(
            CommandMetadataRegistry.GetMetadata(command!));
        Assert.NotNull(safety);
        Assert.Contains(expected, safety, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("playfab file delete", "/File/DeleteFiles")]
    [InlineData("playfab group delete", "/Group/DeleteGroup")]
    [InlineData("playfab group role delete", "/Group/DeleteRole")]
    [InlineData("playfab mps build delete", "/MultiplayerServer/DeleteBuild")]
    public void DestructiveLeaf_HelpMetadataDeclaresItsApiPath(
        string path,
        string expectedApiPath)
    {
        var root = Program.BuildRootCommand();

        Assert.True(TryResolveRegisteredPath(root, path.Split(' '), out var command));

        var metadata = CommandMetadataRegistry.GetMetadata(command!);
        Assert.Contains(expectedApiPath, metadata.ApiPaths ?? []);
        Assert.Contains(
            "--confirm",
            CommandMetadataRegistry.DescribeSafety(metadata),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("playfab api", "playfab-api")]
    [InlineData("playfab file list", "playfab-file-list")]
    [InlineData("xbl schema get", "xbl-schema-get")]
    [InlineData("xbl document download", "xbl-document-download")]
    [InlineData("xbl document commit", "xbl-document-commit")]
    [InlineData(
        "playfab economy inventory transaction list",
        "playfab-economy-inventory-transaction-list")]
    public void TelemetryIdentity_UsesTheFullCanonicalCommandPath(
        string path,
        string expectedIdentity)
    {
        var root = Program.BuildRootCommand();

        Assert.True(TryResolveRegisteredPath(root, path.Split(' '), out var command));

        var identity = CommandPathResolver.ResolveTelemetryIdentity(root, command!);

        Assert.Equal(expectedIdentity, identity);
        Assert.True(identity.Length <= 64);
        Assert.All(identity, character => Assert.True(
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_'));
    }

    [Fact]
    public void TelemetryIdentity_ExcludesOptionsArgumentsAndValues()
    {
        var root = Program.BuildRootCommand();

        var playFabParse = root.Parse(
            ["playfab", "title-data", "get", "--playfab-title-id", "SecretTitle1"],
            Program.NoResponseFileConfig);
        var apiParse = root.Parse([
            "playfab",
            "api",
            "/Admin/GetTitleData",
            "--body",
            "{\"Secret\":\"value\"}",
        ], Program.NoResponseFileConfig);

        Assert.Empty(playFabParse.Errors);
        Assert.Empty(apiParse.Errors);
        Assert.Equal(
            "playfab-title-data-get",
            CommandPathResolver.ResolveTelemetryIdentity(
                root,
                playFabParse.CommandResult.Command));
        Assert.Equal(
            "playfab-api",
            CommandPathResolver.ResolveTelemetryIdentity(
                root,
                apiParse.CommandResult.Command));
    }

    [Fact]
    public void EveryCommandTelemetryIdentity_IsUniqueBoundedAndSanitizerCompatible()
    {
        var root = Program.BuildRootCommand();
        var identities = EnumerateCommands(root)
            .Select(command => CommandPathResolver.ResolveTelemetryIdentity(root, command))
            .ToArray();

        Assert.Equal(
            identities.Length,
            identities.Distinct(StringComparer.Ordinal).Count());
        Assert.All(identities, identity =>
        {
            Assert.InRange(identity.Length, 1, 64);
            Assert.All(identity, character => Assert.True(
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_'));
        });
    }

    [Fact]
    public void SandboxOptions_UseConsistentIdentifierNames()
    {
        var options = EnumerateCommands(Program.BuildRootCommand())
            .SelectMany(command => command.Options)
            .Where(option => option.Name.Split('-').Contains("sandbox"))
            .ToArray();

        Assert.NotEmpty(options);
        Assert.All(options, option =>
        {
            Assert.EndsWith("-sandbox-id", option.Name, StringComparison.Ordinal);
            Assert.Empty(option.Aliases);
        });
    }

    /// <summary>
    /// Every Help example parses to the command that shows it, so an example can't outlive an
    /// option or path it uses. Quoted arguments are read as a shell would.
    /// </summary>
    [Fact]
    public void EveryHelpExample_ParsesToItsCommand()
    {
        var root = Program.BuildRootCommand();
        var checkedExamples = 0;

        foreach (var command in EnumerateCommands(root))
        {
            foreach (var example in CommandMetadataRegistry.GetMetadata(command).Examples ?? [])
            {
                var tokens = Regex.Matches(example, "'([^']*)'|\"([^\"]*)\"|(\\S+)")
                    .Select(match => match.Groups[1].Success ? match.Groups[1].Value
                        : match.Groups[2].Success ? match.Groups[2].Value
                        : match.Groups[3].Value)
                    .ToArray();
                Assert.Equal("xbox", tokens[0]);

                var parsed = root.Parse(tokens[1..], Program.NoResponseFileConfig);
                Assert.True(parsed.Errors.Count == 0,
                    $"'{example}' doesn't parse: {string.Join("; ", parsed.Errors.Select(error => error.Message))}");

                // Many commands share a name such as list or get, so compare the command itself.
                Assert.True(ReferenceEquals(command, parsed.CommandResult.Command),
                    $"'{example}' runs '{parsed.CommandResult.Command.Name}' under "
                    + $"'{parsed.CommandResult.Command.Parents.OfType<Command>().FirstOrDefault()?.Name}', "
                    + $"not the command that shows it.");
                checkedExamples++;
            }
        }

        Assert.True(checkedExamples > 0);
    }

    private static IEnumerable<Command> EnumerateCommands(Command command)
    {
        yield return command;
        foreach (var subcommand in command.Subcommands)
        {
            foreach (var descendant in EnumerateCommands(subcommand))
            {
                yield return descendant;
            }
        }
    }

    private static bool TryResolveRegisteredPath(
        RootCommand root,
        IReadOnlyList<string> segments,
        out Command? command)
    {
        Command current = root;
        foreach (var segment in segments)
        {
            var next = current.Subcommands.SingleOrDefault(candidate =>
                string.Equals(candidate.Name, segment, StringComparison.Ordinal));
            if (next is null)
            {
                command = null;
                return false;
            }

            current = next;
        }

        command = current;
        return true;
    }

    private static async Task<CapturedInvocation> InvokeAsync(string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);

        try
        {
            var exitCode = await Program.Main(args);
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
