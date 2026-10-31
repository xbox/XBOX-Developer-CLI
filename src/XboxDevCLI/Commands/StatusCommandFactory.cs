// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

internal static class StatusCommandFactory
{
    /// <summary>
    /// Builds the <c>status</c> command, which reports the signed-in developer identity.
    /// Human-readable text is the default; passing <c>--output</c> (<c>json</c> or
    /// <c>json-compact</c>) emits machine-readable JSON for agents. The PlayFab title and other
    /// context values are reported by <c>context get</c>, not here.
    /// </summary>
    public static Command Create() =>
        Create(Program.GetAuthentication);

    /// <summary><see cref="Create()"/> with the sign-in service supplied, for tests.</summary>
    internal static Command Create(Func<ParseResult, AuthenticationService> authFactory)
    {
        var command = new Command("status", "Show authentication status");

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            // status defaults to human-readable text. When --output is explicitly provided
            // (json or json-compact), emit machine-readable JSON instead.
            var outputFormat = parseResult.GetValue(Program.OutputOption);
            var asJson = outputFormat != null;
            var compact = string.Equals(outputFormat, "json-compact", StringComparison.OrdinalIgnoreCase);

            IAccount? account;
            AuthenticationService? authService = null;
            try
            {
                // Refused here rather than before the action, so JSON output keeps its error shape.
                Program.RefuseUserAuthenticationOptionsInAutomation(parseResult);
                ContextResolver.RefuseProfileInAutomation(
                    parseResult,
                    ContextProfiles.Normalize(parseResult.GetValue(ContextProfiles.Option)));
                authService = authFactory(parseResult);
                if (authService.IsAutomation)
                {
                    return await WriteServicePrincipalStatusAsync(authService, asJson, compact, cancellationToken);
                }

                account = await authService.GetCachedAccountAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (asJson)
                {
                    // In a service-principal mode, any failure before a token is acquired, such as
                    // invalid settings or a missing --config-env overlay, is a failure, not a
                    // signed-out state.
                    if (ex is AuthenticationConfigurationException
                        || (authService ?? Program.GetAuthentication(parseResult)).IsAutomationSelected)
                    {
                        Console.Error.WriteLine(SerializeStatus(new { signedIn = false, error = ex.Message }, compact));
                        return 1;
                    }

                    // A user who can't be read as signed in is reported as signed out, so agents
                    // can probe sign-in state without handling a failure.
                    Console.WriteLine(SerializeStatus(new { signedIn = false }, compact));
                    return 0;
                }

                Console.Error.WriteLine($"Failed to retrieve status: {ex.Message}");
                return 1;
            }

            if (account == null)
            {
                if (asJson)
                {
                    Console.WriteLine(SerializeStatus(new { signedIn = false }, compact));
                    return 0;
                }

                Console.WriteLine(
                    $"Not signed in. Run '{CliIdentity.CommandName} auth login' to authenticate.");
                return 0;
            }

            AuthenticationResult? token = null;
            try
            {
                token = await authService.AcquireTokenSilentAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Account is known; omit token-derived fields rather than failing the status read.
                // Cancellation propagates so an interrupted status never reports success.
            }

            if (asJson)
            {
                var payload = new
                {
                    signedIn = true,
                    username = account.Username,
                    homeAccountId = account.HomeAccountId?.Identifier,
                    environment = account.Environment,
                    tenantId = token?.TenantId,
                    tokenType = token?.TokenType,
                    tokenExpiresAt = token?.ExpiresOn,
                    scopes = token?.Scopes?.ToArray(),
                };
                Console.WriteLine(SerializeStatus(payload, compact));
                return 0;
            }

            Console.WriteLine($"Signed in as: {account.Username}");
            Console.WriteLine($"Home Account ID: {account.HomeAccountId}");
            Console.WriteLine($"Environment: {account.Environment}");
            if (token != null)
            {
                Console.WriteLine($"Tenant ID: {token.TenantId}");
                Console.WriteLine($"Token type: {token.TokenType}");
                Console.WriteLine($"Token expires at: {token.ExpiresOn.LocalDateTime}");
                Console.WriteLine($"Scopes: {string.Join(", ", token.Scopes)}");
            }

            return 0;
        });

        return command.RefusesAutomationOptionsInAction();
    }

    /// <summary>
    /// Reports a service-principal mode by acquiring an app-only token and describing it from its
    /// claims. The raw token is never printed. A failed acquisition exits 1; with JSON output it
    /// writes <c>signedIn: false</c> and the error to stderr, leaving stdout empty.
    /// </summary>
    private static async Task<int> WriteServicePrincipalStatusAsync(
        AuthenticationService authService,
        bool asJson,
        bool compact,
        CancellationToken cancellationToken)
    {
        var mode = authService.SelectedModeName;
        AuthenticationResult token;
        try
        {
            token = (await authService.AcquireTokenSilentAsync(cancellationToken))
                ?? throw new InvalidOperationException("No access token was returned.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (asJson)
            {
                Console.Error.WriteLine(SerializeStatus(
                    new { signedIn = false, authenticationMode = mode, error = ex.Message },
                    compact));
                return 1;
            }

            Console.Error.WriteLine($"Failed to acquire a token in '{mode}' mode: {ex.Message}");
            return 1;
        }

        var claims = AccessTokenClaims.TryDecode(token.AccessToken);
        if (asJson)
        {
            Console.WriteLine(SerializeStatus(new
            {
                signedIn = true,
                authenticationMode = mode,
                principalType = claims?.PrincipalType ?? "unknown",
                tenantId = claims?.TenantId ?? token.TenantId,
                appId = claims?.AppId,
                objectId = claims?.ObjectId,
                audience = claims?.Audience,
                roles = claims?.Roles.ToArray() ?? [],
                tokenType = token.TokenType,
                tokenExpiresAt = token.ExpiresOn,
                scopes = token.Scopes?.ToArray(),
            }, compact));
            return 0;
        }

        Console.WriteLine($"Authentication mode: {mode}");
        Console.WriteLine($"Principal type: {claims?.PrincipalType ?? "unknown"}");
        Console.WriteLine($"Tenant ID: {claims?.TenantId ?? token.TenantId}");
        Console.WriteLine($"App ID: {claims?.AppId ?? "(unknown)"}");
        Console.WriteLine($"Object ID: {claims?.ObjectId ?? "(unknown)"}");
        Console.WriteLine($"Audience: {claims?.Audience ?? "(unknown)"}");
        Console.WriteLine($"Roles: {(claims is { Roles.Count: > 0 } ? string.Join(", ", claims.Roles) : "(none)")}");
        Console.WriteLine($"Token expires at: {token.ExpiresOn.LocalDateTime}");
        return 0;
    }

    /// <summary>
    /// Serializes a status payload as JSON. <paramref name="compact"/> selects single-line output.
    /// </summary>
    private static string SerializeStatus(object payload, bool compact)
    {
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = !compact,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }
}
