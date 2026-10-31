// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using NuGet.Versioning;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

public class CliIdentityTests
{
    [Fact]
    public void VersionIsTheInformationalVersionWithoutBuildMetadata()
    {
        Assert.DoesNotContain('+', CliIdentity.Version);
        Assert.True(NuGetVersion.TryParse(CliIdentity.Version, out _));
        Assert.StartsWith(CliIdentity.Version, CliUpdateChecker.CurrentVersion, StringComparison.Ordinal);
        Assert.Equal($"XboxDevCLI/{CliIdentity.Version}", CliIdentity.UserAgent);
    }
}
