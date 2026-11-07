// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using Microsoft.Extensions.Configuration;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

internal static class LoginCommandFactory
{
    public static Command Create() => Create(Program.GetAuthentication);

    internal static Command Create(Func<ParseResult, AuthenticationService> authenticationServiceFactory)
    {
        var command = new Command("login", "Sign in with Microsoft Entra ID");

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var authService = authenticationServiceFactory(parseResult);
            if (authService.IsAutomation)
            {
                Console.Error.WriteLine(authService.GetNotApplicableMessage("login"));
                return 1;
            }

            var result = await authService.LoginAsync(cancellationToken);
            Console.WriteLine($"Successfully signed in as: {result.Account.Username}");
            Console.WriteLine($"Token expires at: {result.ExpiresOn.LocalDateTime}");
            return 0;
        });

        return command.FormatsErrors((_, message) => $"Login failed: {message}");
    }
}
