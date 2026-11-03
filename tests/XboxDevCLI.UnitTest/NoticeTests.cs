// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace XboxDevCLI.UnitTest;

/// <summary>
/// <c>NOTICE.md</c> lists every package the CLI ships, with its version and license, because
/// those licenses require their notices to ship with the CLI. This compares it with the packages
/// in the CLI's own dependency file, so adding, removing, or updating a package without updating
/// <c>NOTICE.md</c> fails here instead of shipping notices that are out of date.
/// </summary>
public sealed partial class NoticeTests
{
    [Fact]
    public void NoticeListsEveryShippedPackageAtItsVersion()
    {
        var shipped = ShippedPackages();
        var notice = File.ReadAllText(Path.Combine(RepositoryRoot(), "NOTICE.md"));
        var listed = ComponentRow().Matches(notice)
            .ToDictionary(match => match.Groups["name"].Value, match => match.Groups["version"].Value);

        var problems = new List<string>();
        foreach (var (name, version) in shipped)
        {
            if (!listed.TryGetValue(name, out var listedVersion))
            {
                problems.Add($"{name} {version} ships, but isn't in the components table.");
            }
            else if (listedVersion != version)
            {
                problems.Add($"{name} {version} ships, but the components table lists {listedVersion}.");
            }

            if (!Regex.IsMatch(notice, $"^- {Regex.Escape(name)}: ", RegexOptions.Multiline))
            {
                problems.Add($"{name} isn't listed under its license.");
            }
        }

        problems.AddRange(listed.Keys.Except(shipped.Keys).Select(name => $"{name} is in the components table, but doesn't ship."));

        Assert.True(
            problems.Count == 0,
            "NOTICE.md doesn't match the packages the CLI ships. Update its components table and license sections "
            + "from each package's license and copyright:\n" + string.Join('\n', problems));
    }

    private static Dictionary<string, string> ShippedPackages()
    {
        var depsFile = Path.Combine(AppContext.BaseDirectory, $"{typeof(Program).Assembly.GetName().Name}.deps.json");
        Assert.True(File.Exists(depsFile), $"Could not find the CLI's dependency file, {depsFile}.");

        using var deps = JsonDocument.Parse(File.ReadAllText(depsFile));
        var packages = deps.RootElement.GetProperty("libraries").EnumerateObject()
            .Where(library => library.Value.GetProperty("type").GetString() == "package")
            .Select(library => library.Name.Split('/'))
            .ToDictionary(parts => parts[0], parts => parts[1]);

        Assert.NotEmpty(packages);
        return packages;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "XboxDevCLI.sln")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "Could not locate the repository root from the test directory.");
        return directory!.FullName;
    }

    // A components table row for a package: a NuGet ID, then a version starting with a digit. The
    // header row and the rows for components that aren't packages don't match.
    [GeneratedRegex(@"^\| (?<name>[A-Za-z0-9]+(?:\.[A-Za-z0-9]+)*) \| (?<version>\d[^\s|]*) \|", RegexOptions.Multiline)]
    private static partial Regex ComponentRow();
}
