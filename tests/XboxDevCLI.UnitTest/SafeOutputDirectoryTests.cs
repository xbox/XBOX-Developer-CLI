// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XboxDevCLI.CommandLine;
using Xunit;

namespace XboxDevCLI.UnitTest;

public class SafeOutputDirectoryTests
{
    [Theory]
    [InlineData("/tmp", true)]
    [InlineData("/var", true)]
    [InlineData("/etc", true)]
    [InlineData("/tmp/", true)]
    [InlineData("/var/folders", false)]
    [InlineData("/work", false)]
    [InlineData("/", false)]
    public void IsSystemLink_AllowsOnlyMacOSPrivateLinks(string path, bool expected)
    {
        Assert.Equal(expected && OperatingSystem.IsMacOS(), SafeOutputDirectory.IsSystemLink(path));
    }

    [Theory]
    [InlineData("/tmp/")]
    [InlineData("/var/")]
    public void ValidateExistingComponents_AcceptsSystemLinkWithTrailingSlash(string path)
    {
        SafeOutputDirectory.ValidateExistingComponents(path);
    }
}
