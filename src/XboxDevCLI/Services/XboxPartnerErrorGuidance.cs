// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json;

namespace XboxDevCLI.Services;

internal static class XboxPartnerErrorGuidance
{
    public static string Describe(XboxPartnerApiException exception, bool isAutomation)
    {
        // The detail is server-supplied text written to the terminal.
        var detail = Output.TerminalText.SingleLine(TryReadProblemDetail(exception.ResponseBody));
        if (string.IsNullOrWhiteSpace(detail))
        {
            detail = null;
        }
        var diagnosis = exception.StatusCode switch
        {
            HttpStatusCode.BadRequest =>
                "The XboxPartner gateway rejected the request. Verify the gateway base URL "
                + "and rerun with --verbose for the gateway detail.",
            HttpStatusCode.Unauthorized when isAutomation =>
                "Authentication failed for the service principal selected by "
                + $"{AutomationAuthenticationSettings.ModeVariable}. Check its credentials, or run "
                + $"'{CliIdentity.CommandName} auth status' to see the identity in use.",
            HttpStatusCode.Unauthorized =>
                $"Authentication failed. Run '{CliIdentity.CommandName} auth login' and retry.",
            HttpStatusCode.Forbidden =>
                "The signed-in user or CLI application is not authorized for this XboxPartner request.",
            HttpStatusCode.Conflict =>
                "The request conflicts with existing Partner Center state; the resource may already exist.",
            HttpStatusCode.NotFound when IsSellersRequest(exception.RequestUri) =>
                $"The seller endpoint was not found at '{exception.RequestUri}'. "
                + "This indicates a gateway deployment or base URL issue. Having no sellers is "
                + "returned as HTTP 200 with an empty sellers array.",
            HttpStatusCode.NotFound =>
                $"The XboxPartner gateway returned 404 Not Found for '{exception.RequestUri}'. "
                + "Check the IDs in the command.",
            HttpStatusCode.TooManyRequests =>
                "The downstream service throttled the request. Wait before retrying.",
            HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable =>
                "The XboxPartner gateway or Partner Center is temporarily unavailable.",
            HttpStatusCode.GatewayTimeout =>
                "The XboxPartner request timed out at the gateway or Partner Center.",
            _ =>
                $"The XboxPartner gateway returned HTTP {(int)exception.StatusCode} "
                + $"({exception.StatusCode}).",
        };

        return detail == null
            ? diagnosis
            : $"{diagnosis}{Environment.NewLine}Gateway detail: {detail}";
    }

    private static bool IsSellersRequest(Uri requestUri) =>
        requestUri.AbsolutePath.TrimEnd('/').EndsWith(
            "/xboxpartner/v1/sellers", StringComparison.OrdinalIgnoreCase);

    private static string? TryReadProblemDetail(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            foreach (var propertyName in new[] { "detail", "message", "title" })
            {
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty(propertyName, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(value.GetString()))
                {
                    return value.GetString();
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return null;
        }

        return null;
    }
}
