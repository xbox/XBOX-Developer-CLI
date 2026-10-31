// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Help;

namespace XboxDevCLI.CommandLine;

internal static class CommandLineExperience
{
    /// <summary>
    /// Installs the unified Help experience: one renderer for <c>--help</c> at every depth, a
    /// path-aware <c>help</c> command, and Help as the default action for the root and for any
    /// command that only groups subcommands.
    /// </summary>
    internal static void ConfigureHelp(RootCommand rootCommand, string cliName)
    {
        ArgumentNullException.ThrowIfNull(rootCommand);
        ArgumentException.ThrowIfNullOrWhiteSpace(cliName);

        var defaultHelpOption = rootCommand.Options
            .OfType<HelpOption>()
            .Single();
        rootCommand.Options.Remove(defaultHelpOption);

        var helpOption = new HelpOption("--help", "-h")
        {
            Description = "Show help for the command.",
            Action = new CommandHelpAction(rootCommand, cliName),
        };
        rootCommand.Options.Add(helpOption);

        rootCommand.Action = new HelpRenderingAction(parseResult =>
        {
            CommandHelpWriter.Write(
                parseResult.InvocationConfiguration.Output,
                rootCommand,
                rootCommand,
                cliName,
                useAnsi: CommandHelpWriter.ShouldUseAnsi());
            return 0;
        });

        rootCommand.Subcommands.Add(BuildHelpCommand(rootCommand, cliName));

        SetGroupHelpActions(rootCommand, rootCommand, cliName);
    }

    /// <summary>
    /// Builds the <c>help</c> command. It is visible and takes an optional command path, so
    /// <c>xbox help playfab file list</c> renders the same output as
    /// <c>xbox playfab file list --help</c> at any depth.
    /// </summary>
    private static Command BuildHelpCommand(RootCommand rootCommand, string cliName)
    {
        var pathArgument = new Argument<string[]>("command")
        {
            Description = "The command path to show help for, such as `playfab file list`.",
            Arity = ArgumentArity.ZeroOrMore,
        };

        var helpCommand = new Command("help")
        {
            Description = "Show help for any command",
        };
        helpCommand.Arguments.Add(pathArgument);
        helpCommand.WithMetadata(new CommandMetadata(Category: HelpCategory.Support));

        helpCommand.Action = new HelpRenderingAction(parseResult =>
        {
            var segments = parseResult.GetValue(pathArgument) ?? [];
            var output = parseResult.InvocationConfiguration.Output;

            if (!CommandPath.TryResolve(
                    rootCommand,
                    segments,
                    out var target,
                    out var unresolvedSegment,
                    out var resolvedParent))
            {
                CommandLineErrorWriter.WriteUnknownCommand(
                    rootCommand,
                    resolvedParent,
                    unresolvedSegment!,
                    cliName,
                    parseResult.InvocationConfiguration.Error);
                return 1;
            }

            CommandHelpWriter.Write(
                output,
                rootCommand,
                target,
                cliName,
                useAnsi: CommandHelpWriter.ShouldUseAnsi());
            return 0;
        });

        return helpCommand;
    }

    /// <summary>
    /// Makes every command that only groups subcommands print its own Help when invoked bare,
    /// instead of failing with a "required command was not provided" parse error.
    /// </summary>
    private static void SetGroupHelpActions(RootCommand rootCommand, Command command, string cliName)
    {
        foreach (var subcommand in command.Subcommands)
        {
            if (subcommand.Subcommands.Count > 0)
            {
                // Only supply the default group action. A factory that has already set its own
                // action on a command with subcommands did so deliberately, so don't clobber it.
                if (subcommand.Action is null &&
                    subcommand.Subcommands.Any(child => !child.Hidden))
                {
                    subcommand.Action = new HelpRenderingAction(parseResult =>
                    {
                        CommandHelpWriter.Write(
                            parseResult.InvocationConfiguration.Output,
                            rootCommand,
                            subcommand,
                            cliName,
                            useAnsi: CommandHelpWriter.ShouldUseAnsi());
                        return 0;
                    });
                }

                SetGroupHelpActions(rootCommand, subcommand, cliName);
            }
        }
    }
}
