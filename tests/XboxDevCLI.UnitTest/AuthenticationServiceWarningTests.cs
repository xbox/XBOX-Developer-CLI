// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

public class AuthenticationServiceWarningTests
{
    [Fact]
    public void WriteLinuxUnencryptedCacheFallbackWarning_WritesClearWarning()
    {
        using var stderr = new StringWriter();

        AuthenticationService.WriteLinuxUnencryptedCacheFallbackWarning(stderr);

        var warning = stderr.ToString();
        Assert.Contains("unencrypted token cache file", warning, StringComparison.Ordinal);
        Assert.Contains("filesystem permissions", warning, StringComparison.Ordinal);
    }
}
