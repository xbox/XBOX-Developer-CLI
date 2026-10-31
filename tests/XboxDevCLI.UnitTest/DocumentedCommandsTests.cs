// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace XboxDevCLI.Tests;

/// <summary>
/// Every <c>xbox</c> command line in the docs names commands and options the CLI has, so a
/// rename that misses a page fails here instead of in a reader's terminal. It reads the docs
/// at the repository root and under <c>docs/</c>, in code spans and in code blocks, and checks
/// only the names: placeholders like <c>&lt;id&gt;</c> stand for any value.
/// </summary>
public sealed partial class DocumentedCommandsTests
{
    [Fact]
    public void EveryDocumentedCommandAndOptionExists()
    {
        var root = Program.BuildRootCommand();
        var problems = DocumentedCommands()
            .Select(documented => (documented, Problem: Check(root, documented.Tokens)))
            .Where(result => result.Problem is not null)
            .Select(result => $"{result.documented.File}:{result.documented.Line}: `{string.Join(' ', result.documented.Tokens)}` {result.Problem}")
            .ToList();

        Assert.True(problems.Count == 0, "The docs name commands or options the CLI doesn't have:\n" + string.Join('\n', problems));
    }

    [Theory]
    [InlineData("XBOX_PLAYFAB_TITLE_ID=DEF34 xbox contxt get")]
    [InlineData(".\\xbox contxt get")]
    [InlineData("./xbox contxt get")]
    [InlineData("& \"$cliDir\\xbox.exe\" contxt get")]
    public void ChecksCommandLinesThatSetVariablesOrNameAPath(string commandLine) =>
        Assert.Equal("has unknown `contxt get`.", Check(Program.BuildRootCommand(), CommandLine(commandLine)!));

    [Theory]
    [InlineData("xbox-win-x64.zip")]
    [InlineData("xbox.xml")]
    [InlineData("unzip xbox-linux-x64.zip")]
    public void IgnoresTextThatDoesntRunTheCli(string text) =>
        Assert.Null(CommandLine(text));

    [Theory]
    [InlineData("xbox context get", null)]
    [InlineData("xbox context set seller-id <id>", null)]
    [InlineData("xbox --playfab-title-id <TITLE_ID> playfab api /Admin/GetTitleData", null)]
    [InlineData("xbox game list --output table --verbose", null)]
    [InlineData("xbox playfab <service> --help", null)]
    [InlineData("xbox xbl document download --type account --dest out --document a.xml b.xml", null)]
    [InlineData("xbox playfab mps build get --body-file -", null)]
    [InlineData("xbox playfab api -- /Admin/GetTitleData", null)]
    [InlineData("xbox playfab api -- --not-an-option", null)]
    [InlineData("xbox --playfab-title-id <TITLE_ID> playfab contxt", "has unknown `contxt`.")]
    [InlineData("xbox context set seller-id <id> --format table", "has unknown `--format table`.")]
    [InlineData("xbox playfab api /Admin/GetTitleData --body-file=<file> --format table", "has unknown `--format table`.")]
    [InlineData("xbox playfab api /Admin/GetTitleData < body.json", null)]
    [InlineData("xbox game list > games.json", null)]
    [InlineData("xbox context get; echo done", null)]
    [InlineData("xbox game list 2>/dev/null", null)]
    [InlineData("xbox game list --output table # a comment", null)]
    [InlineData("xbox game list --output '#table' --format table", "has unknown `--format table`.")]
    [InlineData("xbox game list --format=<format>", "has unknown `--format=<format>`.")]
    [InlineData("xbox help game list --format=<format>", "has unknown `--format=<format>`.")]
    [InlineData("xbox help", null)]
    [InlineData("xbox help [command...]", null)]
    [InlineData("xbox help playfab economy inventory collection list", null)]
    [InlineData("xbox help playfab contxt", "has unknown `contxt`.")]
    [InlineData("xbox contxt get", "has unknown `contxt get`.")]
    [InlineData("xbox game list --format table", "has unknown `--format table`.")]
    public void ChecksCommandsAndOptionsButNotValues(string commandLine, string? problem) =>
        Assert.Equal(problem, Check(Program.BuildRootCommand(), CommandLine(commandLine)!));

    /// <summary>
    /// The tokens the CLI's parser doesn't recognize, other than placeholders, or
    /// <see langword="null"/> if it recognizes them all. <c>xbox help</c> is checked as the
    /// command it names, with <c>--help</c>.
    /// </summary>
    private static string? Check(Command root, IReadOnlyList<string> tokens)
    {
        var args = tokens.Skip(1).ToList();
        if (args is ["help", ..])
        {
            args = [.. args.Skip(1).Where(token => !IsPlaceholder(token)), "--help"];
        }

        var unmatched = root.Parse(args, Program.NoResponseFileConfig).UnmatchedTokens.Where(token => !IsPlaceholder(token)).ToList();
        return unmatched.Count == 0 ? null : $"has unknown `{string.Join(' ', unmatched)}`.";
    }

    // An option with a placeholder value, like `--format=<format>`, is still an option to check.
    private static bool IsPlaceholder(string token) =>
        !token.StartsWith('-') && (token.IndexOfAny(['<', '[', '{', '$', '*']) >= 0 || token.Contains("...", StringComparison.Ordinal));

    /// <summary>Every <c>xbox</c> command line in the docs, with where it is.</summary>
    private static IEnumerable<(string File, int Line, IReadOnlyList<string> Tokens)> DocumentedCommands()
    {
        var repository = RepositoryRoot();

        // The development repository keeps the user docs in docs/public, and the released source
        // keeps them in docs.
        var docs = Path.Combine(repository, "docs", "public");
        if (!Directory.Exists(docs))
        {
            docs = Path.Combine(repository, "docs");
        }

        var files = Directory.GetFiles(repository, "*.md").Concat(Directory.GetFiles(docs, "*.md", SearchOption.AllDirectories));
        foreach (var file in files.Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(repository, file).Replace('\\', '/');
            var lines = File.ReadAllLines(file);
            string? fence = null;
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();
                if (Fence().Match(trimmed) is { Success: true } opening && (fence is null || trimmed.StartsWith(fence, StringComparison.Ordinal)))
                {
                    fence = fence is null ? opening.Value : null;
                    continue;
                }

                if (fence is not null)
                {
                    var command = Prompt().Replace(trimmed, "");
                    var start = i;
                    // A line ending in a space and a backslash, or in PowerShell a backtick, continues on the next.
                    while ((command.EndsWith(" \\", StringComparison.Ordinal) || command.EndsWith(" `", StringComparison.Ordinal)) && i + 1 < lines.Length)
                    {
                        command = command[..^1] + lines[++i].Trim();
                    }

                    if (CommandLine(command) is { } tokens)
                    {
                        yield return (relative, start + 1, tokens);
                    }

                    continue;
                }

                foreach (Match span in CodeSpan().Matches(line))
                {
                    if (CommandLine(span.Groups[1].Value) is { } tokens)
                    {
                        yield return (relative, i + 1, tokens);
                    }
                }
            }
        }
    }

    /// <summary>
    /// The words of a command line that runs <c>xbox</c>, starting with <c>xbox</c>, or
    /// <see langword="null"/> if it doesn't run it. Environment variables set before it, and a
    /// path to it, like <c>./xbox</c> or <c>.\xbox.exe</c>, are left out.
    /// </summary>
    private static List<string>? CommandLine(string text) =>
        Invocation().Match(text) is { Success: true } invocation ? ["xbox", .. Tokenize(text[invocation.Length..])] : null;

    /// <summary>Splits a shell command line into words, honoring quotes, up to an unquoted pipe, redirection, separator, or comment.</summary>
    private static List<string> Tokenize(string commandLine)
    {
        var tokens = new List<string>();
        var token = new StringBuilder();
        var started = false;
        char? quote = null;
        for (var i = 0; i < commandLine.Length; i++)
        {
            var character = commandLine[i];
            if (quote is not null)
            {
                if (character == quote)
                {
                    quote = null;
                }
                else
                {
                    token.Append(character);
                }

                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                started = true;
            }
            else if (character == '<' && Placeholder().Match(commandLine, i) is { Success: true } placeholder)
            {
                // A placeholder, like `<id>`, not a redirection.
                token.Append(placeholder.Value);
                started = true;
                i += placeholder.Length - 1;
            }
            else if (character is ';' or '|' or '&' or '<' or '>' || (character == '#' && !started))
            {
                // A file descriptor before a redirection, like `2>`, isn't a word.
                if (character == '>' && started && token.ToString().All(char.IsAsciiDigit))
                {
                    started = false;
                }

                break;
            }
            else if (char.IsWhiteSpace(character))
            {
                if (started)
                {
                    tokens.Add(token.ToString());
                    token.Clear();
                    started = false;
                }
            }
            else
            {
                token.Append(character);
                started = true;
            }
        }

        if (started)
        {
            tokens.Add(token.ToString());
        }

        return tokens;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "XboxDevCLI.sln")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "Could not locate the repository root from the test directory.");
        return directory!.FullName;
    }

    [GeneratedRegex("""^(?:[A-Za-z_][A-Za-z0-9_]*=\S*\s+)*(?:&\s*)?(?:"[^"]*[\\/]|'[^']*[\\/]|[^\s"']*[\\/])?xbox(?:\.exe)?["']?(?:\s+|$)""")]
    private static partial Regex Invocation();

    [GeneratedRegex(@"\G<[^\s<>]+>")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^(```+|~~~+)")]
    private static partial Regex Fence();

    [GeneratedRegex(@"^(\$|>|PS>|PS [^>]*>)\s+")]
    private static partial Regex Prompt();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex CodeSpan();
}
