// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XboxDevCLI.CommandLine;
using Xunit;
using Xunit.Abstractions;

namespace XboxDevCLI.UnitTest;

/// <summary>
/// Covers the PlayFab-specific part of table rendering: unwrapping the response envelope so a
/// command's view describes the payload rather than repeating <c>code</c>/<c>status</c>/<c>data</c>.
/// </summary>
public class ProgramTableOutputTests(ITestOutputHelper output)
{
    private static readonly TableView BuildView = new(
        "BuildSummaries",
        [
            new TableColumn("BUILD ID", "BuildId", 38),
            new TableColumn("NAME", "BuildName", 40),
            new TableColumn("CREATED", "CreationTime", 24),
        ]);

    private const string EnvelopedResponse = """
    {
      "code": 200,
      "status": "OK",
      "data": {
        "BuildSummaries": [
          {
            "BuildId": "6f1a2c30-8f1e-4a1b-9c2d-3e4f5a6b7c8d",
            "BuildName": "combat-sandbox",
            "CreationTime": "2026-09-01T10:14:00Z"
          },
          {
            "BuildId": "a2b3c4d5-e6f7-4890-1234-56789abcdef0",
            "BuildName": "lobby-service",
            "CreationTime": "2026-08-22T17:03:00Z"
          }
        ]
      }
    }
    """;

    [Fact]
    public void FormatTableOutputReadsThroughTheResponseEnvelope()
    {
        var table = Program.FormatTableOutput(EnvelopedResponse, BuildView);
        output.WriteLine(table);

        var lines = table.Split(Environment.NewLine);
        Assert.Equal(4, lines.Length);
        Assert.StartsWith("BUILD ID", lines[0], StringComparison.Ordinal);
        Assert.Contains("combat-sandbox", table, StringComparison.Ordinal);
        Assert.Contains("lobby-service", table, StringComparison.Ordinal);

        // The envelope must not leak into the rendered table.
        Assert.DoesNotContain("status", table, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("200", table, StringComparison.Ordinal);
    }

    /// <summary>
    /// Responses that are already unwrapped still render, so a view does not have to know whether
    /// the caller passed <c>--data-only</c>.
    /// </summary>
    [Fact]
    public void FormatTableOutputAcceptsAnUnwrappedPayload()
    {
        const string bare = """
        { "BuildSummaries": [ { "BuildId": "1", "BuildName": "solo", "CreationTime": "2026-01-01T00:00:00Z" } ] }
        """;

        Assert.Contains("solo", Program.FormatTableOutput(bare, BuildView), StringComparison.Ordinal);
    }
}
