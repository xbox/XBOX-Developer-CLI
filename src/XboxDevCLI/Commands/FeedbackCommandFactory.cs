// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using XboxDevCLI.CommandLine;

namespace XboxDevCLI.Commands;

internal sealed record FeedbackDependencies(
    Func<bool> IsInteractive,
    Func<CancellationToken, Task<string?>> ReadLineAsync,
    Action<ProcessStartInfo> OpenBrowser,
    Func<string> GetEnvironmentSummary)
{
    internal static FeedbackDependencies Default { get; } = new(
        XboxPartnerCommandSupport.IsInteractiveTerminal,
        CancelableReadLine(Console.ReadLine),
        startInfo =>
        {
            using var process = Process.Start(startInfo);
        },
        FeedbackCommandFactory.GetEnvironmentSummary);

    /// <summary>
    /// Wraps a blocking line read so cancellation (Ctrl-C) ends the wait promptly.
    /// Console.In.ReadLineAsync only checks the token before blocking in ReadLine. The read
    /// runs on a background thread; if cancelled it is abandoned, which is safe because the
    /// CLI process exits once the command returns.
    /// </summary>
    internal static Func<CancellationToken, Task<string?>> CancelableReadLine(Func<string?> readLine)
    {
        ArgumentNullException.ThrowIfNull(readLine);
        return cancellationToken => Task.Run(readLine, CancellationToken.None).WaitAsync(cancellationToken);
    }
}

internal static class FeedbackCommandFactory
{
    internal const string BugReportTemplate = "bug_report.yml";
    internal const string EnvironmentField = "environment";

    internal static Command Create(FeedbackDependencies? dependencies = null)
    {
        dependencies ??= FeedbackDependencies.Default;
        var command = new Command("feedback", "Open a CLI bug report with environment information");
        // Command aliases parse normally but are not listed by our Help renderer.
        command.Aliases.Add("report-issue");
        var noBrowser = new Option<bool>("--no-browser")
        {
            Description = "Print the environment summary and issue URL without prompting or opening a browser.",
        };
        command.Options.Add(noBrowser);
        command.WithMetadata(new CommandMetadata(
            Category: HelpCategory.Support,
            Safety: CommandSafety.ReadOnly,
            Scope: "The running CLI and its OS and .NET runtime. No sign-in or title ID is required.",
            Output: "Human-readable environment summary and a prefilled GitHub issue URL. Does not support --output table.",
            SafetyDetails: "Opens a draft only after you agree. Never submits an issue. Review the public report for sensitive information before submitting.",
            Examples: ["xbox feedback", "xbox feedback --no-browser"],
            LearnMore: [ProjectLinks.NewIssue]));
        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            if (string.Equals(parseResult.GetValue(Program.OutputOption), "table", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine(Program.TableUnavailableMessage);
                return 1;
            }

            var printOnly = parseResult.GetValue(noBrowser);
            if (!printOnly && !dependencies.IsInteractive())
            {
                Console.Error.WriteLine(
                    "Feedback requires an interactive terminal. Run 'xbox feedback --no-browser' to print the issue URL without opening a browser.");
                return 1;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var environment = dependencies.GetEnvironmentSummary();
                Console.Out.WriteLine("Environment Summary");
                Console.Out.WriteLine(environment);
                Console.Error.WriteLine(
                    "GitHub issues are public. Review the environment information and remove sensitive data from anything you add. No issue is submitted automatically.");

                if (!printOnly)
                {
                    while (true)
                    {
                        Console.Error.Write("Open a browser to draft a bug report? [y/N]: ");
                        var answer = (await dependencies.ReadLineAsync(cancellationToken))?.Trim();
                        cancellationToken.ThrowIfCancellationRequested();
                        if (string.IsNullOrEmpty(answer)
                            || answer.Equals("n", StringComparison.OrdinalIgnoreCase)
                            || answer.Equals("no", StringComparison.OrdinalIgnoreCase)
                            || answer.Equals("q", StringComparison.OrdinalIgnoreCase))
                        {
                            Console.Error.WriteLine("Feedback cancelled. No browser was opened.");
                            return 0;
                        }
                        if (answer.Equals("y", StringComparison.OrdinalIgnoreCase)
                            || answer.Equals("yes", StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }
                        Console.Error.WriteLine("Enter Y to open the draft, or N to cancel.");
                    }
                }

                var url = BuildIssueUrl(environment);
                Console.Out.WriteLine($"Create an issue at: {url}");
                if (!printOnly)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        // Use the OS URL handler, including open/xdg-open on macOS/Linux.
                        dependencies.OpenBrowser(new ProcessStartInfo(url) { UseShellExecute = true });
                        Console.Error.WriteLine(
                            "Requested browser opening. If it does not open, copy the URL above into your browser.");
                    }
                    catch (Exception exception) when (
                        exception is Win32Exception or InvalidOperationException or IOException
                            or UnauthorizedAccessException or NotSupportedException)
                    {
                        Console.Error.WriteLine(
                            $"Could not open a browser: {exception.Message} Copy the URL above into your browser.");
                    }
                }
                return 0;
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Feedback cancelled.");
                return 1;
            }
            catch (IOException exception)
            {
                Console.Error.WriteLine($"Feedback failed: {exception.Message}");
                return 1;
            }
        });
        return command.Requires(CommandRequirement.None);
    }

    internal static string BuildIssueUrl(string environmentSummary) =>
        $"{ProjectLinks.NewIssue}?template={BugReportTemplate}&{EnvironmentField}={Uri.EscapeDataString(environmentSummary)}";

    internal static string GetEnvironmentSummary() =>
        $"XBOX CLI: {CliIdentity.Version}\n"
        + $"OS: {RuntimeInformation.OSDescription}\n"
        + $"OS architecture: {RuntimeInformation.OSArchitecture}\n"
        + $"Process architecture: {RuntimeInformation.ProcessArchitecture}\n"
        + $"Runtime: {RuntimeInformation.FrameworkDescription}";
}
