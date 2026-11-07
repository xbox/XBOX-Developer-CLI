// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Help;
using XboxDevCLI.Presentation;
using XboxDevCLI.Services;

namespace XboxDevCLI.CommandLine;

/// <summary>
/// Renders Help for every command in the tree.
/// </summary>
/// <remarks>
/// One renderer, three shapes chosen from the target command rather than from its depth: the root
/// gets a concise task-oriented overview, a command with subcommands gets a group listing, and a
/// leaf gets full reference detail. Previously the root and a hardcoded set of groups used this
/// writer while everything deeper fell through to the stock <c>System.CommandLine</c> renderer,
/// which produced a visibly different layout at arbitrary depth.
/// </remarks>
internal static class CommandHelpWriter
{
    private const int DefaultWidth = 80;
    private const int MinimumWidth = 40;
    private const int MinimumDescriptionWidth = 24;
    private const string AnsiBold = "\u001b[1m";
    private const string AnsiReset = "\u001b[0m";

    private static readonly (HelpCategory Category, string Title)[] CommandCategories =
    [
        (HelpCategory.Core, "CORE COMMANDS"),
        (HelpCategory.Support, "SUPPORT COMMANDS"),
    ];

    private static readonly string[][] RootOptionGroups =
    [
        ["--playfab-title-id", "--profile", "--output", "--data-only"],
        ["--client-id", "--tenant-id", "--scopes"],
        ["--confirm", "--verbose", "--disable-telemetry"],
        ["--help", "--version"],
    ];

    /// <summary>
    /// Writes Help for <paramref name="command"/>, which may be the root or any command at any
    /// depth beneath it.
    /// </summary>
    internal static void Write(
        TextWriter writer,
        RootCommand rootCommand,
        Command command,
        string cliName,
        int? width = null,
        bool useAnsi = false)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(rootCommand);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(cliName);

        var outputWidth = Math.Max(MinimumWidth, width ?? GetOutputWidth());

        if (ReferenceEquals(command, rootCommand))
        {
            WriteRootHelp(writer, rootCommand, cliName, outputWidth, useAnsi);
            return;
        }

        var path = CommandPath.Resolve(rootCommand, command);
        var commandPath = $"{cliName} {string.Join(' ', path)}";
        var visibleSubcommands = command.Subcommands.Where(subcommand => !subcommand.Hidden).ToArray();

        if (visibleSubcommands.Length > 0)
        {
            WriteGroupHelp(writer, rootCommand, command, visibleSubcommands, commandPath, outputWidth, useAnsi);
            return;
        }

        WriteLeafHelp(writer, rootCommand, command, commandPath, outputWidth, useAnsi);
    }

    /// <summary>Writes Help for the root command.</summary>
    internal static void WriteRoot(
        TextWriter writer,
        RootCommand rootCommand,
        string cliName,
        int? width = null,
        bool useAnsi = false) =>
        Write(writer, rootCommand, rootCommand, cliName, width, useAnsi);

    private static void WriteRootHelp(
        TextWriter writer,
        RootCommand rootCommand,
        string cliName,
        int width,
        bool useAnsi)
    {
        WriteRootDescription(writer, rootCommand.Description, width, useAnsi);
        writer.WriteLine();

        WriteTextSection(writer, "GET STARTED", useAnsi, $"$ {cliName} auth login");
        WriteTextSection(writer, "USAGE", useAnsi, $"{cliName} <command> [subcommand...] [flags]");

        var visibleSubcommands = rootCommand.Subcommands
            .Where(subcommand => !subcommand.Hidden)
            .ToArray();
        var rootLabelWidth = visibleSubcommands
            .Select(subcommand => subcommand.Name.Length + 1)
            .Concat(
                rootCommand.Options
                    .Where(IsVisibleAtRoot)
                    .Select(option => FormatOptionUsage(option).Length))
            .DefaultIfEmpty(0)
            .Max();

        var categorized = new HashSet<Command>();
        foreach (var (category, title) in CommandCategories)
        {
            var rows = visibleSubcommands
                .Where(subcommand => CommandMetadataRegistry.GetMetadata(subcommand).Category == category)
                .Select(subcommand =>
                {
                    categorized.Add(subcommand);
                    return new HelpRow($"{subcommand.Name}:", DescribeCommand(subcommand));
                })
                .ToArray();

            WriteRowsSection(writer, title, rows, width, useAnsi, rootLabelWidth);
        }

        var uncategorized = visibleSubcommands
            .Where(subcommand => !categorized.Contains(subcommand))
            .Select(subcommand => new HelpRow($"{subcommand.Name}:", DescribeCommand(subcommand)))
            .ToArray();
        WriteRowsSection(
            writer,
            "ADDITIONAL COMMANDS",
            uncategorized,
            width,
            useAnsi,
            rootLabelWidth);

        var grouped = new HashSet<Option>();
        var optionGroups = new List<IReadOnlyList<HelpRow>>();
        foreach (var symbolNames in RootOptionGroups)
        {
            var rows = FindOptions(rootCommand, symbolNames, grouped);
            if (rows.Length > 0)
            {
                optionGroups.Add(rows);
            }
        }

        var additionalOptions = rootCommand.Options
            .Where(option => IsVisibleAtRoot(option) && !grouped.Contains(option))
            .Select(option => new HelpRow(FormatOptionUsage(option), ContextResolver.Describe(option)))
            .ToArray();
        if (additionalOptions.Length > 0)
        {
            optionGroups.Add(additionalOptions);
        }

        WriteGroupedRowsSection(writer, "FLAGS", optionGroups, width, useAnsi, rootLabelWidth);

        WriteTextSection(
            writer,
            "EXAMPLES",
            useAnsi,
            $"$ {cliName} auth status",
            $"$ {cliName} playfab title-data get --playfab-title-id abc123",
            $"$ {cliName} help playfab file list");

        WriteTextSection(
            writer,
            "LEARN MORE",
            useAnsi,
            $"Use `{cliName} <command> [subcommand...] --help` for more information about a command.",
            $"Use `{cliName} help <command> [subcommand...]` for the same information.",
            $"Read the documentation at {ProjectLinks.Documentation}",
            $"Run `{cliName} info` for version and getting-started links.");
    }

    private static void WriteGroupHelp(
        TextWriter writer,
        RootCommand rootCommand,
        Command command,
        IReadOnlyList<Command> subcommands,
        string commandPath,
        int width,
        bool useAnsi)
    {
        WriteDescriptionSection(writer, command, width, useAnsi);
        WriteExperimentalSection(writer, command, width, useAnsi);

        WriteTextSection(writer, "USAGE", useAnsi, BuildUsage(command, commandPath));

        var rows = subcommands
            .Select(subcommand => new HelpRow($"{subcommand.Name}:", DescribeCommand(subcommand)))
            .ToArray();
        var (localRows, inheritedRows) = BuildOptionRows(rootCommand, command);
        var labelWidth = GetSharedLabelWidth(rows, localRows, inheritedRows);
        WriteRowsSection(writer, "AVAILABLE COMMANDS", rows, width, useAnsi, labelWidth);

        WriteOptionSections(
            writer,
            localRows,
            inheritedRows,
            width,
            useAnsi,
            labelWidth);

        WriteTextSection(
            writer,
            "LEARN MORE",
            useAnsi,
            $"Use `{commandPath} <command> --help` for more information about a command.",
            $"Read the documentation at {ProjectLinks.Documentation}");
    }

    private static void WriteLeafHelp(
        TextWriter writer,
        RootCommand rootCommand,
        Command command,
        string commandPath,
        int width,
        bool useAnsi)
    {
        var metadata = CommandMetadataRegistry.GetMetadata(command);
        var requiredRows = BuildRequiredInputRows(command);
        var optionalRows = BuildOptionalInputRows(command);
        var (localRows, inheritedRows) = BuildOptionRows(rootCommand, command);
        var labelWidth = GetSharedLabelWidth(
            requiredRows,
            optionalRows,
            localRows,
            inheritedRows);

        WriteDescriptionSection(writer, command, width, useAnsi);
        WriteExperimentalSection(writer, command, width, useAnsi);

        WriteTextSection(writer, "USAGE", useAnsi, BuildUsage(command, commandPath));

        WriteRowsSection(writer, "REQUIRED INPUT", requiredRows, width, useAnsi, labelWidth);

        WriteRowsSection(writer, "OPTIONAL INPUT", optionalRows, width, useAnsi, labelWidth);

        if (!string.IsNullOrWhiteSpace(metadata.Scope))
        {
            WriteProseSection(writer, "SCOPE", width, useAnsi, metadata.Scope);
        }

        WriteOptionSections(
            writer,
            localRows,
            inheritedRows,
            width,
            useAnsi,
            labelWidth);

        var safety = CommandMetadataRegistry.DescribeSafety(metadata);
        if (safety is not null)
        {
            WriteProseSection(writer, "SAFETY", width, useAnsi, safety);
        }

        if (metadata.Examples is { Count: > 0 } examples)
        {
            WriteTextSection(
                writer,
                "EXAMPLES",
                useAnsi,
                [.. examples.Select(example => $"$ {example}")]);
        }

        if (!string.IsNullOrWhiteSpace(metadata.Output))
        {
            WriteProseSection(writer, "OUTPUT", width, useAnsi, metadata.Output);
        }

        if (metadata.Paging is { } paging)
        {
            WriteProseSection(writer, "PAGING", width, useAnsi, DescribePaging(paging));
        }

        // Table support is declared in metadata but was otherwise invisible: a reader had no way
        // to tell from --help that a command accepts --output table, let alone which columns it
        // shows, and the only other record of it is the docs site. Derived from the declaration
        // rather than written into each command's Output prose, so the two cannot disagree.
        if (metadata.Table is { } table)
        {
            WriteTextSection(
                writer,
                "TABLE OUTPUT",
                useAnsi,
                [
                    .. Wrap("Supports --output table, showing:", Math.Max(20, width - 2)),
                    .. table.Columns.Select(column => $"- {column.Header}"),
                    .. Wrap(
                        "Columns may change between releases; use --output json for a stable shape.",
                        Math.Max(20, width - 2)),
                ]);
        }

        var learnMore = new List<string>();
        if (metadata.ApiPaths is { Count: > 0 } apiPaths)
        {
            // One path fits the line; several do not. Six is what `player-data get` reaches
            // across --visibility and --publisher, and comma-joining those runs past 200 columns
            // in a section the renderer deliberately does not wrap, because its other lines are
            // commands and links that wrapping would corrupt.
            if (apiPaths.Count == 1)
            {
                learnMore.Add($"Calls the PlayFab API: {apiPaths[0]}");
            }
            else
            {
                // "one or more" because both shapes reach here: `title-data set` calls exactly one
                // of its two paths per invocation, while `file upload` calls both in sequence.
                // Which it is comes from the command's own LearnMore line, where a selector
                // exists to name.
                learnMore.Add("Calls one or more of these PlayFab APIs:");
                learnMore.AddRange(apiPaths.Select(apiPath => $"  {apiPath}"));
            }
        }

        if (metadata.LearnMore is { Count: > 0 } extra)
        {
            learnMore.AddRange(extra);
        }

        learnMore.Add($"Read the documentation at {ProjectLinks.Documentation}");
        WriteTextSection(writer, "LEARN MORE", useAnsi, [.. learnMore]);
    }

    private static bool IsExperimental(Command command) =>
        CommandMetadataRegistry.GetMetadata(command).Stage == CommandStage.Experimental ||
        command.Parents.OfType<Command>().Any(IsExperimental);

    private static string DescribeCommand(Command command) =>
        IsExperimental(command)
            ? $"{command.Description} [Experimental]"
            : command.Description ?? string.Empty;

    private static void WriteExperimentalSection(
        TextWriter writer, Command command, int width, bool useAnsi)
    {
        if (IsExperimental(command))
        {
            WriteProseSection(writer, "EXPERIMENTAL", width, useAnsi,
                "This command's CLI interface may change or be removed based on feedback.");
        }
    }

    /// <summary>
    /// Builds the usage line for any command shape. Root and group commands advertise their
    /// variable-depth child paths; leaves include their own arguments and required options.
    /// </summary>
    internal static string BuildUsage(Command command, string commandPath)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandPath);

        if (command is RootCommand)
        {
            return $"{commandPath} <command> [subcommand...] [flags]";
        }

        if (command.Subcommands.Any(subcommand => !subcommand.Hidden))
        {
            return $"{commandPath} <command> [flags]";
        }

        return BuildLeafUsage(command, commandPath);
    }

    private static string BuildLeafUsage(Command command, string commandPath)
    {
        var usage = new List<string> { commandPath };

        foreach (var argument in command.Arguments.Where(argument => !argument.Hidden))
        {
            usage.Add(FormatArgumentUsage(argument));
        }

        foreach (var option in command.Options.Where(option => !option.Hidden && option.Required))
        {
            usage.Add(FormatOptionUsage(option, includeAliases: false));
        }

        usage.Add("[flags]");
        return string.Join(' ', usage);
    }

    /// <summary>
    /// Rows for the inputs a command cannot run without: required arguments and required options.
    /// </summary>
    /// <remarks>
    /// Required options are surfaced here and deliberately excluded from OPTIONS by
    /// <see cref="WriteOptionSections"/>, so each input is documented exactly once.
    /// </remarks>
    private static HelpRow[] BuildRequiredInputRows(Command command)
    {
        var rows = new List<HelpRow>();

        rows.AddRange(command.Arguments
            .Where(argument => !argument.Hidden && argument.Arity.MinimumNumberOfValues > 0)
            .Select(argument => new HelpRow(
                FormatArgumentUsage(argument),
                argument.Description ?? string.Empty)));

        rows.AddRange(command.Options
            .Where(option => !option.Hidden && option.Required)
            .Select(option => new HelpRow(FormatOptionUsage(option), ContextResolver.Describe(option))));

        return [.. rows];
    }

    /// <summary>
    /// Rows for arguments the command accepts but does not require. Without this the stock
    /// renderer's "Arguments:" block would be lost when its fallback was removed, leaving
    /// optional positional input such as <c>playfab title-data get [key]</c> undocumented.
    /// </summary>
    private static HelpRow[] BuildOptionalInputRows(Command command) =>
    [
        .. command.Arguments
            .Where(argument => !argument.Hidden && argument.Arity.MinimumNumberOfValues == 0)
            .Select(argument => new HelpRow(
                FormatArgumentUsage(argument),
                argument.Description ?? string.Empty)),
    ];

    private static (HelpRow[] LocalRows, HelpRow[] InheritedRows) BuildOptionRows(
        RootCommand rootCommand,
        Command command)
    {
        // Required options are already listed under REQUIRED INPUT; listing them again here
        // would document every required input twice.
        var localRows = command.Options
            .Where(option => !option.Hidden && !option.Required)
            .Select(option => new HelpRow(FormatOptionUsage(option), ContextResolver.Describe(option)))
            .ToArray();

        var isPlayFabScope = CommandPath.IsUnderPlayFab(rootCommand, command);
        var inheritedRows = rootCommand.Options
            .Where(option => !option.Hidden)
            .Where(option => option.Recursive || option is HelpOption)
            .Where(option => isPlayFabScope ||
                CommandMetadataRegistry.GetAudience(option) != OptionAudience.PlayFab)
            .Select(option => new HelpRow(FormatOptionUsage(option), ContextResolver.Describe(option)))
            .ToArray();

        return (localRows, inheritedRows);
    }

    /// <summary>
    /// Writes the command's own options followed by the inherited options that are meaningful at
    /// this point in the tree.
    /// </summary>
    private static void WriteOptionSections(
        TextWriter writer,
        IReadOnlyList<HelpRow> localRows,
        IReadOnlyList<HelpRow> inheritedRows,
        int width,
        bool useAnsi,
        int labelWidth)
    {
        WriteRowsSection(writer, "OPTIONS", localRows, width, useAnsi, labelWidth);
        WriteRowsSection(writer, "INHERITED FLAGS", inheritedRows, width, useAnsi, labelWidth);
    }

    private static int GetSharedLabelWidth(params IReadOnlyList<HelpRow>[] groups) =>
        groups
            .SelectMany(group => group)
            .Select(row => row.Label.Length)
            .DefaultIfEmpty(0)
            .Max();

    private static void WriteDescriptionSection(
        TextWriter writer,
        Command command,
        int width,
        bool useAnsi)
    {
        var description = command.Description;
        if (string.IsNullOrWhiteSpace(description))
        {
            return;
        }

        WriteProseSection(writer, "DESCRIPTION", width, useAnsi, description);
    }

    private static HelpRow[] FindOptions(
        RootCommand rootCommand,
        IReadOnlyList<string> names,
        ISet<Option> groupedOptions)
    {
        var optionsByName = rootCommand.Options
            .Where(IsVisibleAtRoot)
            .ToDictionary(option => option.Name, StringComparer.Ordinal);

        return names
            .Where(optionsByName.ContainsKey)
            .Select(name => optionsByName[name])
            .Select(option =>
            {
                groupedOptions.Add(option);
                return new HelpRow(FormatOptionUsage(option), ContextResolver.Describe(option));
            })
            .ToArray();
    }

    private static bool IsVisibleAtRoot(Option option) =>
        !option.Hidden &&
        CommandMetadataRegistry.GetAudience(option) != OptionAudience.PlayFab;

    private static string FormatOptionUsage(Option option, bool includeAliases = true)
    {
        var names = includeAliases
            ? new[] { option.Name }
                .Concat(option.Aliases)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name.StartsWith("--", StringComparison.Ordinal) ? 1 : 0)
                .ThenBy(name => name, StringComparer.Ordinal)
                .AsEnumerable()
            : [option.Name];

        var usage = string.Join(", ", names);
        if (option.ValueType != typeof(bool) && option.ValueType != typeof(void))
        {
            var helpName = string.IsNullOrWhiteSpace(option.HelpName)
                ? option.Name.TrimStart('-', '/')
                : option.HelpName;
            usage += $" <{helpName}>";
        }

        return usage;
    }

    private static string FormatArgumentUsage(Argument argument)
    {
        var name = argument.Arity.MaximumNumberOfValues > 1
            ? $"{argument.Name}..."
            : argument.Name;
        return argument.Arity.MinimumNumberOfValues > 0
            ? $"<{name}>"
            : $"[{name}]";
    }

    private static void WriteRowsSection(
        TextWriter writer,
        string title,
        IReadOnlyList<HelpRow> rows,
        int width,
        bool useAnsi,
        int? labelWidth = null)
    {
        if (rows.Count == 0)
        {
            return;
        }

        WriteSectionTitle(writer, title, useAnsi);
        WriteRows(writer, rows, width, labelWidth);
        writer.WriteLine();
    }

    private static void WriteGroupedRowsSection(
        TextWriter writer,
        string title,
        IReadOnlyList<IReadOnlyList<HelpRow>> groups,
        int width,
        bool useAnsi,
        int labelWidth)
    {
        if (groups.Count == 0)
        {
            return;
        }

        WriteSectionTitle(writer, title, useAnsi);
        for (var index = 0; index < groups.Count; index++)
        {
            if (index > 0)
            {
                writer.WriteLine();
            }

            WriteRows(writer, groups[index], width, labelWidth);
        }

        writer.WriteLine();
    }

    private static void WriteRows(
        TextWriter writer,
        IReadOnlyList<HelpRow> rows,
        int width,
        int? sharedLabelWidth = null)
    {
        var labelWidth = sharedLabelWidth ?? rows.Max(row => row.Label.Length);
        var descriptionWidth = width - labelWidth - 4;
        if (descriptionWidth < MinimumDescriptionWidth)
        {
            foreach (var row in rows)
            {
                writer.WriteLine($"  {row.Label}");
                foreach (var line in Wrap(row.Description, width - 4))
                {
                    writer.WriteLine($"    {line}");
                }
            }
        }
        else
        {
            var continuationIndent = new string(' ', labelWidth + 4);
            foreach (var row in rows)
            {
                var descriptionLines = Wrap(row.Description, descriptionWidth).ToArray();
                writer.Write($"  {row.Label.PadRight(labelWidth)}  ");
                writer.WriteLine(descriptionLines.FirstOrDefault() ?? string.Empty);

                foreach (var line in descriptionLines.Skip(1))
                {
                    writer.WriteLine($"{continuationIndent}{line}");
                }
            }
        }
    }

    /// <summary>
    /// Writes a section whose lines are copied verbatim. Used for content the reader may want to
    /// copy and run — usage strings, examples, links — where wrapping would corrupt the text.
    /// </summary>
    private static void WriteTextSection(
        TextWriter writer,
        string title,
        bool useAnsi,
        params string[] lines)
    {
        if (lines.Length == 0)
        {
            return;
        }

        WriteSectionTitle(writer, title, useAnsi);
        foreach (var line in lines)
        {
            writer.WriteLine($"  {line}");
        }

        writer.WriteLine();
    }

    /// <summary>What Help says about a command that returns one page per call.</summary>
    private static string DescribePaging(CommandPaging paging) =>
        $"Returns one page of results, not necessarily all of them. When more exist, the " +
        $"response's {paging.Token} is set: to get the next page, add \"{paging.Token}\" with " +
        $"that value to the request body, keeping its other fields, and repeat until " +
        $"{paging.Token} is null. Set \"{paging.PageSize}\" in the request body to change how " +
        "many results a page holds. With --output table, a notice on stderr gives the token.";

    /// <summary>
    /// Writes a prose section, wrapped to the available width. Used for SCOPE, SAFETY, OUTPUT,
    /// and PAGING, whose text is sentences rather than commands and so would otherwise overflow a
    /// narrow terminal.
    /// </summary>
    private static void WriteProseSection(
        TextWriter writer,
        string title,
        int width,
        bool useAnsi,
        string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        WriteSectionTitle(writer, title, useAnsi);
        foreach (var line in Wrap(text, Math.Max(20, width - 2)))
        {
            writer.WriteLine($"  {line}");
        }

        writer.WriteLine();
    }

    private static void WriteSectionTitle(TextWriter writer, string title, bool useAnsi) =>
        writer.WriteLine(useAnsi ? $"{AnsiBold}{title}{AnsiReset}" : title);

    private static void WriteRootDescription(
        TextWriter writer,
        string? description,
        int width,
        bool useAnsi)
    {
        var value = description ?? string.Empty;
        var lines = Wrap(value, width).ToArray();
        foreach (var (line, index) in lines.Select((line, index) => (line, index)))
        {
            if (index == 0 &&
                useAnsi &&
                line.StartsWith(CliIdentity.DisplayName, StringComparison.Ordinal))
            {
                writer.Write(WelcomeExperience.ColorizeBrand("XBOX", useColor: true));
                writer.WriteLine(line["XBOX".Length..]);
            }
            else
            {
                writer.WriteLine(line);
            }
        }
    }

    internal static bool ShouldUseAnsi() =>
        WelcomeExperience.SupportsColor(Console.IsOutputRedirected);

    private static IEnumerable<string> Wrap(string value, int width)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield return string.Empty;
            yield break;
        }

        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var line = words[0];

        foreach (var word in words.Skip(1))
        {
            if (line.Length + word.Length + 1 <= width)
            {
                line += $" {word}";
                continue;
            }

            yield return line;
            line = word;
        }

        yield return line;
    }

    private static int GetOutputWidth()
    {
        if (Console.IsOutputRedirected)
        {
            return DefaultWidth;
        }

        try
        {
            return Math.Clamp(Console.WindowWidth, 60, 120);
        }
        catch (IOException)
        {
            return DefaultWidth;
        }
    }

    private sealed record HelpRow(string Label, string Description);
}
