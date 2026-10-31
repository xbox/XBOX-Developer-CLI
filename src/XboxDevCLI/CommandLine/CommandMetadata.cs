// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;

namespace XboxDevCLI.CommandLine;

/// <summary>A command's CLI compatibility commitment.</summary>
internal enum CommandStage
{
    /// <summary>The normal backward-compatibility expectations apply.</summary>
    Stable,
    /// <summary>A working, shippable command whose CLI contract may change or be removed.</summary>
    Experimental,
}

/// <summary>
/// The Help category a root-level command belongs to. Replaces the hardcoded name lists that
/// previously drove Help grouping, so a newly registered command is categorized deliberately
/// rather than silently falling into an "additional commands" bucket.
/// </summary>
internal enum HelpCategory
{
    /// <summary>Uncategorized. Surfaced by tests so new commands must opt into a category.</summary>
    None = 0,

    /// <summary>The primary task-oriented commands users reach for.</summary>
    Core,

    /// <summary>Diagnostics and self-service commands that support the core workflow.</summary>
    Support,
}

/// <summary>
/// Which part of the command tree an option is meaningful for.
/// </summary>
/// <remarks>
/// Options such as <c>--playfab-title-id</c> are registered recursively on the root so they parse
/// both before and after a subcommand — the placement flexibility the design guideline asks for,
/// and the form the documentation's examples use. Registration is
/// therefore deliberately broader than relevance, and Help uses this audience to show an option
/// only where it actually does something.
/// </remarks>
internal enum OptionAudience
{
    /// <summary>Meaningful anywhere in the tree.</summary>
    Universal = 0,

    /// <summary>Meaningful only within the <c>playfab</c> subtree.</summary>
    PlayFab,
}

/// <summary>
/// Explicit safety classification for commands whose effect cannot be derived from a PlayFab
/// operation path.
/// </summary>
internal enum CommandSafety
{
    /// <summary>Reads only: no remote state changes, and no local state changes either.</summary>
    ReadOnly,

    /// <summary>Writes local CLI state, such as the selected seller.</summary>
    LocalState,

    /// <summary>
    /// Changes remote state even though no invoked PlayFab operation says so. This exists for
    /// commands that mutate through something other than the operation they call —
    /// <c>mps asset upload</c> asks <c>GetAssetUploadUrl</c> for a storage URL and then writes the
    /// asset over it, so classifying it from its one PlayFab operation would report a write as a
    /// read.
    /// </summary>
    Mutates,

    /// <summary>
    /// The effect is chosen by the caller at run time, so it cannot be stated in advance. Reserved
    /// for the raw passthrough, <c>playfab api</c>, whose path is an argument.
    /// </summary>
    DependsOnApiPath,

    /// <summary>Changes remote state through a service other than PlayFab.</summary>
    RemoteMutation,

    /// <summary>
    /// Changes this PC outside the CLI, such as its XBOX sandbox, which affects every user and app
    /// on it, or the XBOX apps running on it. Not CLI state, and not remote state.
    /// </summary>
    MachineState,
}

/// <summary>
/// A column in a command's table view.
/// </summary>
/// <param name="Header">The column heading, conventionally upper case.</param>
/// <param name="Property">
/// The property to read from each record, as it appears on the wire.
/// <para>
/// Write this as <c>nameof(SomeResponse.SomeField)</c> rather than a string literal. The
/// generated models name every property exactly as the wire does, so <c>nameof</c> produces the
/// right string <em>and</em> binds the declaration to the model: if a swagger regeneration
/// renames or removes the field, this line stops compiling, instead of silently rendering a
/// column of dashes until a test catches it.
/// </para>
/// <para>
/// Dotted paths walk nested objects — compose them the same way, as
/// <c>$"{nameof(GroupBlock.Entity)}.{nameof(EntityWithLineage.Key)}.{nameof(EntityKey.Id)}"</c>.
/// Literals are reserved for responses with no generated model, which today means only the
/// XboxPartner gateway commands.
/// </para>
/// <para>
/// Empty means the element <em>is</em> the value, for collections of bare strings such as
/// <c>ListContainerImages</c>' <c>Images</c>. Such a view must declare exactly one column: there
/// is only one value per element, so a second column could only repeat it.
/// </para>
/// </param>
/// <param name="MaximumWidth">
/// Width, in terminal cells, at which the cell is truncated with an ellipsis. Cells rather than
/// UTF-16 code units because that is what a column occupies on screen: a CJK value fits half as
/// many characters into the same column as a Latin one, and counting them the same way is what
/// pushes the next column out of line.
/// </param>
/// <param name="Redact">
/// Whether to mask the value instead of printing it. Set this for credentials and other secrets.
/// </param>
/// <param name="Transform">
/// Optional table-only projection of a scalar field, such as the filename of a CDN URL.
/// JSON output retains the original field.
/// </param>
/// <remarks>
/// Redaction applies to table output only. The JSON formats still return the real value — the
/// command exists to retrieve it — but a table is the format people paste into chat, screenshot,
/// and pipe into logs, so printing a reusable secret there is a meaningfully larger exposure for
/// no benefit the JSON default does not already provide.
/// </remarks>
internal sealed record TableColumn(
    string Header,
    string Property,
    int MaximumWidth = 40,
    bool Redact = false,
    Func<string, string>? Transform = null)
{
    /// <summary>What a redacted cell shows in place of the value.</summary>
    internal const string RedactedCell = "********";

    /// <summary>Whether this column reads the array element itself rather than a property of it.</summary>
    internal bool IsElementItself => string.IsNullOrEmpty(Property);
}

/// <summary>Whether a view describes many records or a single one.</summary>
internal enum TableShape
{
    /// <summary>The source property holds many records; each becomes a row.</summary>
    Collection,

    /// <summary>
    /// The source is one record, rendered as a one-row table. The distinction matters because an
    /// object-valued source is otherwise ambiguous: <c>file list</c> returns records keyed by
    /// file name, where the object is a map of rows, not a row itself.
    /// </summary>
    Record,
}

/// <summary>
/// Declares that a command can render its response as a table, and which columns to show.
/// </summary>
/// <remarks>
/// Table support is declared here rather than inferred from the command's path, for the same
/// reason <see cref="HelpCategory"/> and <see cref="CommandSafety"/> are: a central list keyed on
/// command names silently goes stale as the surface is renamed, and the canonicalization of the
/// PlayFab surface has already renamed most of it once.
/// <para>
/// A view still has to be declared per command: the columns worth showing are an editorial choice,
/// and a generic flattener would have to drop nested fields to invent them — the failure mode that
/// makes <c>az ... --output table</c> silently lossy. What the shape decides is only how the
/// chosen columns are laid out.
/// </para>
/// </remarks>
/// <param name="Collection">
/// The response property holding the record or records, written as
/// <c>nameof(SomeResponse.SomeProperty)</c> so a regeneration that renames it breaks the build.
/// For PlayFab commands this is read from inside the response envelope's <c>data</c> object.
/// Empty means the payload itself is the record, which is the usual shape for a <c>get</c> whose
/// fields sit at the top of <c>data</c>; it is only valid for <see cref="TableShape.Record"/>.
/// </param>
/// <param name="Columns">The columns to render, in order.</param>
/// <param name="Shape">Whether <paramref name="Collection"/> holds many records or one.</param>
internal sealed record TableView(
    string Collection,
    IReadOnlyList<TableColumn> Columns,
    TableShape Shape = TableShape.Collection)
{
    /// <summary>A view over the single record that is the response payload itself.</summary>
    internal static TableView Record(IReadOnlyList<TableColumn> columns) =>
        new(string.Empty, columns, TableShape.Record);

    /// <summary>A view over the single record held by <paramref name="property"/>.</summary>
    internal static TableView Record(string property, IReadOnlyList<TableColumn> columns) =>
        new(property, columns, TableShape.Record);

    /// <summary>Whether this view describes one record rather than a collection.</summary>
    internal bool IsRecord => Shape == TableShape.Record;
}

/// <summary>
/// Declares that a command returns one page of results per call, and which request-body fields
/// get the next page.
/// </summary>
/// <remarks>
/// Help says so from this declaration, and table output, which has nowhere to show the token,
/// writes a notice to stderr when the response carries one. Without it, one page reads as
/// everything: a build or server on the next page looks like it doesn't exist.
/// </remarks>
/// <param name="PageSize">
/// The request-body field that sets how many results a page holds, written as
/// <c>nameof(SomeRequest.PageSize)</c>.
/// </param>
/// <param name="Token">
/// The field that carries the next-page token: the response sets it when more results exist, and
/// the request takes it back to get them. Written as <c>nameof(SomeResponse.SkipToken)</c>.
/// </param>
internal sealed record CommandPaging(string PageSize, string Token);

/// <summary>
/// Supplemental Help information for a command that cannot be derived from the
/// <see cref="Command"/> itself.
/// </summary>
/// <param name="Category">Help grouping for root-level commands.</param>
/// <param name="Stage">The compatibility commitment, shown in Help for Experimental commands.</param>
/// <param name="ApiPaths">
/// The PlayFab API paths this command invokes. Used to describe the command's safety from its
/// underlying effect rather than from its friendly CLI name, and left empty for commands that
/// make no API call.
/// </param>
/// <param name="Safety">
/// Explicit safety classification, for commands whose effect cannot be derived from an operation
/// name. Most PlayFab commands should leave this unset and let <paramref name="ApiPaths"/> derive
/// the classification, so a rename cannot change what the CLI says about an effect.
/// <para>
/// Two PlayFab commands set it because deriving would be wrong in opposite directions:
/// <c>mps asset upload</c> writes over a storage URL that a <c>Get</c> operation returns, and
/// <c>group member is-member</c> reads through <c>/Group/IsMember</c>, which the
/// <c>Get</c>/<c>List</c>/<c>Search</c> rule does not recognise as a read. Setting it does not
/// weaken the destructive tier — see <see cref="CommandMetadataRegistry.DescribeSafety"/> — and
/// the combination rules are enforced by <c>CommandSafetyMetadataTests</c>.
/// </para>
/// </param>
/// <param name="Scope">What the command reads or affects, when not obvious from the description.</param>
/// <param name="Output">The shape of the command's primary output.</param>
/// <param name="Examples">Complete runnable invocations; the renderer adds only the shell prompt.</param>
/// <param name="LearnMore">Extra "learn more" lines appended to the generated ones.</param>
/// <param name="SafetyDetails">
/// Behavior-specific safety details appended to the classification derived from
/// <paramref name="ApiPaths"/>. This supplements the underlying API effect; it never replaces it.
/// </param>
/// <param name="Table">
/// The table view for <c>--output table</c>: a collection view when the response holds many
/// records, or a record view when it is a single object, which renders as a one-row table.
/// Commands without one reject <c>--output table</c> rather than rendering a degenerate table.
/// </param>
/// <param name="Paging">
/// Set when the command returns one page of results per call. See <see cref="CommandPaging"/>.
/// </param>
internal sealed record CommandMetadata(
    HelpCategory Category = HelpCategory.None,
    IReadOnlyList<string>? ApiPaths = null,
    CommandSafety? Safety = null,
    string? Scope = null,
    string? Output = null,
    IReadOnlyList<string>? Examples = null,
    IReadOnlyList<string>? LearnMore = null,
    string? SafetyDetails = null,
    TableView? Table = null,
    CommandStage Stage = CommandStage.Stable,
    CommandPaging? Paging = null);
