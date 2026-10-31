// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Reflection;
using System.Text.Json.Serialization;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Commands;
using XboxDevCLI.Output;
using XboxDevCLI.PlayFab.Generated;
using Xunit;

namespace XboxDevCLI.UnitTest;

/// <summary>
/// Guards the table-output contract across the whole command tree.
/// </summary>
/// <remarks>
/// These tests walk every command rather than naming a fixed set, so a view added later is
/// covered without anyone remembering to extend a list here — the same staleness problem that
/// moved table support into metadata in the first place.
/// <para>
/// The important test is <see cref="EveryDeclaredViewMatchesItsResponseModel"/>. A view names its
/// collection and columns as strings, and the renderer deliberately tolerates a missing field so
/// a sparse response does not fail. Those two facts together mean a typo produces a table of
/// dashes and still exits 0 — it is invisible. Validating declarations against the generated
/// response models turns that into a build failure.
/// </para>
/// </remarks>
public class TableOutputSupportTests
{
    /// <summary>Every command in the tree, depth-first, with its CLI path.</summary>
    private static IEnumerable<(string Path, Command Command)> AllCommands(
        Command command, string prefix = "")
    {
        var path = string.IsNullOrEmpty(prefix) ? command.Name : $"{prefix} {command.Name}";
        yield return (path, command);

        foreach (var child in command.Subcommands)
        {
            foreach (var descendant in AllCommands(child, path))
            {
                yield return descendant;
            }
        }
    }

    private static IEnumerable<(string Path, Command Command, TableView View)> DeclaredViews()
    {
        foreach (var (path, command) in AllCommands(Program.BuildRootCommand()))
        {
            var view = CommandMetadataRegistry.GetMetadata(command).Table;
            if (view is not null)
            {
                yield return (path, command, view);
            }
        }
    }

    /// <summary>Resolves a generated model type by its unqualified name.</summary>
    private static Type? ResolveGeneratedType(string typeName) =>
        typeof(PlayFabApiMetadata).Assembly
            .GetTypes()
            .SingleOrDefault(type =>
                type.Namespace?.StartsWith("XboxDevCLI.PlayFab.Generated.", StringComparison.Ordinal) == true
                && type.Name.Equals(typeName, StringComparison.Ordinal));

    /// <summary>Finds the property carrying a given JSON wire name.</summary>
    private static PropertyInfo? PropertyForWireName(Type type, string wireName) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(property =>
                (property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name)
                    .Equals(wireName, StringComparison.Ordinal));

    /// <summary>
    /// Resolves the type whose fields a view's columns read: the collection's element for a
    /// collection view, the record itself for a record view.
    /// </summary>
    private static Type ResolveElementType(string path, Type responseType, TableView view)
    {
        // A record view over the payload root reads the response type's own fields, so there is
        // no intermediate property to resolve.
        if (view.IsRecord && string.IsNullOrEmpty(view.Collection))
        {
            return responseType;
        }

        var source = PropertyForWireName(responseType, view.Collection);
        Assert.True(
            source is not null,
            $"'{path}' declares {(view.IsRecord ? "record" : "collection")} '{view.Collection}', " +
            $"which is not a field on {responseType.Name}. A table of dashes would be the only " +
            "symptom at runtime.");

        var sourceType = Unwrap(source!.PropertyType);

        if (view.IsRecord)
        {
            // A record view must not point at a list: every column would read a field off the
            // list itself and render "-", which looks like an empty record rather than a
            // mis-declaration.
            Assert.False(
                sourceType.IsGenericType &&
                    sourceType.GetGenericTypeDefinition() == typeof(List<>),
                $"'{path}' declares record '{view.Collection}', but it holds a list. Use a " +
                "collection view, or name the single record inside it.");
            return sourceType;
        }

        // Two shapes are legitimate. Most responses return List<T>. Some key their records
        // by name instead — file metadata is keyed by file name — and the generator types
        // those as the record type itself rather than a dictionary, so the element type is
        // that type. Either way the columns are read from the record.
        return sourceType.IsGenericType &&
            sourceType.GetGenericTypeDefinition() == typeof(List<>)
                ? sourceType.GetGenericArguments()[0]
                : sourceType;
    }

    [Fact]
    public void SomeCommandsDeclareAView()
    {
        // Guards the walker itself: if command construction or metadata attachment regressed,
        // every other test here would pass vacuously over an empty set.
        Assert.NotEmpty(DeclaredViews().ToArray());
    }

    [Fact]
    public void EveryDeclaredViewIsStructurallyValid()
    {
        foreach (var (path, _, view) in DeclaredViews())
        {
            Assert.False(
                string.IsNullOrWhiteSpace(view.Collection) && !view.IsRecord,
                $"'{path}' declares a collection view with no collection.");
            Assert.True(view.Columns.Count > 0, $"'{path}' declares a view with no columns.");

            Assert.All(view.Columns, column =>
            {
                Assert.False(
                    string.IsNullOrWhiteSpace(column.Header),
                    $"'{path}' declares a column with no header.");
                Assert.True(
                    column.MaximumWidth > 0,
                    $"'{path}' column '{column.Header}' has a non-positive width.");

                // A width narrower than the header truncates the header itself, leaving a column
                // labelled something like "SERVERS P...". The data would be readable and the
                // heading would not, which no declaration intends. Compared in the cells the
                // width is declared in, not in code units, so a header is measured the way the
                // renderer will measure it.
                Assert.True(
                    column.MaximumWidth >= DisplayWidth.Measure(column.Header),
                    $"'{path}' column '{column.Header}' declares width {column.MaximumWidth}, " +
                    $"narrower than its {DisplayWidth.Measure(column.Header)}-cell header, so " +
                    "the header would render truncated.");

                // A secret reaching table output is a different class of mistake from a wrong
                // column: the table is the format that lands in screenshots, pasted chat, and
                // scrollback, so it has to be opted into deliberately.
                Assert.False(
                    LooksLikeASecret(column.Property) && !column.Redact,
                    $"'{path}' column '{column.Header}' reads '{column.Property}', which names a " +
                    "credential, but is not marked Redact. Pass Redact: true, or rename the " +
                    "column if it holds no secret.");
            });

            // An element-itself column consumes the whole element, so it cannot share a view.
            if (view.Columns.Any(column => column.IsElementItself))
            {
                Assert.True(
                    view.Columns.Count == 1,
                    $"'{path}' mixes an element-itself column with others; a scalar collection " +
                    "has one value per element.");

                // A record view names the fields of one object, so there is no "element" to be.
                Assert.False(
                    view.IsRecord,
                    $"'{path}' is a record view with an element-itself column, which has no " +
                    "meaning: a record's columns read its fields.");
            }
        }
    }

    /// <summary>
    /// Checks every declared view against the response model of the API the command calls: the
    /// source must exist, and each column must name a real field on the record it holds.
    /// </summary>
    /// <remarks>
    /// Two source shapes are accepted. Most responses return a <c>List&lt;T&gt;</c>. Some key
    /// their records instead — <c>file list</c> returns metadata keyed by file name — and the
    /// generator types those as the record itself, so the columns are read from that type either
    /// way. A record view's source is the single record, or the response root.
    /// </remarks>
    [Fact]
    public void EveryDeclaredViewMatchesItsResponseModel()
    {
        var checkedViews = 0;

        foreach (var (path, command, view) in DeclaredViews())
        {
            var apiPaths = CommandMetadataRegistry.GetMetadata(command).ApiPaths ?? [];

            // Commands that call no PlayFab API (the seller gateway commands) have no generated
            // response model to check against; structural validation above still applies.
            if (apiPaths.Count == 0)
            {
                continue;
            }

            foreach (var apiPath in apiPaths)
            {
                var endpoint = PlayFabApiMetadata.AllEndpoints.FirstOrDefault(candidate =>
                    string.Equals(candidate.Path, apiPath, StringComparison.OrdinalIgnoreCase));

                Assert.True(endpoint is not null, $"'{path}' names unknown API path '{apiPath}'.");

                var responseType = ResolveGeneratedType(endpoint!.ResponseType!);
                Assert.True(
                    responseType is not null,
                    $"'{path}': cannot resolve response type '{endpoint.ResponseType}'.");

                var elementType = ResolveElementType(path, responseType!, view);

                Assert.True(
                    elementType != typeof(string) || view.Columns.All(column => column.IsElementItself),
                    $"'{path}' declares collection '{view.Collection}' as a scalar, but its " +
                    "columns read properties.");

                foreach (var column in view.Columns)
                {
                    if (column.IsElementItself)
                    {
                        // A scalar view is only meaningful over a collection of scalars.
                        Assert.True(
                            elementType == typeof(string) || elementType.IsPrimitive,
                            $"'{path}' column '{column.Header}' reads the element itself, but " +
                            $"'{view.Collection}' holds {elementType.Name}, not a scalar.");
                        continue;
                    }

                    // Walk every segment, not just the first. A dotted path is the supported way
                    // to reach a nested field, so a typo in a later segment ("Entity.Key.Idd")
                    // must fail here too — at runtime it renders "-", which is indistinguishable
                    // from legitimately absent data.
                    var current = elementType;
                    var walked = new List<string>();

                    foreach (var segment in column.Property.Split('.'))
                    {
                        var property = PropertyForWireName(current, segment);
                        Assert.True(
                            property is not null,
                            $"'{path}' column '{column.Header}' reads '{column.Property}', but " +
                            $"'{segment}' is not a field on {current.Name}" +
                            (walked.Count > 0 ? $" (reached via {string.Join('.', walked)})" : string.Empty) +
                            ". A cell of dashes would be the only symptom at runtime.");

                        walked.Add(segment);
                        current = Unwrap(property!.PropertyType);
                    }

                    // The final segment must render as a scalar. Anything else — a collection or
                    // a nested object — reaches Stringify's fallback and emits raw JSON truncated
                    // to the column width. Checking only for collections let an object-valued
                    // property through, which is the same defect with a different shape.
                    Assert.True(
                        RendersAsScalar(current),
                        $"'{path}' column '{column.Header}' reads '{column.Property}', which is " +
                        $"{(IsCollection(current) ? $"a collection ({current.Name})" : $"an object ({current.Name})")}. " +
                        "A table cell would show truncated raw JSON; point at a field inside it " +
                        "with a dotted path, or drop the column.");
                }

                checkedViews++;
            }
        }

        Assert.True(checkedViews > 0, "No PlayFab-backed views were checked.");
    }

    /// <summary>Strips <c>Nullable&lt;T&gt;</c> so a value type's fields can be walked.</summary>
    private static Type Unwrap(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    /// <summary>
    /// Whether a property path names something that should never be printed in a table.
    /// </summary>
    /// <remarks>
    /// Deliberately a short, high-confidence list matched on the final path segment. Asset upload
    /// and download URLs are included because the service returns pre-authenticated bearer URLs.
    /// <c>Token</c> is left out because the generated models use it mostly for continuation
    /// cursors, which are not secrets, and a rule that cries wolf gets suppressed.
    /// <c>SecretSummary.Name</c> does not match either: the summary endpoints return names and
    /// versions, never values.
    /// </remarks>
    private static bool LooksLikeASecret(string property)
    {
        if (string.IsNullOrEmpty(property))
        {
            return false;
        }

        var leaf = property.Split('.')[^1];
        string[] markers =
        [
            "Password", "Secret", "Credential", "PrivateKey", "ApiKey", "AccessKey", "ConnectionString",
            "AssetUploadUrl", "AssetDownloadUrl",
        ];

        return markers.Any(marker => leaf.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsCollection(Type type) =>
        type != typeof(string)
        && (type.IsArray
            || (type.IsGenericType
                && type.GetGenericTypeDefinition() == typeof(List<>)));

    /// <summary>
    /// Whether a value of this type serializes to a JSON scalar, and so fits in a table cell.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>TableFormatter.Stringify</c>, which renders strings, numbers and booleans and
    /// falls back to raw JSON for everything else. The listed struct types are not primitives but
    /// do serialize as JSON strings, so they render fine; <c>object</c> is excluded because the
    /// generator uses it for arbitrary JSON.
    /// </remarks>
    private static bool RendersAsScalar(Type type) =>
        type == typeof(string)
        || type.IsEnum
        || type.IsPrimitive
        || type == typeof(decimal)
        || type == typeof(DateTime)
        || type == typeof(DateTimeOffset)
        || type == typeof(TimeSpan)
        || type == typeof(Guid)
        || type == typeof(Uri);

    /// <summary>
    /// Support must come from metadata alone. A path-prefix fallback re-introduces exactly the
    /// staleness this design removed: a rename silently detaches or wrongly enables table output.
    /// </summary>
    [Fact]
    public void TableSupportFollowsDeclaredMetadataExactly()
    {
        foreach (var (path, command, _) in AllCommands(Program.BuildRootCommand())
            .Select(entry => (entry.Path, entry.Command, 0)))
        {
            var declared = CommandMetadataRegistry.GetMetadata(command).Table is not null;

            Assert.True(
                Program.SupportsTableOutput(command) == declared,
                $"'{path}': table support disagrees with its declared metadata.");
        }
    }

    [Fact]
    public void SellerCommandsDeclareAViewRatherThanRelyingOnTheirPath()
    {
        var root = Program.BuildRootCommand();
        var seller = root.Subcommands.Single(command => command.Name == "seller");

        Assert.All(seller.Subcommands, leaf =>
            Assert.True(
                CommandMetadataRegistry.GetMetadata(leaf).Table is not null,
                $"'seller {leaf.Name}' relies on a path fallback rather than declared metadata."));
    }

    /// <summary>
    /// Seller views have no generated model to check against, so they are validated against the
    /// payloads their own commands build.
    /// </summary>
    /// <remarks>
    /// This closes the hole that once let a seller command ship declaring a <c>NAME</c> column its
    /// response never contained, while silently dropping the <c>source</c> field that was the
    /// whole reason the command existed. Commands with no <c>ApiPaths</c> are skipped by the
    /// model-backed check above, so without this they are unvalidated.
    /// </remarks>
    [Theory]
    [InlineData("list", "sellerId", "name")]
    public void SellerViewsMatchTheResponseTheirCommandBuilds(
        string leafName, params string[] expectedProperties)
    {
        var root = Program.BuildRootCommand();
        var seller = root.Subcommands.Single(command => command.Name == "seller");
        var leaf = seller.Subcommands.Single(command => command.Name == leafName);
        var view = CommandMetadataRegistry.GetMetadata(leaf).Table;

        Assert.NotNull(view);
        Assert.Equal("sellers", view!.Collection);
        Assert.Equal(
            expectedProperties,
            view.Columns.Select(column => column.Property).ToArray());
    }

    /// <summary>
    /// Every context command writes the same rows, so they share one declared view. Like the
    /// seller views it has no generated model to check against; <c>ContextCommandTests</c> checks
    /// it against the rows the commands actually write.
    /// </summary>
    [Theory]
    [InlineData("get")]
    [InlineData("set")]
    [InlineData("unset")]
    public void ContextCommandsDeclareTheSharedKeyValueView(string leafName)
    {
        var root = Program.BuildRootCommand();
        var context = root.Subcommands.Single(command => command.Name == "context");
        var leaf = context.Subcommands.Single(command => command.Name == leafName);
        var view = CommandMetadataRegistry.GetMetadata(leaf).Table;

        Assert.Same(ContextCommandFactory.TableView, view);
        Assert.Equal("values", view!.Collection);
        Assert.Equal(
            new[] { "key", "value" },
            view.Columns.Select(column => column.Property).ToArray());
    }

    /// <summary>
    /// <c>context profile list</c> and <c>delete</c> both write profile rows, so they share one
    /// declared view, checked the same way as the context values view.
    /// </summary>
    [Theory]
    [InlineData("list")]
    [InlineData("delete")]
    public void ContextProfileCommandsDeclareTheSharedProfileView(string leafName)
    {
        var root = Program.BuildRootCommand();
        var context = root.Subcommands.Single(command => command.Name == "context");
        var profile = context.Subcommands.Single(command => command.Name == "profile");
        var leaf = profile.Subcommands.Single(command => command.Name == leafName);
        var view = CommandMetadataRegistry.GetMetadata(leaf).Table;

        Assert.Same(ContextCommandFactory.ProfileTableView, view);
        Assert.Equal("profiles", view!.Collection);
        Assert.Equal(
            new[] { "name" },
            view.Columns.Select(column => column.Property).ToArray());
    }

    /// <summary>
    /// Table support tracks whether a view is declared, not whether the response is a collection:
    /// a single record is rendered as labelled rows. What still refuses is a response no fixed
    /// view can describe.
    /// </summary>
    [Fact]
    public void PassThroughAndUndeclaredCommandsRejectTable()
    {
        var root = Program.BuildRootCommand();

        Command Resolve(params string[] path)
        {
            Command current = root;
            foreach (var segment in path)
            {
                current = current.Subcommands.Single(
                    subcommand => string.Equals(subcommand.Name, segment, StringComparison.Ordinal));
            }

            return current;
        }

        // `playfab api` returns whatever the endpoint returns, so no fixed view can describe it.
        Assert.False(Program.SupportsTableOutput(Resolve("playfab", "api")));

        // Declares no view: its response is a map of arbitrary title-defined keys.
        Assert.False(Program.SupportsTableOutput(Resolve("playfab", "title-data", "get")));

        // A single record does support table — as a record view, not a one-row table.
        var buildGet = Resolve("playfab", "mps", "build", "get");
        Assert.True(Program.SupportsTableOutput(buildGet));
        Assert.True(CommandMetadataRegistry.GetMetadata(buildGet).Table!.IsRecord);
    }

    /// <summary>
    /// Help surfaces declared table support, so it is discoverable without reading the docs site.
    /// </summary>
    /// <remarks>
    /// Rendered from the declaration rather than written into each command's prose, so a view and
    /// its help text cannot drift apart. A command with no view must show nothing, or help would
    /// advertise a format that the gate rejects.
    /// </remarks>
    [Fact]
    public void HelpAdvertisesTableSupportOnlyWhereItIsDeclared()
    {
        var root = Program.BuildRootCommand();

        var supported = RenderHelp(root, "playfab", "mps", "build", "list");
        Assert.Contains("TABLE OUTPUT", supported, StringComparison.Ordinal);
        Assert.Contains("BUILD ID", supported, StringComparison.Ordinal);
        Assert.Contains("CREATED", supported, StringComparison.Ordinal);

        var unsupported = RenderHelp(root, "playfab", "api");
        Assert.DoesNotContain("TABLE OUTPUT", unsupported, StringComparison.Ordinal);
    }

    /// <summary>Every command declaring a view advertises it, so none is left undiscoverable.</summary>
    [Fact]
    public void EveryTableCapableCommandAdvertisesItInHelp()
    {
        // One tree: the help writer resolves a command's path against the root it is given, so
        // commands from a separately built tree are not part of it.
        var root = Program.BuildRootCommand();

        foreach (var (path, command) in AllCommands(root))
        {
            if (CommandMetadataRegistry.GetMetadata(command).Table is not { } view)
            {
                continue;
            }

            var help = RenderHelp(command, root);

            Assert.True(
                help.Contains("TABLE OUTPUT", StringComparison.Ordinal),
                $"'{path}' declares a table view but its help never mentions it.");

            foreach (var column in view.Columns.Where(column => !column.IsElementItself))
            {
                Assert.True(
                    help.Contains(column.Header, StringComparison.Ordinal),
                    $"'{path}' help omits the '{column.Header}' column.");
            }
        }
    }

    private static string RenderHelp(RootCommand root, params string[] path)
    {
        Command current = root;
        foreach (var segment in path)
        {
            current = current.Subcommands.Single(
                subcommand => string.Equals(subcommand.Name, segment, StringComparison.Ordinal));
        }

        return RenderHelp(current, root);
    }

    private static string RenderHelp(Command command, RootCommand root)
    {
        var writer = new StringWriter();
        CommandHelpWriter.Write(writer, root, command, "xbox", width: 100, useAnsi: false);
        return writer.ToString();
    }
}
