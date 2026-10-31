// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.RegularExpressions;
using XboxDevCLI.PlayFab.Commands;
using XboxDevCLI.PlayFab.Generated;
using Xunit;

namespace XboxDevCLI.UnitTest;

public class MpsCommandTests
{
    // Walks the command tree and yields every leaf (a command with no sub-commands) as its
    // space-joined path, e.g. "mps build alias create".
    private static IEnumerable<string> LeafPaths(Command command, string prefix = "")
    {
        var path = prefix.Length == 0 ? command.Name : $"{prefix} {command.Name}";
        if (command.Subcommands.Count == 0)
        {
            yield return path;
            yield break;
        }

        foreach (var sub in command.Subcommands)
        {
            foreach (var leaf in LeafPaths(sub, path))
            {
                yield return leaf;
            }
        }
    }

    [Fact]
    public void MpsCommand_IsNamedMps()
    {
        var mps = MpsCommandFactory.CreateMpsCommand();
        Assert.Equal("mps", mps.Name);
    }

    [Fact]
    public void MpsCommand_HasOneLeafPerApiCommandPlusBuildCreateAndAssetUpload()
    {
        var mps = MpsCommandFactory.CreateMpsCommand();
        var leaves = LeafPaths(mps).ToList();

        // One leaf per mapped API command, plus the special `build create` and `asset upload`.
        Assert.Equal(MpsCommandFactory.ApiCommands.Count + 2, leaves.Count);
    }

    [Fact]
    public void MpsCommand_ExposesRepresentativeNounVerbCommands()
    {
        var mps = MpsCommandFactory.CreateMpsCommand();
        var paths = LeafPaths(mps).ToHashSet();

        Assert.Contains("mps build get", paths);
        Assert.Contains("mps build list", paths);
        Assert.Contains("mps build create", paths);
        Assert.Contains("mps build alias create", paths);
        Assert.Contains("mps build region update-all", paths);
        Assert.Contains("mps server shutdown", paths);
        Assert.Contains("mps quota change list", paths);
        Assert.Contains("mps container-registry credentials rollover", paths);
        Assert.Contains("mps asset upload", paths);
        Assert.Contains("mps party request", paths);
    }

    [Fact]
    public void ApiCommands_HaveUniquePaths()
    {
        var paths = MpsCommandFactory.ApiCommands.Select(c => string.Join(' ', c.Path)).ToList();
        Assert.Equal(paths.Count, paths.Distinct().Count());
    }

    [Fact]
    public void ApiCommands_UseKebabCasePathSegments()
    {
        var kebab = new Regex("^[a-z]+(-[a-z]+)*$");
        foreach (var command in MpsCommandFactory.ApiCommands)
        {
            Assert.NotEmpty(command.Path);
            foreach (var segment in command.Path)
            {
                Assert.Matches(kebab, segment);
            }
        }
    }

    [Fact]
    public void ApiCommands_MapToKnownPlayFabEndpoints()
    {
        var knownPaths = PlayFabApiMetadata.AllEndpoints.Select(e => e.Path).ToHashSet();
        foreach (var command in MpsCommandFactory.ApiCommands)
        {
            Assert.Contains(command.ApiPath, knownPaths);
        }
    }

    [Fact]
    public void BuildCreateEndpoints_AreKnownPlayFabEndpoints()
    {
        var knownPaths = PlayFabApiMetadata.AllEndpoints.Select(e => e.Path).ToHashSet();
        foreach (var apiPath in MpsCommandFactory.BuildCreateEndpoints.Values)
        {
            Assert.Contains(apiPath, knownPaths);
        }
    }

    [Fact]
    public void BuildCreate_RequiresType()
    {
        var create = GetLeaf("build", "create");

        var requiredOptions = create.Options
            .OfType<Option>()
            .Where(o => o.Required)
            .Select(o => o.Name)
            .ToHashSet();

        Assert.Contains("--type", requiredOptions);
    }

    [Fact]
    public void BuildCreate_RejectsUnknownType()
    {
        var create = GetLeaf("build", "create");

        var result = create.Parse("--type bogus --body {}");

        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void BuildCreate_AcceptsTypeCaseInsensitively()
    {
        var create = GetLeaf("build", "create");

        var result = create.Parse("--type Managed --body {}");

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void AssetUpload_RequiresFileAndAssetName()
    {
        var upload = GetLeaf("asset", "upload");

        var requiredOptions = upload.Options
            .OfType<Option>()
            .Where(o => o.Required)
            .Select(o => o.Name)
            .ToHashSet();

        Assert.Contains("--file", requiredOptions);
        Assert.Contains("--asset-name", requiredOptions);
    }

    [Fact]
    public void LeafCommand_AcceptsBodyOption()
    {
        var get = GetLeaf("build", "get");

        var result = get.Parse("--body {}");

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void AssetUpload_AcceptsSingleFilePathWithoutSplitting()
    {
        var upload = GetLeaf("asset", "upload");
        const string path = "build output/game,server.zip";
        var result = upload.Parse(
            ["--file", path, "--asset-name", "gameserver.zip"],
            Program.NoResponseFileConfig);

        Assert.Empty(result.Errors);
        var option = Assert.IsType<Option<string>>(
            Assert.Single(upload.Options, o => o.Name == "--file"));
        Assert.Equal(path, result.GetValue(option));
    }

    [Theory]
    [InlineData()]
    [InlineData("--file")]
    [InlineData("--file-path", "server.zip")]
    [InlineData("--file", "server.zip", "--file", "other.zip")]
    [InlineData("--file", "server.zip", "other.zip")]
    public void AssetUpload_RejectsMissingOrMultipleFilesAndLegacySyntax(params string[] arguments)
    {
        var result = GetLeaf("asset", "upload").Parse(
            ["--asset-name", "server.zip", .. arguments],
            Program.NoResponseFileConfig);

        Assert.NotEmpty(result.Errors);
    }

    private static Command GetLeaf(string group, string verb)
    {
        var mps = MpsCommandFactory.CreateMpsCommand();
        var groupCommand = mps.Subcommands.Single(c => c.Name == group);
        return groupCommand.Subcommands.Single(c => c.Name == verb);
    }
}
