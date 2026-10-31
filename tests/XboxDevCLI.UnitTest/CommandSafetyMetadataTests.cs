// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using XboxDevCLI.CommandLine;
using XboxDevCLI.PlayFab.Commands;
using XboxDevCLI.PlayFab.Generated;
using Xunit;

namespace XboxDevCLI.UnitTest;

/// <summary>
/// Guards the presence and accuracy of the safety metadata Help renders.
/// </summary>
/// <remarks>
/// <see cref="CommandMetadataRegistry.DescribeSafety"/> returns <c>null</c> when a command records
/// neither an explicit classification nor an API path, and Help then omits the SAFETY section
/// rather than guessing. That is the right call for the renderer, but it means a command that was
/// never given metadata says nothing about its effect and nothing notices — which is how 41 leaf
/// commands, 27 of them writing to production, came to advertise nothing at all.
/// <para>
/// These tests close that by inverting the default: a command that does work must record an API
/// path unless it appears in <see cref="CommandsWithoutApiPaths"/> with a reason. The table is the
/// point. A test that skipped every command it could not classify would pass exactly as happily
/// with the gap reopened, so the exceptions are enumerated one command path at a time rather than
/// by subtree: adding a PlayFab call under <c>auth</c> or <c>seller</c> fails here just as adding
/// one under <c>playfab</c> does.
/// </para>
/// <para>
/// The one category exclusion is commands whose action is a <see cref="HelpRenderingAction"/>,
/// which covers every group and the <c>help</c> command. That is a guarantee from the type rather
/// than a name on a list: an action that renders Help cannot reach PlayFab. Everything else is in
/// scope, including a command that has subcommands <em>and</em> an action of its own —
/// <c>CommandLineExperience.SetGroupHelpActions</c> deliberately leaves such a command's action
/// alone, so defining the guarded set as "no subcommands" would let one through.
/// </para>
/// </remarks>
public class CommandSafetyMetadataTests
{
    /// <summary>
    /// The commands that record no PlayFab API path: the classification each must still render,
    /// and why it records no path. Listed by exact command path — an exemption covers one command,
    /// never a namespace — and paired with its expected <see cref="CommandSafety"/> so the
    /// exemption asserts something rather than only suppressing a check.
    /// </summary>
    /// <remarks>
    /// Scoping this to the <c>playfab</c> subtree would shrink the table to one entry, but a
    /// subtree exemption is the blanket skip this file exists to avoid: it would silently cover a
    /// PlayFab call added anywhere else. What the table cannot detect is an exempt command that
    /// later starts calling PlayFab without declaring a path — no static check sees that — which
    /// is why the entries are kept few and specific.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, (CommandSafety? Safety, string Reason)>
        CommandsWithoutApiPaths =
        new Dictionary<string, (CommandSafety?, string)>(StringComparer.Ordinal)
        {
            ["playfab api"] = (CommandSafety.DependsOnApiPath,
                "The API path is an argument, so no single path can be recorded. It states that " +
                "its effect depends on that path, which is why it still renders SAFETY."),
            ["auth login"] = (null, "Entra ID sign-in. Not a PlayFab command."),
            ["auth logout"] = (null, "Clears the local token cache. Not a PlayFab command."),
            ["auth status"] = (null, "Reports the local sign-in state. Not a PlayFab command."),
            ["info"] = (null, "Prints version and getting-started links. Not a PlayFab command."),
            ["feedback"] = (CommandSafety.ReadOnly,
                "Opens a local issue draft after consent and never submits it; not a PlayFab command."),
            ["game list"] = (CommandSafety.ReadOnly,
                "Calls the XboxPartner gateway, not PlayFab; classified explicitly."),
            ["game get"] = (CommandSafety.ReadOnly,
                "Calls the XboxPartner gateway, not PlayFab; classified explicitly."),
            ["game create"] = (CommandSafety.RemoteMutation,
                "Creates a game through the XboxPartner gateway, not PlayFab; classified explicitly."),
            ["game job get"] = (CommandSafety.ReadOnly,
                "Reads a creation job through the XboxPartner gateway, not PlayFab; classified explicitly."),
            ["seller list"] = (CommandSafety.ReadOnly,
                "Calls the XboxPartner gateway, not PlayFab; classified explicitly."),
            ["context get"] = (CommandSafety.ReadOnly,
                "Reads the locally saved context; makes no PlayFab or gateway call."),
            ["context set"] = (CommandSafety.LocalState,
                "Writes the locally saved context; makes no PlayFab, gateway, or other network call."),
            ["context unset"] = (CommandSafety.LocalState,
                "Removes values from the locally saved context; makes no PlayFab or gateway call."),
            ["context key list"] = (CommandSafety.ReadOnly,
                "Lists the context keys the CLI supports; makes no network call."),
            ["context profile list"] = (CommandSafety.ReadOnly,
                "Lists the profiles in the locally saved context; makes no network call."),
            ["context profile delete"] = (CommandSafety.LocalState,
                "Removes a profile from the locally saved context; makes no network call."),
            ["pc sandbox get"] = (CommandSafety.ReadOnly,
                "Runs XblPCSandbox.exe /get on this PC, not PlayFab; classified explicitly."),
            ["pc sandbox set"] = (CommandSafety.MachineState,
                "Runs XblPCSandbox.exe to change a machine-wide PC setting, not PlayFab; classified explicitly."),
            ["pc sandbox launch-apps"] = (CommandSafety.MachineState,
                "Runs XblPCSandbox.exe to reopen apps on this PC, not PlayFab; classified explicitly."),
            ["sandbox list"] = (CommandSafety.ReadOnly,
                "Calls the XboxPartner gateway, not PlayFab; classified explicitly."),
            ["sandbox get"] = (CommandSafety.ReadOnly,
                "Calls the XboxPartner gateway, not PlayFab; classified explicitly."),
            ["sandbox create"] = (CommandSafety.RemoteMutation,
                "Creates a sandbox through the XboxPartner gateway, not PlayFab; classified explicitly."),
            ["test-account batch list"] = (CommandSafety.ReadOnly,
                "Calls the XboxPartner gateway, not PlayFab; classified explicitly."),
            ["test-account batch get"] = (CommandSafety.ReadOnly,
                "Calls the XboxPartner gateway, not PlayFab; classified explicitly."),
            ["test-account get"] = (CommandSafety.ReadOnly,
                "Calls the XboxPartner gateway, not PlayFab; classified explicitly."),
            ["test-account batch create"] = (CommandSafety.RemoteMutation,
                "Creates test accounts through the XboxPartner gateway, not PlayFab; classified explicitly."),
            ["test-account batch delete"] = (CommandSafety.RemoteMutation,
                "Deletes a test account batch through the XboxPartner gateway, not PlayFab; classified explicitly."),
            ["test-account batch update"] = (CommandSafety.RemoteMutation,
                "Changes a test account batch through the XboxPartner gateway, not PlayFab; classified explicitly."),
            ["test-account job get"] = (CommandSafety.ReadOnly,
                "Reads a creation job through the XboxPartner gateway, not PlayFab; classified explicitly."),
            ["test-account job wait"] = (CommandSafety.ReadOnly,
                "Polls a creation job through the XboxPartner gateway, not PlayFab; classified explicitly."),
            ["xbl configuration list"] = (CommandSafety.ReadOnly,
                "Calls the XboxPartner gateway, not PlayFab; classified explicitly."),
            ["xbl configuration get"] = (CommandSafety.ReadOnly,
                "Calls the XboxPartner gateway, not PlayFab; classified explicitly."),
            ["xbl achievement-image upload"] = (CommandSafety.RemoteMutation,
                "Registers an image through the XboxPartner gateway, not PlayFab."),
            ["xbl achievement-image get"] = (CommandSafety.ReadOnly,
                "Reads an image through the XboxPartner gateway, not PlayFab."),
            ["xbl achievement-image list"] = (CommandSafety.ReadOnly,
                "Lists images through the XboxPartner gateway, not PlayFab."),
            ["xbl achievement-image download"] = (CommandSafety.ReadOnly,
                "Reads image metadata and CDN content, then writes local files."),
            ["upgrade"] = (CommandSafety.ReadOnly,
                "Reads GitHub release metadata, not PlayFab; classified explicitly."),
            ["xbl schema get"] = (CommandSafety.ReadOnly,
                "Calls the XboxPartner gateway, not PlayFab; classified explicitly."),
            ["xbl document download"] = (CommandSafety.ReadOnly,
                "Calls the XboxPartner gateway, not PlayFab; classified explicitly."),
            ["xbl document commit"] = (CommandSafety.RemoteMutation,
                "Commits through the XboxPartner gateway, not PlayFab; classified explicitly."),
            ["xbl publish start"] = (CommandSafety.RemoteMutation,
                "Publishes through the XboxPartner gateway, not PlayFab; classified explicitly."),
            ["xbl publish status"] = (CommandSafety.ReadOnly,
                "Reads publish status through the XboxPartner gateway, not PlayFab; classified explicitly."),
        };

    /// <summary>
    /// A guard that inspects nothing passes. This pins both the enumeration and the theory data
    /// against silently collapsing to empty — the failure mode that would leave every other test
    /// in this class green while checking no command at all.
    /// </summary>
    [Fact]
    public void TheGuard_InspectsTheWholeCommandSurface()
    {
        var guarded = EnumerateGuardedCommands().Select(entry => entry.Path).ToArray();

        Assert.True(
            guarded.Length > 100,
            $"Expected the guard to inspect the whole command surface; it found {guarded.Length} " +
            "commands. A collapsed enumeration would make every other test here vacuous.");
        Assert.Contains("playfab title-data set", guarded);
        Assert.Contains("seller list", guarded);
        Assert.True(RuntimeSelectedPaths().Count > 20);

        // Groups and `help` render Help and cannot reach PlayFab, so they are out of scope by
        // type rather than by name. Asserted so the exclusion stays deliberate.
        Assert.DoesNotContain("playfab", guarded);
        Assert.DoesNotContain("playfab title-data", guarded);
        Assert.DoesNotContain("help", guarded);
    }

    /// <summary>
    /// A command that reaches PlayFab must say which operation it reaches. Anything else is
    /// either a mistake or a decision, and a decision belongs in
    /// <see cref="CommandsWithoutApiPaths"/> where a reviewer will see it.
    /// </summary>
    [Fact]
    public void EveryCommand_DeclaresAnApiPathOrIsADocumentedException()
    {
        var undeclared = EnumerateGuardedCommands()
            .Where(entry => !CommandsWithoutApiPaths.ContainsKey(entry.Path))
            .Where(entry => CommandMetadataRegistry.GetMetadata(entry.Command).ApiPaths
                is not { Count: > 0 })
            .Select(entry => entry.Path)
            .ToArray();

        Assert.True(
            undeclared.Length == 0,
            "These commands record no PlayFab API path, so their Help says nothing about " +
            "what they do to a production title:" + Environment.NewLine +
            string.Join(Environment.NewLine, undeclared.Select(path => $"  xbox {path}")) +
            Environment.NewLine +
            $"Attach a {nameof(CommandMetadata)} with the paths the command actually calls " +
            "(every path it can reach, not just the default branch), or add it to " +
            $"{nameof(CommandsWithoutApiPaths)} with the reason it cannot declare one.");
    }

    /// <summary>
    /// A wrong path is worse than no path: it produces a confident safety claim about an
    /// operation that does not exist. Every declared path is therefore checked against the
    /// bundled metadata, which is the same source the runtime resolves requests from.
    /// </summary>
    [Fact]
    public void EveryDeclaredApiPath_ExistsInTheBundledPlayFabMetadata()
    {
        var known = PlayFabApiMetadata.AllEndpoints
            .Select(endpoint => endpoint.Path)
            .ToHashSet(StringComparer.Ordinal);

        var unknown = EnumerateGuardedCommands()
            .SelectMany(entry =>
                (CommandMetadataRegistry.GetMetadata(entry.Command).ApiPaths ?? [])
                    .Where(path => !known.Contains(path))
                    .Select(path => $"  xbox {entry.Path} -> {path}"))
            .ToArray();

        Assert.True(
            unknown.Length == 0,
            "These commands declare a PlayFab API path that does not exist in " +
            $"{nameof(PlayFabApiMetadata)}:" + Environment.NewLine +
            string.Join(Environment.NewLine, unknown));
    }

    /// <summary>
    /// The exemption table has to stay honest in both directions: an entry that no longer names a
    /// guarded command is stale — a rename would leave it behind, un-guarding nothing but
    /// misdescribing the surface — and one that has since gained an API path is no longer an
    /// exception and should be removed so the command is guarded like every other. The recorded
    /// classification is asserted too, so an exemption cannot quietly become silence.
    /// </summary>
    [Fact]
    public void EveryDocumentedException_NamesACommandWithTheClassificationItRecords()
    {
        var guarded = EnumerateGuardedCommands().ToDictionary(
            entry => entry.Path,
            entry => entry.Command,
            StringComparer.Ordinal);

        var problems = new List<string>();
        foreach (var (path, expected) in CommandsWithoutApiPaths)
        {
            if (!guarded.TryGetValue(path, out var command))
            {
                problems.Add($"  '{path}' is not a command this test guards");
                continue;
            }

            var metadata = CommandMetadataRegistry.GetMetadata(command);
            if (metadata.ApiPaths is { Count: > 0 })
            {
                problems.Add($"  '{path}' now declares an API path");
            }

            if (metadata.Safety != expected.Safety)
            {
                problems.Add(
                    $"  '{path}' records {Describe(metadata.Safety)}, the table expects " +
                    $"{Describe(expected.Safety)}");
            }

            // The reason is the whole justification for the exemption. Left blank it reads as a
            // decision someone made and can no longer explain, which is indistinguishable from
            // the command having been added here to make a failing test pass.
            if (string.IsNullOrWhiteSpace(expected.Reason))
            {
                problems.Add($"  '{path}' is exempt without recording why");
            }
        }

        Assert.True(
            problems.Count == 0,
            $"{nameof(CommandsWithoutApiPaths)} is out of date:" + Environment.NewLine +
            string.Join(Environment.NewLine, problems));

        static string Describe(CommandSafety? safety) =>
            safety?.ToString() ?? "no explicit classification";
    }

    /// <summary>
    /// Everything under <c>playfab</c> answers the question a user opens Help to ask, including
    /// the four commands that record no path — silence there reads the same as a command whose
    /// metadata was forgotten.
    /// </summary>
    [Fact]
    public void EveryPlayFabCommand_RendersASafetySection()
    {
        var silent = EnumerateGuardedCommands()
            .Where(entry => entry.Path.StartsWith("playfab ", StringComparison.Ordinal))
            .Where(entry => CommandMetadataRegistry.DescribeSafety(
                CommandMetadataRegistry.GetMetadata(entry.Command)) is null)
            .Select(entry => $"  xbox {entry.Path}")
            .ToArray();

        Assert.True(
            silent.Length == 0,
            "These PlayFab commands render no SAFETY section:" + Environment.NewLine +
            string.Join(Environment.NewLine, silent));
    }

    /// <summary>
    /// An explicit classification is applied before the operation names are consulted, so it is
    /// the one mechanism that can contradict them. It exists to correct a name that misleads, and
    /// these are the rules that keep it to that: <see cref="CommandSafety.Mutates"/> must still
    /// name the operation it calls, or it becomes a way to claim a classification while declaring
    /// nothing; <see cref="CommandSafety.DependsOnApiPath"/> must declare no paths, because a
    /// command with a known path does not have a run-time-dependent effect; and
    /// <see cref="CommandSafety.LocalState"/> must declare none either, because it renders
    /// "Changes local CLI state only" — a claim that no remote call happens at all, which a
    /// declared PlayFab path contradicts outright.
    /// <para>
    /// <see cref="CommandSafety.ReadOnly"/> deliberately may accompany paths.
    /// <c>group member is-member</c> depends on it: <c>/Group/IsMember</c> reads, but is not named
    /// <c>Get</c>, <c>List</c>, or <c>Search</c>, so the name-derived classification is the thing
    /// being corrected. That direction is pinned separately by
    /// <see cref="OnlyAVerifiedReadDeclaresItselfReadOnlyAgainstItsOperationName"/>.
    /// </para>
    /// </summary>
    /// <remarks>
    /// A classification cannot suppress the destructive tier — <c>DescribeSafety</c> decides that
    /// from the paths first, and <c>AnExplicitClassification_CannotSuppressTheDestructiveWarning</c>
    /// pins it — so these rules only have to keep the read-only and mutating tiers honest.
    /// </remarks>
    [Fact]
    public void AnExplicitClassification_ObeysItsCombinationRules()
    {
        var problems = EnumerateGuardedCommands()
            .Select(entry => (entry.Path, Metadata: CommandMetadataRegistry.GetMetadata(entry.Command)))
            .SelectMany(entry => Violations(entry.Path, entry.Metadata))
            .ToArray();

        Assert.True(
            problems.Length == 0,
            "These commands combine an explicit classification with API paths in a way the " +
            "classification is not meant for:" + Environment.NewLine +
            string.Join(Environment.NewLine, problems));

        static IEnumerable<string> Violations(string path, CommandMetadata metadata)
        {
            var hasPaths = metadata.ApiPaths is { Count: > 0 };

            if (metadata.Safety == CommandSafety.Mutates && !hasPaths)
            {
                yield return $"  xbox {path} claims {nameof(CommandSafety.Mutates)} without " +
                    "naming the PlayFab operation it calls";
            }

            if (metadata.Safety == CommandSafety.DependsOnApiPath && hasPaths)
            {
                yield return $"  xbox {path} claims {nameof(CommandSafety.DependsOnApiPath)} " +
                    "while declaring a known path";
            }

            if (metadata.Safety == CommandSafety.LocalState && hasPaths)
            {
                yield return $"  xbox {path} claims {nameof(CommandSafety.LocalState)} while " +
                    "declaring a PlayFab path, so Help would report a remote call as local";
            }

            if (metadata.Safety == CommandSafety.MachineState && hasPaths)
            {
                yield return $"  xbox {path} claims {nameof(CommandSafety.MachineState)} while " +
                    "declaring a PlayFab path, so Help would hide a remote call";
            }
        }
    }

    /// <summary>
    /// The destructive tier is the only part of this text tied to a runtime gate, so Help must not
    /// be able to contradict it. An explicit classification is applied before the operation names
    /// for every other tier; this pins that it is applied after them here.
    /// </summary>
    [Fact]
    public void AnExplicitClassification_CannotSuppressTheDestructiveWarning()
    {
        foreach (var safety in Enum.GetValues<CommandSafety>())
        {
            var description = CommandMetadataRegistry.DescribeSafety(new CommandMetadata(
                ApiPaths: ["/File/DeleteFiles"],
                Safety: safety));

            Assert.StartsWith(
                "Destructive and irreversible.",
                description,
                StringComparison.Ordinal);
            Assert.Contains("--confirm", description, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The commands that pick their path at run time are the ones a partial declaration hides
    /// best: declaring only the default branch reads as complete and is wrong for every other
    /// value of the flag. Each branch is resolved through the same helper the action uses and
    /// checked against what Help advertises.
    /// </summary>
    [Theory]
    [MemberData(nameof(RuntimeSelectedPaths))]
    public void ARuntimeSelectedPath_IsOneOfThePathsItsCommandDeclares(
        string commandPath,
        string resolvedApiPath)
    {
        var command = ResolveCommand(commandPath);
        var declared = CommandMetadataRegistry.GetMetadata(command).ApiPaths ?? [];

        Assert.Contains(resolvedApiPath, declared);
    }

    /// <summary>
    /// The converse, and the half that is easy to leave out: a declared path no branch can reach
    /// is advertised to the reader and never called. Nothing else here would notice — an unrelated
    /// but real path satisfies the existence check and every "the branch I resolved is declared"
    /// assertion — and a spurious <c>Delete*</c> would make Help call the command destructive.
    /// </summary>
    [Theory]
    [MemberData(nameof(RuntimeSelectedCommands))]
    public void ARuntimeSelectedCommand_DeclaresExactlyThePathsItCanReach(string commandPath)
    {
        var reachable = RuntimeReachablePaths[commandPath]
            .OrderBy(path => path, StringComparer.Ordinal);
        var declared = (CommandMetadataRegistry.GetMetadata(ResolveCommand(commandPath)).ApiPaths ?? [])
            .OrderBy(path => path, StringComparer.Ordinal);

        Assert.Equal(reachable, declared);
    }

    public static TheoryData<string> RuntimeSelectedCommands() => [.. RuntimeReachablePaths.Keys];

    public static TheoryData<string, string> RuntimeSelectedPaths()
    {
        var data = new TheoryData<string, string>();
        foreach (var (commandPath, reachable) in RuntimeReachablePaths)
        {
            foreach (var apiPath in reachable)
            {
                data.Add(commandPath, apiPath);
            }
        }

        return data;
    }

    /// <summary>
    /// Every path each run-time-selecting command can reach, resolved through the same helpers its
    /// action uses rather than restated as literals — a restated list would agree with the
    /// declaration for exactly as long as someone kept both in step.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> RuntimeReachablePaths =
        BuildRuntimeReachablePaths();

    private static IReadOnlyDictionary<string, IReadOnlySet<string>> BuildRuntimeReachablePaths()
    {
        var reachable = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var internalTier in new[] { false, true })
        {
            Add("playfab title-data get", TitleDataCommandFactory.BuildGetCall(internalTier, null).apiPath);
            Add("playfab title-data set", TitleDataCommandFactory.BuildSetCall(internalTier, "k", "v").apiPath);

            // delete removes keys through SetTitleData, so it must advertise the Set paths.
            Add("playfab title-data delete", TitleDataCommandFactory.BuildSetCall(internalTier, "k", null).apiPath);
        }

        foreach (var visibility in new[] { "client", "read-only", "internal" })
        {
            foreach (var publisher in new[] { false, true })
            {
                Assert.True(PlayerDataCommandFactory.TryResolvePaths(
                    visibility, publisher, out var getPath, out var setPath, out _, out _));

                Add("playfab player-data get", getPath);
                Add("playfab player-data set", setPath);
                Add("playfab player-data delete", setPath);
            }
        }

        foreach (var type in new string?[] { null, "http", "queued", "eventhub" })
        {
            Add(
                "playfab cloudscript function list",
                CloudScriptFunctionCommandFactory.BuildListFunctionsApiPath(type));
        }

        foreach (var endpoint in MpsCommandFactory.BuildCreateEndpoints.Values)
        {
            Add("playfab mps build create", endpoint);
        }

        // Two calls in sequence rather than a choice between them, so both are reachable.
        Add("playfab file upload", FileCommandFactory.InitiateFileUploadsPath);
        Add("playfab file upload", FileCommandFactory.FinalizeFileUploadsPath);

        // Sent only when the upload fails after it starts, to cancel the pending upload.
        Add("playfab file upload", FileCommandFactory.AbortFileUploadsPath);

        // The bytes travel over a storage URL, but the metadata read that yields it is a PlayFab
        // call and is the one the command must name.
        Add("playfab file download", FileCommandFactory.BuildListCall("id", "title").apiPath);
        Add("playfab mps asset upload", MpsCommandFactory.GetAssetUploadUrlPath);

        return reachable.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlySet<string>)entry.Value,
            StringComparer.Ordinal);

        void Add(string commandPath, string apiPath)
        {
            if (!reachable.TryGetValue(commandPath, out var paths))
            {
                paths = new HashSet<string>(StringComparer.Ordinal);
                reachable[commandPath] = paths;
            }

            paths.Add(apiPath);
        }
    }

    /// <summary>
    /// Overriding toward read-only is the dangerous direction: it replaces the conservative
    /// classification the operation name would produce with a quieter one, and a command that
    /// really wrote would then advertise that it does not. Exactly one command does it, and this
    /// pins the set so a second is a deliberate, reviewed change rather than a quiet one.
    /// </summary>
    /// <remarks>
    /// The operation-name rule the repository states — only <c>Get</c>, <c>List</c>, and
    /// <c>Search</c> are read-only — is a heuristic over names, and <c>/Group/IsMember</c> is its
    /// one false positive across every path the CLI declares: PlayFab documents it as "Checks to
    /// see if an entity is a member of a group or role within the group". The rule itself is
    /// deliberately left alone, because a central prefix list that grows exceptions is how a
    /// conservative default stops being conservative.
    /// </remarks>
    [Fact]
    public void OnlyAVerifiedReadDeclaresItselfReadOnlyAgainstItsOperationName()
    {
        string[] expected = ["playfab group member is-member"];

        var overriding = EnumerateGuardedCommands()
            .Select(entry => (entry.Path, Metadata: CommandMetadataRegistry.GetMetadata(entry.Command)))
            .Where(entry => entry.Metadata.Safety == CommandSafety.ReadOnly)
            .Where(entry => entry.Metadata.ApiPaths is { Count: > 0 } paths &&
                !paths.All(IsReadOnlyOperation))
            .Select(entry => entry.Path)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, overriding);
    }

    private static bool IsReadOnlyOperation(string apiPath)
    {
        var operation = apiPath[(apiPath.LastIndexOf('/') + 1)..];
        return operation.StartsWith("Get", StringComparison.Ordinal) ||
            operation.StartsWith("List", StringComparison.Ordinal) ||
            operation.StartsWith("Search", StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>mps asset upload</c> and <c>file download</c> both call a <c>Get*</c> operation and then
    /// transfer bytes over the URL it returns, in opposite directions. Deriving either one's
    /// safety from its operation name alone would call the upload read-only.
    /// </summary>
    [Fact]
    public void ATransferCommand_IsClassifiedByItsEffectNotItsOperationName()
    {
        var upload = CommandMetadataRegistry.DescribeSafety(
            CommandMetadataRegistry.GetMetadata(ResolveCommand("playfab mps asset upload")));
        var download = CommandMetadataRegistry.DescribeSafety(
            CommandMetadataRegistry.GetMetadata(ResolveCommand("playfab file download")));

        Assert.StartsWith("Mutates production title state", upload, StringComparison.Ordinal);
        Assert.StartsWith("Read-only.", download, StringComparison.Ordinal);
        Assert.Contains("local destination", download, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a command changes anything decides whether it says where the implicit context it
    /// uses came from. It's judged the way Help describes it: a destructive path first, then an
    /// explicit classification, then the operation names. A path given at run time stands in for
    /// the declared ones, and a command that declares neither isn't counted as a change.
    /// </summary>
    [Theory]
    [InlineData(null, null, null, false)]
    [InlineData("ReadOnly", null, null, false)]
    [InlineData("LocalState", null, null, false)]
    [InlineData("MachineState", null, null, false)]
    [InlineData("Mutates", null, null, true)]
    [InlineData("RemoteMutation", null, null, true)]
    [InlineData(null, "/Admin/GetTitleData", null, false)]
    [InlineData(null, "/Admin/SetTitleData", null, true)]
    [InlineData("ReadOnly", "/Admin/DeleteTitleDataOverride", null, true)]
    [InlineData("DependsOnApiPath", null, null, false)]
    [InlineData("DependsOnApiPath", null, "/Admin/SetTitleData", true)]
    [InlineData("DependsOnApiPath", null, "/Admin/GetTitleData", false)]
    [InlineData("ReadOnly", null, "/Admin/DeleteTitle", true)]
    [InlineData(null, "/Admin/SetTitleData", "/Admin/GetTitleData", false)]
    public void IsMutation_FollowsTheSafetyHelpDescribes(
        string? safety,
        string? declaredPath,
        string? apiPath,
        bool expected)
    {
        var command = new Command("probe");
        if (safety is not null || declaredPath is not null)
        {
            command.WithMetadata(new CommandMetadata(
                ApiPaths: declaredPath is null ? null : [declaredPath],
                Safety: safety is null ? null : Enum.Parse<CommandSafety>(safety)));
        }

        Assert.Equal(expected, CommandMetadataRegistry.IsMutation(command, apiPath));
    }

    [Theory]
    [InlineData("playfab title-data set", true)]
    [InlineData("playfab title-data get", false)]
    [InlineData("playfab mps asset upload", true)]
    [InlineData("playfab group member is-member", false)]
    [InlineData("game create", true)]
    [InlineData("game list", false)]
    [InlineData("context set", false)]
    [InlineData("pc sandbox set", false)]
    [InlineData("pc sandbox launch-apps", false)]
    [InlineData("auth status", false)]
    public void IsMutation_ClassifiesTheShippedCommands(string commandPath, bool expected)
    {
        Assert.Equal(expected, CommandMetadataRegistry.IsMutation(ResolveCommand(commandPath)));
    }

    private static Command ResolveCommand(string commandPath)
    {
        var root = Program.BuildRootCommand();
        Command current = root;

        foreach (var segment in commandPath.Split(' '))
        {
            current = current.Subcommands.Single(candidate =>
                string.Equals(candidate.Name, segment, StringComparison.Ordinal));
        }

        return current;
    }

    /// <summary>
    /// Every command this file holds to the declaration rule: everything in the tree except the
    /// ones that only render Help. A command with subcommands <em>and</em> its own action is
    /// included deliberately — see the note on the class.
    /// </summary>
    private static IEnumerable<(string Path, Command Command)> EnumerateGuardedCommands()
    {
        var root = Program.BuildRootCommand();
        return Walk(root, string.Empty).Where(entry => !RendersHelpOnly(entry.Command));

        static bool RendersHelpOnly(Command command) =>
            command.Action is HelpRenderingAction ||
            (command.Subcommands.Count > 0 && command.Action is null);

        static IEnumerable<(string Path, Command Command)> Walk(Command command, string prefix)
        {
            foreach (var subcommand in command.Subcommands)
            {
                var path = prefix.Length == 0
                    ? subcommand.Name
                    : $"{prefix} {subcommand.Name}";

                yield return (path, subcommand);

                foreach (var descendant in Walk(subcommand, path))
                {
                    yield return descendant;
                }
            }
        }
    }
}
