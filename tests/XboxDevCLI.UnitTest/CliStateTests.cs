// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection(nameof(CliStateEnvironmentCollection))]
public class CliStateTests
{
    [Fact]
    public void GetDefaultDirectory_UsesADataFolderThatDoesNotExistYet()
    {
        // The data folder comes from XDG_DATA_HOME only on Linux, where CI runs.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var original = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var dataFolder = Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", dataFolder);

            Assert.Equal(Path.Combine(dataFolder, "XboxDevCLI"), CliState.GetDefaultDirectory());
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", original);
        }
    }
}

/// <summary>Tests in this collection change the environment variables that locate the CLI's state.</summary>
[CollectionDefinition(nameof(CliStateEnvironmentCollection), DisableParallelization = true)]
public class CliStateEnvironmentCollection;
