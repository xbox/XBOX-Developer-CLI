// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using System.Net;
using System.Text.Json;

namespace XboxDevCLI.Services;

internal static class XboxPartnerErrorGuidance
{
    /// <summary>The problem <c>code</c> of a 429 from the service's own per-user request limit.</summary>
    internal const string RateLimitedCode = "rate_limited";

    /// <summary>The cause of a <c>rate_limited</c> 429; <see cref="DescribeRetry"/> adds the wait.</summary>
    internal const string RateLimitedHint =
        "You sent too many requests in a short time. If a script runs these commands, slow it down.";

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
                "The request was rejected. Check the supplied input "
                + "and rerun with --verbose to see the full response.",
            HttpStatusCode.Unauthorized when isAutomation =>
                "Authentication failed for the service principal selected by "
                + $"{AutomationAuthenticationSettings.ModeVariable}. Check its credentials, or run "
                + $"'{CliIdentity.CommandName} auth status' to see the identity in use.",
            HttpStatusCode.Unauthorized =>
                $"Authentication failed. Run '{CliIdentity.CommandName} auth login' and retry.",
            HttpStatusCode.Forbidden =>
                "The authenticated user or CLI application is not authorized for this request.",
            HttpStatusCode.Conflict =>
                "The request conflicts with existing state; the resource may already exist.",
            HttpStatusCode.NotFound when IsSellersRequest(exception.RequestUri) =>
                "The seller list could not be retrieved. If you set an endpoint override, "
                + "check XboxPartner:BaseUrl, XBOX_XBOXPARTNER__BASEURL, or --base-url.",
            HttpStatusCode.NotFound =>
                "The requested resource was not found. Check the IDs in the command.",
            HttpStatusCode.TooManyRequests when IsRateLimited(exception.ResponseBody) =>
                $"{RateLimitedHint} {DescribeRetry(exception)}",
            HttpStatusCode.TooManyRequests =>
                $"The request was throttled. {DescribeRetry(exception)}",
            HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable =>
                "The service is temporarily unavailable.",
            HttpStatusCode.GatewayTimeout =>
                "The request timed out.",
            _ =>
                "The request failed.",
        };

        string status = $"HTTP {(int)exception.StatusCode}"
            + (string.IsNullOrWhiteSpace(exception.ReasonPhrase)
                ? "."
                : $" ({Output.TerminalText.SingleLine(exception.ReasonPhrase)}).");
        diagnosis = $"{diagnosis}{Environment.NewLine}{status}";
        if (exception.InnerException is { } bodyReadFailure)
        {
            diagnosis += $"{Environment.NewLine}The response body could not be read.";
            if (bodyReadFailure is InvalidOperationException)
            {
                diagnosis += $" {Output.TerminalText.SingleLine(bodyReadFailure.Message)}";
            }
        }

        return detail == null
            ? diagnosis
            : $"{diagnosis}{Environment.NewLine}Error detail: {detail}";
    }

    internal static bool IsRateLimited(string? responseBody) =>
        Commands.TestAccountCommandFactory.ReadProblemString(responseBody, "code") == RateLimitedCode;

    /// <summary>A 429's <c>Retry-After</c> wait, rounded up to whole seconds and at least one.</summary>
    internal static long? RetryAfterSeconds(XboxPartnerApiException exception) =>
        exception.StatusCode == HttpStatusCode.TooManyRequests && exception.RetryAfter is { } retryAfter
            ? (long)Math.Max(1, Math.Ceiling(retryAfter.TotalSeconds))
            : null;

    internal static string DescribeRetry(XboxPartnerApiException exception) =>
        RetryAfterSeconds(exception) is { } seconds
            ? $"Try again in {seconds.ToString(CultureInfo.InvariantCulture)} {(seconds == 1 ? "second" : "seconds")}."
            : "Wait before retrying.";

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
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }

        return null;
    }
}
