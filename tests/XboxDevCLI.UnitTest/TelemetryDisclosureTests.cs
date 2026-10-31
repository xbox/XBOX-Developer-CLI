// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XboxDevCLI.Telemetry;
using Xunit;

namespace XboxDevCLI.UnitTest;

public class TelemetryDisclosureTests
{
    [Fact]
    public void WriteIfFirstRun_FirstInvocationWritesDisclosureAndSentinel()
    {
        var stateDirectory = CreateTempDirectory();
        try
        {
            using var writer = new StringWriter();

            TelemetryDisclosure.WriteIfFirstRun(stateDirectory, writer);

            var output = writer.ToString();
            var expected =
                $"""
                TELEMETRY
                  XBOX CLI collects anonymous usage telemetry.
                  Learn more or opt out: {ProjectLinks.TelemetryPolicy}
                """ + Environment.NewLine;
            Assert.Equal(expected, output);
            Assert.True(File.Exists(
                Path.Combine(stateDirectory, TelemetryDisclosure.SentinelFileName)));
        }
        finally
        {
            Directory.Delete(stateDirectory, recursive: true);
        }
    }

    [Fact]
    public void FormatDisclosure_StylesHeadingAndBrandWhenAnsiIsEnabled()
    {
        var output = TelemetryDisclosure.FormatDisclosure(useAnsi: true);

        Assert.StartsWith("\u001b[1mTELEMETRY\u001b[0m", output, StringComparison.Ordinal);
        Assert.Contains(
            "\u001b[1;38;2;139;240;0mXBOX\u001b[0m CLI collects anonymous usage telemetry.",
            output,
            StringComparison.Ordinal);
        Assert.EndsWith(
            $"Learn more or opt out: {ProjectLinks.TelemetryPolicy}",
            output,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WriteIfFirstRun_SecondInvocationIsSilent()
    {
        var stateDirectory = CreateTempDirectory();
        try
        {
            using var first = new StringWriter();
            TelemetryDisclosure.WriteIfFirstRun(stateDirectory, first);
            Assert.NotEmpty(first.ToString());

            using var second = new StringWriter();
            TelemetryDisclosure.WriteIfFirstRun(stateDirectory, second);

            Assert.Empty(second.ToString());
        }
        finally
        {
            Directory.Delete(stateDirectory, recursive: true);
        }
    }

    [Fact]
    public void WriteIfFirstRun_CreatesMissingStateDirectory()
    {
        var stateDirectory = Path.Combine(
            Path.GetTempPath(),
            "XboxDevCLI-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            using var writer = new StringWriter();

            TelemetryDisclosure.WriteIfFirstRun(stateDirectory, writer);

            Assert.True(Directory.Exists(stateDirectory));
            Assert.NotEmpty(writer.ToString());
        }
        finally
        {
            if (Directory.Exists(stateDirectory))
            {
                Directory.Delete(stateDirectory, recursive: true);
            }
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
