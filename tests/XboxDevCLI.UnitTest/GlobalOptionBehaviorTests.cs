// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public class GlobalOptionBehaviorTests
{
    [Fact]
    public void WriteVerbose_Enabled_WritesPrefixedMessageToStderr()
    {
        var originalError = Console.Error;
        var errorWriter = new StringWriter();
        Console.SetError(errorWriter);
        try
        {
            Program.WriteVerbose(verbose: true, () => "resolving title");

            var stderr = errorWriter.ToString();
            Assert.Contains("[verbose]", stderr, StringComparison.Ordinal);
            Assert.Contains("resolving title", stderr, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    [Fact]
    public void WriteVerbose_Disabled_WritesNothing()
    {
        var originalError = Console.Error;
        var errorWriter = new StringWriter();
        Console.SetError(errorWriter);
        try
        {
            Program.WriteVerbose(verbose: false, () => "should not appear");

            Assert.Equal(string.Empty, errorWriter.ToString());
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    [Fact]
    public void WriteVerbose_Disabled_DoesNotInvokeMessageFactory()
    {
        // The message factory must not run when verbose is off, so no (potentially large)
        // string — e.g. a full JSON request body — is allocated on the hot path.
        var invoked = false;
        Program.WriteVerbose(verbose: false, () =>
        {
            invoked = true;
            return "expensive";
        });

        Assert.False(invoked, "message factory must not be evaluated when verbose is disabled.");
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("--version", "--output", "table")]
    public async Task VersionFlag_ReturnsZeroAndPrintsVersion(params string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var outWriter = new StringWriter();
        var errorWriter = new StringWriter();
        Console.SetOut(outWriter);
        Console.SetError(errorWriter);
        try
        {
            var result = await Program.Main(args);

            // --version is handled by the framework: it must exit cleanly (0) and print a
            // non-empty version line. The exact value comes from the entry assembly (the CLI's
            // own version when published), which differs under the test host, so we don't assert
            // on the literal value here — the published value is covered by a build smoke.
            Assert.Equal(0, result);
            Assert.False(string.IsNullOrWhiteSpace(outWriter.ToString()),
                "--version should print a version string to stdout.");
            Assert.DoesNotContain(
                Program.TableUnavailableMessage, errorWriter.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}
