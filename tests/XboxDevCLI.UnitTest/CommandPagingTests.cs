// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Reflection;
using System.Text.Json.Serialization;
using XboxDevCLI.CommandLine;
using XboxDevCLI.PlayFab.Generated;
using Xunit;

namespace XboxDevCLI.UnitTest;

/// <summary>
/// Guards the declaration that a command returns one page per call: that every MPS command
/// whose API pages declares it, that each declaration names fields the operation really has,
/// and that Help and the table notice say what the declaration means.
/// </summary>
public class CommandPagingTests
{
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

    /// <summary>
    /// Resolves a model in its endpoint's group, since some names, such as
    /// <c>EmptyResponse</c>, are generated in more than one.
    /// </summary>
    private static Type? ResolveGeneratedType(string group, string? typeName) =>
        typeName is null
            ? null
            : typeof(PlayFabApiMetadata).Assembly.GetType(
                $"XboxDevCLI.PlayFab.Generated.{group}.{typeName}");

    private static bool HasWireField(Type? type, string wireName) =>
        type is not null
        && type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Any(property =>
                (property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name)
                    .Equals(wireName, StringComparison.Ordinal));

    private static (Type? Request, Type? Response) ModelsFor(string apiPath)
    {
        var endpoint = PlayFabApiMetadata.AllEndpoints.Single(candidate =>
            string.Equals(candidate.Path, apiPath, StringComparison.OrdinalIgnoreCase));
        return (
            ResolveGeneratedType(endpoint.Group, endpoint.RequestType),
            ResolveGeneratedType(endpoint.Group, endpoint.ResponseType));
    }

    /// <summary>
    /// An MPS command whose operation takes and returns a next-page token returns one page, so
    /// it must say so. Walking the tree rather than naming the commands catches one added later.
    /// </summary>
    [Fact]
    public void EveryPagedMpsCommandDeclaresPaging()
    {
        var root = Program.BuildRootCommand();
        var mps = AllCommands(root)
            .Where(entry => entry.Path.StartsWith($"{root.Name} playfab mps ", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(mps);

        var paged = 0;
        foreach (var (path, command) in mps)
        {
            var metadata = CommandMetadataRegistry.GetMetadata(command);
            if (metadata.ApiPaths is not [var apiPath])
            {
                continue;
            }

            var (request, response) = ModelsFor(apiPath);
            var pages = HasWireField(request, "SkipToken") && HasWireField(response, "SkipToken");

            Assert.True(
                pages == metadata.Paging is not null,
                pages
                    ? $"'{path}' calls {apiPath}, which returns one page per call, but declares no paging."
                    : $"'{path}' declares paging, but {apiPath} takes no next-page token.");
            paged += pages ? 1 : 0;
        }

        Assert.Equal(10, paged);
    }

    /// <summary>
    /// The field names in a declaration are what Help and the notice tell the user to send, so
    /// each must exist on the request, and the token on the response, of the operation called.
    /// </summary>
    [Fact]
    public void EveryPagingDeclarationNamesRealFields()
    {
        var checkedDeclarations = 0;
        foreach (var (path, command) in AllCommands(Program.BuildRootCommand()))
        {
            var metadata = CommandMetadataRegistry.GetMetadata(command);
            if (metadata.Paging is not { } paging)
            {
                continue;
            }

            Assert.True(metadata.ApiPaths is { Count: > 0 }, $"'{path}' declares paging but calls no API.");
            foreach (var apiPath in metadata.ApiPaths!)
            {
                var (request, response) = ModelsFor(apiPath);
                Assert.True(HasWireField(request, paging.PageSize), $"'{path}': {request?.Name} has no '{paging.PageSize}'.");
                Assert.True(HasWireField(request, paging.Token), $"'{path}': {request?.Name} has no '{paging.Token}'.");
                Assert.True(HasWireField(response, paging.Token), $"'{path}': {response?.Name} has no '{paging.Token}'.");
                checkedDeclarations++;
            }
        }

        Assert.True(checkedDeclarations > 0, "No paging declarations were checked.");
    }

    [Fact]
    public void HelpForAPagedCommandSaysItReturnsOnePage()
    {
        var root = Program.BuildRootCommand();
        var list = root.Subcommands.Single(command => command.Name == "playfab")
            .Subcommands.Single(command => command.Name == "mps")
            .Subcommands.Single(command => command.Name == "build")
            .Subcommands.Single(command => command.Name == "list");
        using var writer = new StringWriter();

        CommandHelpWriter.Write(writer, root, list, CliIdentity.CommandName, width: 100);

        var help = string.Join(' ', writer.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("PAGING", help, StringComparison.Ordinal);
        Assert.Contains("Returns one page of results", help, StringComparison.Ordinal);
        Assert.Contains("\"SkipToken\"", help, StringComparison.Ordinal);
        Assert.Contains("\"PageSize\"", help, StringComparison.Ordinal);
    }

    /// <summary>
    /// The token comes from the service and is written to the terminal, so anything outside
    /// printable ASCII is escaped: the notice stays valid JSON to paste, and can't carry an
    /// escape sequence or a bidi override to the terminal.
    /// </summary>
    [Fact]
    public void NoticeEscapesTheTokenForTheTerminal()
    {
        const string Response =
            """{"data":{"SkipToken":"a\"b\\c\u001b[31m\u202Ed"}}""";

        var notice = Program.FormatNextPageNotice(Response, new CommandPaging("PageSize", "SkipToken"));

        Assert.NotNull(notice);
        Assert.Contains("""
            "SkipToken": "a\"b\\c\u001b[31m\u202ed"
            """.Trim(), notice, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', notice);
        Assert.DoesNotContain('\u202e', notice);
    }
}
