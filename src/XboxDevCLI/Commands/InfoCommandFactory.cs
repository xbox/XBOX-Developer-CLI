// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using XboxDevCLI.Presentation;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

internal static class InfoCommandFactory
{
    internal static string Banner => WelcomeExperience.PlainLogo;

    public static Command Create(
        Func<bool>? useColor = null,
        Func<bool>? isInteractive = null,
        Func<string>? stateDirectory = null)
    {
        var command = new Command("info", "Show version and getting-started links");
        command.SetAction((ParseResult _) =>
        {
            var isInteractiveOutput = isInteractive?.Invoke()
                ?? (Environment.UserInteractive && !Console.IsOutputRedirected);
            var colorEnabled =
                useColor?.Invoke() ?? WelcomeExperience.SupportsColor(Console.IsOutputRedirected);
            WelcomeExperience.WriteInfo(Console.Out, CliIdentity.Version, colorEnabled);

            if (isInteractiveOutput)
            {
                WelcomeExperience.MarkShown(
                    stateDirectory?.Invoke() ?? CliState.GetDefaultDirectory(),
                    CliIdentity.Version);
            }

            return 0;
        });

        return command;
    }
}
