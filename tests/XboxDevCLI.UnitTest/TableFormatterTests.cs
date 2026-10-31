// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using Xunit;

namespace XboxDevCLI.UnitTest;

public class TableFormatterTests
{
    private static readonly TableView SellerView = new(
        "sellers",
        [
            new TableColumn("SELLER ID", "sellerId", 24),
            new TableColumn("NAME", "name", 40),
        ]);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void FormatRendersHeaderSeparatorAndRows()
    {
        var payload = Parse("""
        {
          "sellers": [
            { "sellerId": "1234567", "name": "Nod Publishers" },
            { "sellerId": "7654321", "name": "Lucerne Publishing" }
          ]
        }
        """);

        var lines = TableFormatter.Format(payload, SellerView).Split(Environment.NewLine);

        Assert.Equal(4, lines.Length);
        Assert.StartsWith("SELLER ID", lines[0], StringComparison.Ordinal);
        Assert.Contains("---", lines[1], StringComparison.Ordinal);
        Assert.Contains("Nod Publishers", lines[2], StringComparison.Ordinal);
        Assert.Contains("Lucerne Publishing", lines[3], StringComparison.Ordinal);
    }

    [Fact]
    public void FormatReportsEmptyCollectionRatherThanBareHeaders()
    {
        var payload = Parse("""{ "sellers": [] }""");

        Assert.Equal("No results.", TableFormatter.Format(payload, SellerView));
    }

    /// <summary>
    /// The failure Azure CLI's generic flattener produces instead: a nested-only result there
    /// prints a blank line and exits successfully, which reads as "no results".
    /// </summary>
    [Fact]
    public void FormatThrowsWhenCollectionIsMissing()
    {
        var payload = Parse("""{ "somethingElse": [] }""");

        var exception = Assert.Throws<InvalidDataException>(
            () => TableFormatter.Format(payload, SellerView));

        Assert.Contains("sellers", exception.Message, StringComparison.Ordinal);
        Assert.Contains("--output json", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A cut that falls inside a text element must not split it.
    /// </summary>
    /// <remarks>
    /// NAME is 40 wide, so 37 characters are kept and 36 'a's put the emoji across indices 36–37.
    /// A UTF-16 slice at 37 therefore keeps only its high half and emits a lone surrogate, which
    /// the console shows as a replacement character — valid data made to look corrupt. This is the
    /// fixture that distinguishes a grapheme-safe truncation from an index slice.
    /// </remarks>
    [Fact]
    public void FormatTruncatesWithoutSplittingASurrogatePair()
    {
        var name = new string('a', 36) + "\U0001F600" + new string('b', 10);
        var payload = Parse($$"""{ "sellers": [ { "sellerId": "1", "name": "{{name}}" } ] }""");

        var rendered = TableFormatter.Format(payload, SellerView);

        Assert.DoesNotContain('\uFFFD', rendered);
        SurrogateAssert.NoLoneSurrogates(rendered);
    }

    /// <summary>
    /// A text element that fits inside the width is kept whole, not dropped.
    /// </summary>
    /// <remarks>
    /// The companion to the split case: 35 'a's plus one emoji is exactly the 37 kept characters,
    /// so a correct truncation retains that emoji and discards the next. Without this, a
    /// truncation that simply dropped every multi-unit element would satisfy the split test.
    /// </remarks>
    [Fact]
    public void FormatKeepsATextElementThatFitsExactly()
    {
        var name = new string('a', 35) + "\U0001F600\U0001F600" + new string('b', 10);
        var payload = Parse($$"""{ "sellers": [ { "sellerId": "1", "name": "{{name}}" } ] }""");

        var rendered = TableFormatter.Format(payload, SellerView);

        SurrogateAssert.NoLoneSurrogates(rendered);
        Assert.Contains("\U0001F600", rendered, StringComparison.Ordinal);
        Assert.Contains("...", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// A scalar collection renders the element itself, for responses that return bare strings.
    /// </summary>
    /// <remarks>
    /// This path backs `mps container-image list` and `list-tags`. It was previously unexercised
    /// by any renderer test — every other case uses object rows — so a regression could have
    /// emitted empty cells while the declaration validator still passed.
    /// </remarks>
    [Fact]
    public void ScalarCollectionRendersTheElementItself()
    {
        var view = new TableView("Images", [new TableColumn("IMAGE", string.Empty, 72)]);
        var payload = Parse("""{ "Images": [ "nod/arena-server:1.4", "nod/arena-server:latest" ] }""");

        var table = TableFormatter.Format(payload, view);
        var lines = table.Split(Environment.NewLine);

        Assert.Equal(4, lines.Length);
        Assert.Contains("IMAGE", lines[0], StringComparison.Ordinal);
        Assert.Equal("nod/arena-server:1.4", lines[2].Trim());
        Assert.Equal("nod/arena-server:latest", lines[3].Trim());
    }

    /// <summary>
    /// Pairing an element-itself column with another is a declaration bug, not a response
    /// problem: the element is the whole value, so a second column could only repeat it.
    /// </summary>
    [Fact]
    public void ScalarCollectionWithMoreThanOneColumnIsRejected()
    {
        var view = new TableView(
            "Images",
            [
                new TableColumn("IMAGE", string.Empty, 72),
                new TableColumn("TAG", "Tag", 20),
            ]);

        var exception = Assert.Throws<InvalidOperationException>(
            () => TableFormatter.Format(Parse("""{ "Images": [ "a" ] }"""), view));

        Assert.Contains("exactly one column", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatThrowsWhenCollectionIsAScalar()
    {
        // An object collection is legitimate — some responses key their records by name — but a
        // bare scalar has no records to enumerate.
        var payload = Parse("""{ "sellers": "1234567" }""");

        var exception = Assert.Throws<InvalidDataException>(
            () => TableFormatter.Format(payload, SellerView));

        Assert.Contains("rather than an array or an object", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Responses that key their records by id or name — file metadata is keyed by file name — are
    /// still a list of records to a reader, so the values are enumerated.
    /// </summary>
    [Fact]
    public void FormatRendersAKeyedCollectionByEnumeratingItsValues()
    {
        var payload = Parse(
            """{ "sellers": { "a": { "sellerId": "1", "name": "One" }, "b": { "sellerId": "2", "name": "Two" } } }""");

        var lines = TableFormatter.Format(payload, SellerView).Split(Environment.NewLine);

        Assert.Equal(4, lines.Length);
        Assert.Contains("One", lines[2], StringComparison.Ordinal);
        Assert.Contains("Two", lines[3], StringComparison.Ordinal);
    }

    [Fact]
    public void FormatRendersMissingFieldsAsPlaceholderRatherThanFailing()
    {
        var payload = Parse("""{ "sellers": [ { "sellerId": "1234567" } ] }""");

        var lines = TableFormatter.Format(payload, SellerView).Split(Environment.NewLine);

        Assert.Contains("1234567", lines[2], StringComparison.Ordinal);
        Assert.EndsWith("-", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void FormatReadsNestedPropertiesThroughDottedPaths()
    {
        var view = new TableView(
            "items",
            [new TableColumn("REGION", "Metadata.Region", 20)]);
        var payload = Parse("""{ "items": [ { "Metadata": { "Region": "EastUs" } } ] }""");

        Assert.Contains("EastUs", TableFormatter.Format(payload, view), StringComparison.Ordinal);
    }

    [Fact]
    public void FormatRendersNonStringScalarsReadably()
    {
        var view = new TableView(
            "items",
            [
                new TableColumn("COUNT", "count", 10),
                new TableColumn("ACTIVE", "active", 10),
            ]);
        var payload = Parse("""{ "items": [ { "count": 42, "active": true } ] }""");

        var lines = TableFormatter.Format(payload, view).Split(Environment.NewLine);

        Assert.Contains("42", lines[2], StringComparison.Ordinal);
        Assert.Contains("true", lines[2], StringComparison.Ordinal);
    }

    /// <summary>
    /// A value carrying a newline or an ANSI escape must not be able to break the column layout
    /// or inject styling into output that a human is reading.
    /// </summary>
    [Fact]
    public void FormatNeutralizesControlCharactersInValues()
    {
        var payload = Parse("""
        { "sellers": [ { "sellerId": "1", "name": "Line\u001b[31mBreak\nName" } ] }
        """);

        var table = TableFormatter.Format(payload, SellerView);

        Assert.Equal(3, table.Split(Environment.NewLine).Length);
        Assert.DoesNotContain('\u001b', table);
        Assert.DoesNotContain('\n', table.Split(Environment.NewLine)[2]);
    }

    [Fact]
    public void FormatNeutralizesUnicodeSeparatorsAndBidiControlsInValues()
    {
        var payload = Parse("""
        { "sellers": [ { "sellerId": "1", "name": "One\u2028Two\u2029Three\u202eabc" } ] }
        """);

        var table = TableFormatter.Format(payload, SellerView);

        Assert.Equal(3, table.Split(Environment.NewLine).Length);
        Assert.DoesNotContain('\u2028', table);
        Assert.DoesNotContain('\u2029', table);
        Assert.DoesNotContain('\u202e', table);
        Assert.Contains("One Two Three abc", table, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatTruncatesValuesBeyondTheColumnWidth()
    {
        var longName = new string('x', 80);
        var payload = Parse($$"""{ "sellers": [ { "sellerId": "1", "name": "{{longName}}" } ] }""");

        var table = TableFormatter.Format(payload, SellerView);

        Assert.DoesNotContain(longName, table, StringComparison.Ordinal);
        Assert.Contains("...", table, StringComparison.Ordinal);
    }

    #region Record views

    private static readonly TableView BuildView = TableView.Record(
        [
            new TableColumn("BUILD ID", "BuildId", 38),
            new TableColumn("NAME", "BuildName", 40),
            new TableColumn("SERVERS PER VM", "MultiplayerServerCountPerVm", 14),
        ]);

    /// <summary>
    /// A record renders as a one-row table — the same headers and separator a collection gets, so
    /// a reader never has to work out which shape the command returned.
    /// </summary>
    [Fact]
    public void RecordViewRendersAOneRowTable()
    {
        var payload = Parse("""
        {
          "BuildId": "00bde0f5-c1d2-4ea2-acdf-91e92bc07f63",
          "BuildName": "Nod Arena",
          "MultiplayerServerCountPerVm": 4
        }
        """);

        var table = TableFormatter.Format(payload, BuildView);
        var lines = table.Split(Environment.NewLine);

        Assert.Equal(3, lines.Length);
        Assert.Contains("BUILD ID", lines[0], StringComparison.Ordinal);
        Assert.Contains("NAME", lines[0], StringComparison.Ordinal);
        Assert.Contains("SERVERS PER VM", lines[0], StringComparison.Ordinal);
        Assert.True(
            lines[1].All(character => character is '-' or ' '),
            $"Expected a separator row, got '{lines[1]}'");
        Assert.Contains("00bde0f5-c1d2-4ea2-acdf-91e92bc07f63", lines[2], StringComparison.Ordinal);
        Assert.Contains("Nod Arena", lines[2], StringComparison.Ordinal);
        Assert.Contains("4", lines[2], StringComparison.Ordinal);
    }

    /// <summary>An absent field reads as missing, exactly as it does in a collection row.</summary>
    [Fact]
    public void RecordViewRendersAbsentFieldsAsDashes()
    {
        var table = TableFormatter.Format(Parse("""{ "BuildId": "abc" }"""), BuildView);
        var row = table.Split(Environment.NewLine)[2];

        Assert.Contains('-', row);
    }

    [Fact]
    public void RecordViewReadsANestedRecordWhenTheViewNamesOne()
    {
        var view = TableView.Record(
            "Change",
            [
                new TableColumn("REQUEST ID", "RequestId", 38),
                new TableColumn("APPROVED", "WasApproved", 8),
            ]);
        var payload = Parse("""{ "Change": { "RequestId": "req-1", "WasApproved": true } }""");

        var table = TableFormatter.Format(payload, view);

        Assert.Contains("REQUEST ID", table, StringComparison.Ordinal);
        Assert.Contains("req-1", table, StringComparison.Ordinal);
        Assert.Contains("true", table, StringComparison.Ordinal);
    }

    /// <summary>
    /// A named record that is missing must fail rather than render a column of dashes, which would
    /// be indistinguishable from a record whose fields are all empty.
    /// </summary>
    [Fact]
    public void RecordViewFailsWhenTheNamedRecordIsAbsent()
    {
        var view = TableView.Record("Change", [new TableColumn("REQUEST ID", "RequestId", 38)]);

        var exception = Assert.Throws<InvalidDataException>(
            () => TableFormatter.Format(Parse("""{ "Other": {} }"""), view));

        Assert.Contains("does not contain the expected 'Change'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordViewFailsWhenTheNamedRecordIsNotAnObject()
    {
        var view = TableView.Record("Change", [new TableColumn("REQUEST ID", "RequestId", 38)]);

        var exception = Assert.Throws<InvalidDataException>(
            () => TableFormatter.Format(Parse("""{ "Change": [] }"""), view));

        Assert.Contains("rather than an object", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordViewTruncatesValuesBeyondTheColumnWidth()    {
        var longName = new string('x', 80);
        var payload = Parse($$"""{ "BuildId": "1", "BuildName": "{{longName}}" }""");

        var table = TableFormatter.Format(payload, BuildView);

        Assert.DoesNotContain(longName, table, StringComparison.Ordinal);
        Assert.Contains("...", table, StringComparison.Ordinal);
    }

    /// <summary>
    /// Record cells go through the same control-character sanitization as collection rows.
    /// </summary>
    /// <remarks>
    /// A newline would break the one-field-per-line layout and an ANSI escape would inject
    /// terminal styling into output documented as plain. Collection rows were normalized in
    /// <c>Render</c>; the record path needs the same treatment because it does not go through it.
    /// </remarks>
    [Fact]
    public void RecordViewSanitizesControlCharactersInCells()
    {
        var payload = Parse("""{ "BuildId": "a\u001b[31mb", "BuildName": "one\ntwo\tthree" }""");

        var table = TableFormatter.Format(payload, BuildView);

        Assert.DoesNotContain('\u001b', table);
        Assert.Equal(3, table.Split(Environment.NewLine).Length);
        Assert.Contains("one two three", table, StringComparison.Ordinal);
    }
    /// <summary>
    /// An explicitly null collection means "no value", so it reports no results rather than
    /// failing. The JSON path prints an empty result happily for the same response.
    /// </summary>
    [Fact]
    public void NullCollectionReportsNoResultsRatherThanFailing()
    {
        var table = TableFormatter.Format(Parse("""{ "sellers": null }"""), SellerView);

        Assert.Equal(TableFormatter.EmptyNotice, table);
    }

    /// <summary>
    /// A missing collection still fails: reporting "no results" would present a field the service
    /// renamed as an empty account, which is worse than an error naming the property.
    /// </summary>
    [Fact]
    public void AbsentCollectionStillFails()
    {
        var exception = Assert.Throws<InvalidDataException>(
            () => TableFormatter.Format(Parse("""{ "other": [] }"""), SellerView));

        Assert.Contains("does not contain the expected 'sellers'", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>A column marked for redaction masks its value.</summary>
    /// <remarks>
    /// The JSON formats still return the secret — the command exists to retrieve it — but a table
    /// is the format that ends up in screenshots, pasted chat, and scrollback.
    /// </remarks>
    [Fact]
    public void RedactedColumnMasksItsValue()
    {
        var view = TableView.Record(
            [
                new TableColumn("USERNAME", "Username", 32),
                new TableColumn("PASSWORD", "Password", 12, Redact: true),
            ]);
        var payload = Parse("""{ "Username": "nodpublishers", "Password": "s3cr3t-value" }""");

        var table = TableFormatter.Format(payload, view);

        Assert.DoesNotContain("s3cr3t-value", table, StringComparison.Ordinal);
        Assert.Contains(TableColumn.RedactedCell, table, StringComparison.Ordinal);
        Assert.Contains("nodpublishers", table, StringComparison.Ordinal);
    }

    /// <summary>
    /// An absent secret reads as absent, not as a mask: showing <c>********</c> would claim a
    /// credential exists where the response carried none.
    /// </summary>
    [Fact]
    public void RedactedColumnStillReportsAnAbsentValueAsMissing()
    {
        var view = TableView.Record([new TableColumn("PASSWORD", "Password", 12, Redact: true)]);

        var table = TableFormatter.Format(Parse("""{ "Username": "nodpublishers" }"""), view);

        Assert.DoesNotContain(TableColumn.RedactedCell, table, StringComparison.Ordinal);
    }

    /// <summary>
    /// A secret whose value is literally <c>"-"</c> is still masked.
    /// </summary>
    /// <remarks>
    /// Redaction keys off the JSON value kind, not the rendered text. An earlier version compared
    /// against the <c>"-"</c> placeholder used for absent fields, which would have emitted this
    /// secret in the clear.
    /// </remarks>
    [Fact]
    public void RedactedColumnMasksAValueThatLooksLikeThePlaceholder()
    {
        var view = TableView.Record([new TableColumn("PASSWORD", "Password", 12, Redact: true)]);

        var table = TableFormatter.Format(Parse("""{ "Password": "-" }"""), view);

        Assert.Contains(TableColumn.RedactedCell, table, StringComparison.Ordinal);
    }

    /// <summary>
    /// A scalar collection honours <c>Redact</c> too.
    /// </summary>
    /// <remarks>
    /// The element-itself branch used to return before the masking check, so a view over a
    /// collection of secrets would have declared redaction and printed the values anyway — a
    /// declaration the renderer ignores, which is the defect this whole design exists to remove.
    /// </remarks>
    [Fact]
    public void RedactedScalarCollectionMasksEachElement()
    {
        var view = new TableView("Tokens", [new TableColumn("TOKEN", string.Empty, 20, Redact: true)]);

        var table = TableFormatter.Format(Parse("""{ "Tokens": [ "s3cr3t-a", "s3cr3t-b" ] }"""), view);

        Assert.DoesNotContain("s3cr3t", table, StringComparison.Ordinal);
        Assert.Equal(2, table.Split(Environment.NewLine).Count(line => line.Contains(TableColumn.RedactedCell, StringComparison.Ordinal)));
    }

    /// <summary>Redaction happens in every table shape, not just single records.</summary>
    [Fact]
    public void RedactedColumnMasksInACollectionToo()
    {
        var view = new TableView(
            "creds",
            [
                new TableColumn("NAME", "name", 20),
                new TableColumn("PASSWORD", "password", 12, Redact: true),
            ]);
        var payload = Parse("""{ "creds": [ { "name": "a", "password": "p1" }, { "name": "b", "password": "p2" } ] }""");

        var table = TableFormatter.Format(payload, view);

        Assert.DoesNotContain("p1", table, StringComparison.Ordinal);
        Assert.DoesNotContain("p2", table, StringComparison.Ordinal);
    }

    #endregion
}