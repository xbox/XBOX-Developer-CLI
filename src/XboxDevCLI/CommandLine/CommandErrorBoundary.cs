// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using XboxDevCLI.Output;

namespace XboxDevCLI.CommandLine;

/// <summary>
/// Reports the expected failures of preparing and running a command the same way for every
/// command: one message on stderr, made safe for a terminal, and exit code 1. A command whose
/// errors have their own shape, such as <c>auth status</c>'s JSON, declares a format with
/// <see cref="FormatsErrors{TCommand}"/> rather than catching them itself.
/// </summary>
/// <remarks>
/// Only the failures a correct CLI can meet are reported here: missing or invalid files and
/// settings, sign-in and token failures, and network errors. Anything else is a bug and
/// surfaces as one. Cancellation is never caught, so Ctrl-C keeps its meaning. Messages come from
/// the components that raised them, which name settings and files but never their contents, and
/// inner exceptions are never written.
/// </remarks>
internal static class CommandErrorBoundary
{
    private static readonly ConditionalWeakTable<Command, Func<ParseResult, string, string>> Formats = new();

    /// <summary>
    /// Declares how <paramref name="command"/> writes a reported failure: <paramref name="format"/>
    /// turns the invocation and the failure's message into what's written to stderr.
    /// </summary>
    internal static TCommand FormatsErrors<TCommand>(this TCommand command, Func<ParseResult, string, string> format)
        where TCommand : Command
    {
        Formats.AddOrUpdate(command, format);
        return command;
    }

    /// <summary>Whether <paramref name="exception"/> is an expected failure the boundary reports.</summary>
    internal static bool IsExpected(Exception exception) =>
        exception is (InvalidOperationException and not ObjectDisposedException)
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or MsalException
            or MsalCachePersistenceException
            or HttpRequestException;

    /// <summary>Writes <paramref name="exception"/> in the command's format, and returns exit code 1.</summary>
    internal static int Report(ParseResult parseResult, Exception exception)
    {
        // Line breaks are kept, written as the platform's, and any other control character removed.
        var message = TerminalText.MultiLine(exception.Message.ReplaceLineEndings("\n")).ReplaceLineEndings();
        Console.Error.WriteLine(Formats.TryGetValue(parseResult.CommandResult.Command, out var format)
            ? format(parseResult, message)
            : message);
        return 1;
    }

    /// <summary>
    /// Has every command action under <paramref name="root"/> prepare the invocation's
    /// dependencies first, and report expected failures through the boundary. Help actions are
    /// left alone, so Help never depends on configuration or sign-in.
    /// </summary>
    internal static void Install(Command root)
    {
        if (root.Action is { } action and not (PreparingAction or HelpAction or CommandHelpAction or HelpRenderingAction))
        {
            root.Action = new PreparingAction(action);
        }

        foreach (var subcommand in root.Subcommands)
        {
            Install(subcommand);
        }
    }

    /// <summary>
    /// Runs a command's own action after the invocation is prepared, which <c>Program.Main</c> has
    /// usually done already, reporting expected failures from either.
    /// </summary>
    private sealed class PreparingAction(CommandLineAction inner) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            try
            {
                Program.Prepare(parseResult);
                return inner switch
                {
                    AsynchronousCommandLineAction asynchronous =>
                        await asynchronous.InvokeAsync(parseResult, cancellationToken),
                    SynchronousCommandLineAction synchronous => synchronous.Invoke(parseResult),
                    _ => throw new UnreachableException($"Unsupported action type {inner.GetType()}."),
                };
            }
            catch (Exception exception) when (IsExpected(exception))
            {
                return Report(parseResult, exception);
            }
        }
    }
}
