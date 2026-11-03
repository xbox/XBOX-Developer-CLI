// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using XboxDevCLI.CommandLine;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public class CommandLifecycleTests
{
    [Fact]
    public void Metadata_DefaultsToNormalCompatibility()
    {
        Assert.Equal(CommandStage.Stable, new CommandMetadata().Stage);
        var command = new Command("example");
        Assert.Equal(CommandStage.Stable, CommandMetadataRegistry.GetMetadata(command).Stage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stage_PreservesAvailabilityAliasesOutputAndExitCode(bool experimental)
    {
        var metadata = new CommandMetadata(
            Stage: experimental ? CommandStage.Experimental : CommandStage.Stable,
            Output: "Example JSON result.");
        var root = new RootCommand();
        var command = new Command("game", "Example command").WithMetadata(metadata);
        command.Aliases.Add("example");
        command.SetAction(result =>
        {
            result.InvocationConfiguration.Output.WriteLine("""{"result":"ok"}""");
            return 7;
        });
        root.Subcommands.Add(command);
        CommandLineExperience.ConfigureHelp(root, "xbox");
        Assert.Equal(metadata, CommandMetadataRegistry.GetMetadata(command));
        foreach (var name in new[] { "game", "example" })
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var parsed = root.Parse([name], Program.NoResponseFileConfig);
            Assert.Empty(parsed.Errors);
            Assert.Equal(7, await parsed.InvokeAsync(new()
            {
                Output = stdout,
                Error = stderr,
            }));
            Assert.Equal("""{"result":"ok"}""" + Environment.NewLine, stdout.ToString());
            Assert.Empty(stderr.ToString());
        }

        using var help = new StringWriter();
        CommandHelpWriter.WriteRoot(help, root, "xbox");
        Assert.Equal(experimental, help.ToString().Contains("[Experimental]", StringComparison.Ordinal));
        Assert.DoesNotContain("[Stable]", help.ToString());
        string? expectedHelp = null;
        foreach (var args in new[] { new[] { "game", "--help" }, new[] { "example", "--help" },
                     new[] { "help", "game" }, new[] { "help", "example" } })
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            Assert.Equal(0, await root.Parse(args, Program.NoResponseFileConfig).InvokeAsync(new()
            {
                Output = stdout,
                Error = stderr,
            }));
            expectedHelp ??= stdout.ToString();
            Assert.Equal(expectedHelp, stdout.ToString());
            Assert.Empty(stderr.ToString());
            Assert.Equal(experimental, stdout.ToString().Contains("EXPERIMENTAL", StringComparison.Ordinal));
            if (experimental)
            {
                Assert.Contains("may change or be removed", stdout.ToString());
            }
        }
    }

    [Fact]
    public void Help_LabelsExperimentalCommandsAndDescendants()
    {
        var root = new RootCommand();
        var group = new Command("group", "Structural group");
        var parent = new Command("parent", "Executable group")
            .WithMetadata(new CommandMetadata(Stage: CommandStage.Experimental));
        parent.SetAction(_ => { });
        var child = new Command("child", "Inherited Experimental child");
        child.SetAction(_ => { });
        var experimental = new Command("experimental", "Experimental child")
            .WithMetadata(new CommandMetadata(Stage: CommandStage.Experimental));
        experimental.SetAction(_ => { });
        parent.Subcommands.Add(child);
        group.Subcommands.Add(parent);
        group.Subcommands.Add(experimental);
        root.Subcommands.Add(group);
        CommandLineExperience.ConfigureHelp(root, "xbox");

        foreach (var command in new Command[] { root, group, parent, child, experimental })
        {
            using var writer = new StringWriter();
            CommandHelpWriter.Write(writer, root, command, "xbox", width: 200);
            var output = writer.ToString();
            Assert.Equal(command == parent || command == child || command == experimental,
                output.Contains("EXPERIMENTAL" + Environment.NewLine, StringComparison.Ordinal));
            if (command == group)
            {
                Assert.Contains("Executable group [Experimental]", output);
                Assert.Contains("Experimental child [Experimental]", output);
            }
            Assert.DoesNotContain("[Stable]", output);
        }
        Assert.Empty(root.Parse(["group", "parent", "child"], Program.NoResponseFileConfig).Errors);
    }

    [Fact]
    public async Task XboxLiveRegistration_LabelsTheFamilyAndPreservesOtherCommands()
    {
        var root = Program.BuildRootCommand();
        var xboxLive = root.Subcommands.Single(command => command.Name == "xbl");
        Assert.Equal(CommandStage.Experimental, CommandMetadataRegistry.GetMetadata(xboxLive).Stage);
        Assert.Equal(HelpCategory.Core, CommandMetadataRegistry.GetMetadata(xboxLive).Category);
        using var rootHelp = new StringWriter();
        CommandHelpWriter.WriteRoot(rootHelp, root, "xbox", width: 200);
        Assert.Contains("Manage XBOX services configuration [Experimental]", rootHelp.ToString());

        foreach (var path in new[] { "", "configuration list", "configuration get", "schema get",
                     "document download", "document commit", "publish start", "publish status" })
        {
            var segments = new[] { "xbl" }.Concat(path.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray();
            using var output = new StringWriter();
            using var error = new StringWriter();
            var parsed = root.Parse([.. segments, "--help"], Program.NoResponseFileConfig);
            Assert.Equal(0, await parsed.InvokeAsync(new() { Output = output, Error = error }));
            Assert.Contains("EXPERIMENTAL" + Environment.NewLine, output.ToString());
            Assert.Empty(error.ToString());
        }
        foreach (var path in new[] { new[] { "auth", "status" }, new[] { "playfab", "api" },
                     new[] { "game", "list" }, new[] { "sandbox", "list" }, new[] { "info" } })
        {
            Assert.True(CommandPath.TryResolve(root, path, out var command, out _, out _));
            using var output = new StringWriter();
            CommandHelpWriter.Write(output, root, command, "xbox");
            Assert.DoesNotContain("EXPERIMENTAL" + Environment.NewLine, output.ToString());
        }
    }

    [Fact]
    public void PlayFabRegistration_LabelsOnlyTheThinApiFamiliesExperimental()
    {
        var root = Program.BuildRootCommand();
        var playFab = root.Subcommands.Single(command => command.Name == "playfab");
        Assert.Equal(CommandStage.Stable, CommandMetadataRegistry.GetMetadata(playFab).Stage);

        var experimental = playFab.Subcommands
            .Where(family => CommandMetadataRegistry.GetMetadata(family).Stage == CommandStage.Experimental)
            .Select(family => family.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            ["cloudscript", "experiment", "group", "mps", "player", "privacy"],
            experimental);

        using var playFabHelp = new StringWriter();
        CommandHelpWriter.Write(playFabHelp, root, playFab, "xbox", width: 400);
        Assert.DoesNotContain("EXPERIMENTAL" + Environment.NewLine, playFabHelp.ToString());

        foreach (var family in playFab.Subcommands)
        {
            var isExperimental = experimental.Contains(family.Name);
            var familyRow = playFabHelp.ToString().Split(Environment.NewLine)
                .Single(line => line.TrimStart().StartsWith(family.Name + ":", StringComparison.Ordinal));
            Assert.Equal(isExperimental, familyRow.EndsWith("[Experimental]", StringComparison.Ordinal));

            foreach (var command in SelfAndDescendants(family))
            {
                using var output = new StringWriter();
                CommandHelpWriter.Write(output, root, command, "xbox", width: 400);
                Assert.True(
                    isExperimental == output.ToString().Contains("EXPERIMENTAL" + Environment.NewLine, StringComparison.Ordinal),
                    $"Help for '{command.Name}' under '{family.Name}' should{(isExperimental ? "" : " not")} be labeled Experimental.");
            }
        }
    }

    private static IEnumerable<Command> SelfAndDescendants(Command command) =>
        command.Subcommands.SelectMany(SelfAndDescendants).Prepend(command);
}
