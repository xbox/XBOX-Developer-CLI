// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Commands;
using XboxDevCLI.Output;
using Xunit;

namespace XboxDevCLI.UnitTest;

/// <summary>
/// Checks that table columns start at the same terminal cell on every row, whatever script the
/// values are written in.
/// </summary>
/// <remarks>
/// Asserted by measuring the rendered rows with <see cref="TerminalCells"/>, an independent
/// oracle, rather than with the renderer's own width function. Comparing rendered strings for
/// equality would not catch the defect these cover: a row misaligned by full-width characters is
/// a perfectly ordinary string, and only differs from an aligned one once something counts cells.
/// </remarks>
public class TableAlignmentTests
{
    private static readonly TableView BuildView = new(
        "BuildSummaries",
        [
            new TableColumn("BUILD NAME", "BuildName", 40),
            new TableColumn("REGION", "Region", 20),
        ]);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>
    /// The case from the issue: a Japanese build name is six code units and twelve cells, so a
    /// column sized by length is half as wide as the value it holds and pushes REGION right.
    /// </summary>
    [Fact]
    public void FullWidthValuesDoNotPushLaterColumnsRight()
    {
        var payload = Parse("""
        {
          "BuildSummaries": [
            { "BuildName": "my-build", "Region": "EastUs" },
            { "BuildName": "日本語ビルド", "Region": "WestUs" },
            { "BuildName": "中文服务器", "Region": "NorthEurope" },
            { "BuildName": "サーバー", "Region": "JapanEast" }
          ]
        }
        """);

        AssertColumnsLineUp(TableFormatter.Format(payload, BuildView));
    }

    /// <summary>
    /// The mirror case: a combining mark is a code unit that draws nothing, so a column sized by
    /// length is a cell too wide and pulls REGION left.
    /// </summary>
    [Fact]
    public void CombiningMarksDoNotPullLaterColumnsLeft()
    {
        var payload = Parse("""
        {
          "BuildSummaries": [
            { "BuildName": "Jose build", "Region": "EastUs" },
            { "BuildName": "Jose\u0301 build", "Region": "WestUs" },
            { "BuildName": "Jose\u0301\u0301\u0301 build", "Region": "NorthEurope" }
          ]
        }
        """);

        var table = TableFormatter.Format(payload, BuildView);

        AssertColumnsLineUp(table);

        // All three names draw as ten cells, so the column is ten wide however many marks are
        // stacked on the e — the whole point of not counting code units.
        Assert.Equal(10, SeparatorWidths(table)[0]);
    }

    /// <summary>
    /// A ZWJ family sequence is eleven code units, seven scalars, and one glyph. Counting its
    /// scalars would make it three and a half times too wide; counting its code units, eleven.
    /// </summary>
    [Fact]
    public void ZwjEmojiSequenceIsCountedOnceNotPerCodePoint()
    {
        var payload = Parse("""
        {
          "BuildSummaries": [
            { "BuildName": "ab", "Region": "EastUs" },
            { "BuildName": "\uD83D\uDC68\u200D\uD83D\uDC69\u200D\uD83D\uDC67\u200D\uD83D\uDC66", "Region": "WestUs" },
            { "BuildName": "\uD83D\uDC4D\uD83C\uDFFD", "Region": "NorthEurope" },
            { "BuildName": "\uD83C\uDDEF\uD83C\uDDF5", "Region": "JapanEast" }
          ]
        }
        """);

        var table = TableFormatter.Format(payload, BuildView);

        AssertColumnsLineUp(table);

        // BUILD NAME is the widest thing in the column at ten cells, so every emoji row has to
        // fit inside it. A family counted per scalar would not.
        Assert.Equal(10, SeparatorWidths(table)[0]);
    }

    /// <summary>
    /// A lone surrogate pair emoji, mixed with the narrow values it has to line up against.
    /// </summary>
    [Fact]
    public void SurrogatePairsAreCountedAsTwoCellsNotTwoCodeUnits()
    {
        var payload = Parse("""
        {
          "BuildSummaries": [
            { "BuildName": "\uD83D\uDE00\uD83D\uDE00", "Region": "EastUs" },
            { "BuildName": "abcd", "Region": "WestUs" }
          ]
        }
        """);

        var table = TableFormatter.Format(payload, BuildView);

        AssertColumnsLineUp(table);

        // Two emoji and four narrow characters are both four cells, so neither widens the column
        // past its header.
        Assert.Equal(10, SeparatorWidths(table)[0]);
    }

    /// <summary>
    /// Every script this fix has to handle, in one table, across three columns — the arrangement
    /// a misalignment actually shows up in.
    /// </summary>
    [Fact]
    public void MixedScriptRowsStayAligned()
    {
        var view = new TableView(
            "BuildSummaries",
            [
                new TableColumn("BUILD NAME", "BuildName", 40),
                new TableColumn("REGION", "Region", 20),
                new TableColumn("STATE", "State", 12),
            ]);

        var payload = Parse("""
        {
          "BuildSummaries": [
            { "BuildName": "nod-arena-server", "Region": "EastUs", "State": "Deployed" },
            { "BuildName": "日本語ビルド", "Region": "JapanEast", "State": "Deployed" },
            { "BuildName": "Jose\u0301 build", "Region": "WestUs", "State": "Deploying" },
            { "BuildName": "\uD83D\uDC68\u200D\uD83D\uDC69\u200D\uD83D\uDC67\u200D\uD83D\uDC66 crew", "Region": "NorthEurope", "State": "Failed" },
            { "BuildName": "\u1100\u1161\u11A8 build", "Region": "KoreaCentral", "State": "Deployed" },
            { "BuildName": "\u0915\u093E\u0928\u0940 build", "Region": "CentralIndia", "State": "Deployed" },
            { "BuildName": "ｆｕｌｌ width", "Region": "EastUs2", "State": "Deployed" }
          ]
        }
        """);

        AssertColumnsLineUp(TableFormatter.Format(payload, view));
    }

    /// <summary>
    /// The truncation budget has to be counted in the same unit as the column width, or the fix
    /// would move the misalignment rather than remove it: a nine-character CJK value is under a
    /// twelve-code-unit budget and eighteen cells wide.
    /// </summary>
    [Fact]
    public void TruncationBudgetIsCountedInCells()
    {
        var view = new TableView(
            "items",
            [
                new TableColumn("NAME", "name", 12),
                new TableColumn("REGION", "region", 20),
            ]);
        var payload = Parse(
            """{ "items": [ { "name": "日本語ビルドテスト", "region": "EastUs" } ] }""");

        var table = TableFormatter.Format(payload, view);
        var name = ColumnText(table, 2, 0);

        Assert.EndsWith("...", name, StringComparison.Ordinal);
        Assert.True(
            TerminalCells.Measure(name) <= 12,
            $"'{name}' draws in {TerminalCells.Measure(name)} cells but the column holds 12.");
        AssertColumnsLineUp(table);
    }

    /// <summary>
    /// A cluster that will not fit in what the ellipsis leaves is dropped whole, so a truncated
    /// CJK value lands one cell short of its column. Padding has to close that gap in cells too,
    /// or the shortfall becomes the next misalignment.
    /// </summary>
    [Fact]
    public void PaddingClosesTheGapLeftByADroppedWideCluster()
    {
        var view = new TableView(
            "items",
            [
                new TableColumn("NAME", "name", 12),
                new TableColumn("REGION", "region", 20),
            ]);
        var payload = Parse("""
        {
          "items": [
            { "name": "日本語ビルドテスト", "region": "EastUs" },
            { "name": "aaaaaaaaaaaaaaaa", "region": "WestUs" }
          ]
        }
        """);

        var table = TableFormatter.Format(payload, view);

        // Nine narrow characters plus an ellipsis fill all twelve cells; four wide ones plus an
        // ellipsis reach only eleven, because the fifth would have overflowed.
        Assert.Equal(11, TerminalCells.Measure(ColumnText(table, 2, 0)));
        Assert.Equal(12, TerminalCells.Measure(ColumnText(table, 3, 0)));
        AssertColumnsLineUp(table);
    }

    /// <summary>
    /// The seller tables come from the XboxPartner gateway rather than PlayFab, and are rendered
    /// by a different entry point. They have to line up the same way — that is why the layout is
    /// now written once.
    /// </summary>
    /// <remarks>
    /// The wide value is in SELLER ID rather than NAME because NAME is the last column, and the
    /// last column is not padded — a misalignment there leaves no trace to assert on. The gateway
    /// only requires the id to be a non-empty string, so this is the renderer being tested, not a
    /// claim about what Partner Center returns.
    /// </remarks>
    [Fact]
    public void SellerTableAlignsFullWidthValuesToo()
    {
        var table = XboxPartnerTableFormatter.FormatSellers("""
        {
          "sellers": [
            { "sellerId": "1234567", "name": "Nod Publishers" },
            { "sellerId": "日本語スタジオ", "name": "日本語スタジオ" },
            { "sellerId": "Jose\u0301 2468", "name": "Jose\u0301 Interactive" }
          ]
        }
        """);

        AssertColumnsLineUp(table);
    }

    /// <summary>
    /// The seller renderer truncates in cells too. Forty wide characters are forty code units and
    /// eighty cells, so a NAME column of sixty would have let this through untouched and drawn a
    /// row a third wider than the table it sits in.
    /// </summary>
    [Fact]
    public void SellerNamesAreTruncatedByCellsNotCodeUnits()
    {
        var name = string.Concat(Enumerable.Repeat("日本語ビルド", 8)).Substring(0, 40);
        var table = XboxPartnerTableFormatter.FormatSellers(
            $$"""{"sellers":[{"sellerId":"1234567","name":"{{name}}"}]}""");

        var rendered = ColumnText(table, 2, 1);

        Assert.EndsWith("...", rendered, StringComparison.Ordinal);
        Assert.True(
            TerminalCells.Measure(rendered) <= 60,
            $"The name draws in {TerminalCells.Measure(rendered)} cells but NAME holds 60.");
    }

    /// <summary>
    /// The two renderers lay out an identical view identically, which is the property that keeps
    /// the seller tables from drifting away from every other table one fix at a time.
    /// </summary>
    [Fact]
    public void BothRenderersLayOutTheSameViewIdentically()
    {
        var view = new TableView(
            "sellers",
            [
                new TableColumn("SELLER ID", "sellerId", 24),
                new TableColumn("NAME", "name", 60),
            ]);
        const string Response = """
        {
          "sellers": [
            { "sellerId": "日本語", "name": "日本語スタジオ" },
            { "sellerId": "7654321", "name": "Jose\u0301 Interactive" }
          ]
        }
        """;

        Assert.Equal(
            TableFormatter.Format(Parse(Response), view),
            XboxPartnerTableFormatter.FormatSellers(Response));
    }

    /// <summary>
    /// The truncation contract, stated directly: the budget is cells, the ellipsis costs three of
    /// them, and a cluster that will not fit in what is left is dropped rather than split.
    /// </summary>
    [Theory]
    // Nothing to truncate.
    [InlineData("build", 10, "build")]
    [InlineData("日本語", 6, "日本語")]
    // Narrow values spend one cell each, so nine characters and an ellipsis fill twelve.
    [InlineData("aaaaaaaaaaaaaaaa", 12, "aaaaaaaaa...")]
    // Wide values spend two, so only four fit in the nine cells the ellipsis leaves. The fifth is
    // dropped whole rather than split, which is what leaves the result a cell short.
    [InlineData("日本語ビルドテスト", 12, "日本語ビ...")]
    // Below the ellipsis' own width there is no room to signal the cut.
    [InlineData("abcdef", 3, "abc")]
    [InlineData("日本語", 3, "日")]
    [InlineData("日本語", 2, "日")]
    [InlineData("日本語", 1, "")]
    // A value one cell too wide still loses three to the ellipsis.
    [InlineData("abcdef", 5, "ab...")]
    [InlineData("abcdef", 4, "a...")]
    public void TruncateSpendsACellBudget(string value, int width, string expected)
    {
        var truncated = TableFormatter.Truncate(value, width);

        Assert.Equal(expected, truncated);
        Assert.True(
            TerminalCells.Measure(truncated) <= width,
            $"'{truncated}' draws in {TerminalCells.Measure(truncated)} cells, over the {width} " +
            "asked for.");
    }

    /// <summary>
    /// A width of zero cannot reach a rendered column — every view's header is at least one cell
    /// wide — but the guard is what makes that true by construction rather than by argument.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void TruncateToNoWidthYieldsNothing(int width) =>
        Assert.Equal(string.Empty, TableFormatter.Truncate("日本語", width));

    /// <summary>
    /// Asserts every row's cells sit inside the columns the separator row declares.
    /// </summary>
    /// <remarks>
    /// The separator row is the one line whose layout is unambiguous — runs of dashes divided by
    /// the two-space gutter — so the column widths are read from it and every other line is
    /// checked against them. Reading boundaries out of the content rows instead would mean
    /// guessing where padding ends, which a value containing two spaces defeats.
    /// <para>
    /// The check is that both gutter cells after each column are blank. That catches a column
    /// rendered too wide, too narrow, or ending in a wide cluster that straddles the boundary —
    /// the last of which an offset comparison alone would miss.
    /// </para>
    /// </remarks>
    private static void AssertColumnsLineUp(string table)
    {
        var lines = table.Split(Environment.NewLine);
        var widths = SeparatorWidths(table);

        Assert.True(widths.Count > 1, "The fixture needs more than one column to line up.");

        foreach (var line in lines)
        {
            var cells = ExpandToCells(line);
            var offset = 0;

            for (var column = 0; column < widths.Count - 1; column++)
            {
                var gutter = offset + widths[column];

                Assert.True(
                    gutter + 1 < cells.Count,
                    $"Line '{line}' ends at cell {cells.Count}, before column {column + 1} " +
                    $"should start at cell {gutter + 2}.{Environment.NewLine}{table}");

                for (var cell = gutter; cell < gutter + 2; cell++)
                {
                    Assert.True(
                        cells[cell] == " ",
                        $"Cell {cell} of line '{line}' should be the gutter after column " +
                        $"{column}, but holds '{cells[cell]}'.{Environment.NewLine}{table}");
                }

                offset = gutter + 2;
            }
        }
    }

    /// <summary>The width of each column, read off the separator row's runs of dashes.</summary>
    private static IReadOnlyList<int> SeparatorWidths(string table)
    {
        var separator = table.Split(Environment.NewLine)[1];

        Assert.Matches("^-+(  -+)*$", separator);

        return separator.Split("  ").Select(run => run.Length).ToArray();
    }

    /// <summary>
    /// One entry per terminal cell the line occupies: the cluster drawn there, or the
    /// continuation marker for the second cell of a double-width one.
    /// </summary>
    private static IReadOnlyList<string> ExpandToCells(string line)
    {
        var cells = new List<string>();
        var remaining = line.AsSpan();

        while (!remaining.IsEmpty)
        {
            var length = StringInfo.GetNextTextElementLength(remaining);
            var cluster = remaining[..length].ToString();
            var width = TerminalCells.Measure(cluster);

            if (width > 0)
            {
                cells.Add(cluster);
                for (var extra = 1; extra < width; extra++)
                {
                    cells.Add(Continuation);
                }
            }
            else if (cells.Count > 0)
            {
                // A zero-width cluster composes onto whatever was drawn last rather than
                // occupying a cell of its own.
                cells[^1] += cluster;
            }

            remaining = remaining[length..];
        }

        return cells;
    }

    /// <summary>The text of one column, without the padding that follows it.</summary>
    private static string ColumnText(string table, int line, int column)
    {
        var widths = SeparatorWidths(table);
        var cells = ExpandToCells(table.Split(Environment.NewLine)[line]);
        var offset = widths.Take(column).Sum(width => width + 2);

        return string.Concat(cells
            .Skip(offset)
            .Take(Math.Min(widths[column], cells.Count - offset))
            .Where(cell => cell != Continuation))
            .TrimEnd();
    }

    /// <summary>Marks the second cell of a cluster that occupies two of them.</summary>
    private const string Continuation = "\u0000";
}
