// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using Microsoft.Extensions.Configuration;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

internal static class LogoutCommandFactory
{
    public static Command Create() => Create(Program.GetAuthentication);

    internal static Command Create(Func<ParseResult, AuthenticationService> authenticationServiceFactory)
    {
        var command = new Command("logout", "Sign out and clear cached Microsoft Entra credentials");

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var authService = authenticationServiceFactory(parseResult);
            if (authService.IsAutomation)
            {
                Console.Error.WriteLine(authService.GetNotApplicableMessage("logout"));
                return 1;
            }

            var result = await authService.LogoutAsync(cancellationToken);

            if (result.RemovedAccountCount == 0)
            {
                Console.WriteLine("No account is currently signed in.");
                return 0;
            }

            var usernames = result.Usernames
                .Where(username => !string.IsNullOrWhiteSpace(username))
                .ToArray();

            if (result.RemovedAccountCount == 1)
            {
                Console.WriteLine(usernames.Length == 0
                    ? "Signed out 1 account."
                    : $"Signed out {usernames[0]}.");
                return 0;
            }

            Console.WriteLine(usernames.Length == 0
                ? $"Signed out {result.RemovedAccountCount} accounts."
                : $"Signed out {result.RemovedAccountCount} accounts: {string.Join(", ", usernames)}.");
            return 0;
        });

        return command.FormatsErrors((_, message) => $"Logout failed: {message}");
    }
}
