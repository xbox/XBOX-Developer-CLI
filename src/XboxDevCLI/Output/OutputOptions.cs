// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using XboxDevCLI.CommandLine;

namespace XboxDevCLI.Output;

/// <summary>
/// The output shape requested for one invocation: the format, whether to strip the PlayFab
/// response envelope, and the table view the executing command declared, if any.
/// </summary>
/// <remarks>
/// This replaces the <c>string outputFormat, bool dataOnly</c> pair that was threaded through the
/// command actions. Carrying the declared view alongside them puts it in every command's hand, so
/// routing a response to a JSON formatter is a visible choice at the call site rather than an
/// invisible omission.
/// <para>
/// It is not, however, a compile-time guarantee: a command can still take an
/// <see cref="OutputOptions"/> and never read <see cref="TableView"/>, which is precisely the
/// <c>file list</c> regression this type was introduced after. The obligation is enforced by the
/// wiring tests, which drive each invocation path with <c>--output table</c> and assert a table
/// comes back.
/// </para>
/// </remarks>
/// <param name="Format">The requested <c>--output</c> value.</param>
/// <param name="DataOnly">Whether <c>--data-only</c> was supplied.</param>
/// <param name="View">
/// The executing command's declared table view, independent of the requested format. Use
/// <see cref="TableView"/> to get the view only when a table was actually asked for.
/// </param>
/// <param name="Paging">
/// The executing command's paging declaration, if it returns one page per call. A table drops
/// the next-page token, so table output uses this to say on stderr that more results exist.
/// </param>
internal sealed record OutputOptions(
    string Format,
    bool DataOnly,
    TableView? View = null,
    CommandPaging? Paging = null)
{
    /// <summary>JSON, pretty-printed, with no declared view. The default for direct calls.</summary>
    internal static OutputOptions Json { get; } = new("json", DataOnly: false);

    /// <summary>Single-line JSON, with no declared view.</summary>
    internal static OutputOptions JsonCompact { get; } = new("json-compact", DataOnly: false);

    /// <summary>Pretty-printed JSON with the PlayFab response envelope stripped.</summary>
    internal static OutputOptions JsonDataOnly { get; } = new("json", DataOnly: true);

    /// <summary>Table output rendered with <paramref name="view"/>.</summary>
    internal static OutputOptions Table(TableView view) => new("table", DataOnly: false, view);

    /// <summary>Reads the format, envelope flag, and declared view for the executing command.</summary>
    internal static OutputOptions From(ParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(parseResult);

        var metadata = CommandMetadataRegistry.GetMetadata(parseResult.CommandResult.Command);
        return new OutputOptions(
            parseResult.GetValue(Program.OutputOption) ?? "json",
            parseResult.GetValue(Program.DataOnlyOption),
            metadata.Table,
            metadata.Paging);
    }

    internal bool IsCompact =>
        Format.Equals("json-compact", StringComparison.OrdinalIgnoreCase);

    internal bool IsTable =>
        Format.Equals("table", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The view to render with, or <c>null</c> to format as JSON. Null unless a table was
    /// requested <em>and</em> the command declared a view, so callers need only check one thing.
    /// </summary>
    internal TableView? TableView => IsTable ? View : null;
}
