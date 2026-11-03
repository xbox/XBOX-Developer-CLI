// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

internal static class XboxPartnerCommandErrorFormatter
{
    public static string Format(
        XboxPartnerApiException exception,
        bool compact,
        string? additionalDetail = null,
        bool includeValidationErrors = false,
        bool omitAdditionalDetailWithFieldErrors = false,
        IReadOnlyList<string>? sensitiveValues = null,
        string fallbackTitle = "Request failed",
        string? fallbackDetail = null)
    {
        string failureMessage = fallbackDetail ?? exception.Message;
        long? retryAfterSeconds = XboxPartnerErrorGuidance.RetryAfterSeconds(exception);
        if (exception.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            string retryGuidance = XboxPartnerErrorGuidance.DescribeRetry(exception);
            if (XboxPartnerErrorGuidance.IsRateLimited(exception.ResponseBody) &&
                additionalDetail?.Contains(XboxPartnerErrorGuidance.RateLimitedHint, StringComparison.Ordinal) != true)
            {
                retryGuidance = $"{XboxPartnerErrorGuidance.RateLimitedHint} {retryGuidance}";
            }

            additionalDetail = string.IsNullOrWhiteSpace(additionalDetail)
                ? retryGuidance
                : AppendDetail(additionalDetail, retryGuidance);
        }

        try
        {
            using var document = JsonDocument.Parse(exception.ResponseBody);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return FormatFallback();
            }

            JsonElement root = document.RootElement;
            string structuredFailure = fallbackDetail ?? DescribeStatus(exception);
            var includesDiagnosticDetails =
                TryGetProperty(root, "exceptionDetails", out _) ||
                TryGetProperty(root, "headers", out _) ||
                TryGetProperty(root, "stackTrace", out _);
            Dictionary<string, string[]>? fieldErrors = includesDiagnosticDetails ||
                exception.StatusCode != System.Net.HttpStatusCode.BadRequest
                ? null
                : ReadFieldErrors(root, allowPartial: includeValidationErrors);
            string detail = AppendDetail(
                includesDiagnosticDetails
                    ? structuredFailure
                    : GetString(root, "detail") ?? structuredFailure,
                omitAdditionalDetailWithFieldErrors && fieldErrors is not null
                    ? null
                    : additionalDetail);
            if (fieldErrors is not null)
            {
                fieldErrors = fieldErrors.ToDictionary(
                    pair => Redact(pair.Key, sensitiveValues),
                    pair => pair.Value.Select(value => Redact(value, sensitiveValues)).ToArray(),
                    StringComparer.Ordinal);
            }

            var sanitized = new Dictionary<string, object?>
            {
                ["status"] = (int)exception.StatusCode,
                ["title"] = Redact(includesDiagnosticDetails
                    ? fallbackTitle
                    : GetString(root, "title") ?? fallbackTitle, sensitiveValues),
                ["detail"] = Redact(detail, sensitiveValues),
            };
            if (!includesDiagnosticDetails && GetString(root, "code") is { Length: > 0 } code)
            {
                sanitized["code"] = Redact(code, sensitiveValues);
            }

            if (fieldErrors is not null)
            {
                sanitized["errors"] = fieldErrors;
            }

            if (retryAfterSeconds is { } seconds)
            {
                sanitized["retryAfterSeconds"] = seconds;
            }

            if (!includesDiagnosticDetails &&
                TryGetProperty(root, "validation", out JsonElement validation) &&
                XboxLiveValidationMessages.Read(validation).Where(static message => message.HasText).ToArray()
                    is { Length: > 0 } messages)
            {
                sanitized["validation"] = messages
                    .Select(message => FormatValidationMessage(message, sensitiveValues))
                    .ToArray();
            }

            return PlayFabApiClient.FormatJson(JsonSerializer.Serialize(sanitized), compact);
        }
        catch (Exception parseException) when (parseException is JsonException or ArgumentException
            or InvalidOperationException)
        {
            return FormatFallback();
        }

        string FormatFallback()
        {
            string detail = Redact(AppendDetail(failureMessage, additionalDetail), sensitiveValues);
            if (retryAfterSeconds is not { } seconds)
            {
                return detail;
            }

            var sanitized = new Dictionary<string, object?>
            {
                ["status"] = (int)exception.StatusCode,
                ["title"] = Redact(fallbackTitle, sensitiveValues),
                ["detail"] = detail,
                ["retryAfterSeconds"] = seconds,
            };
            return PlayFabApiClient.FormatJson(JsonSerializer.Serialize(sanitized), compact);
        }
    }

    internal static string Redact(string value, IReadOnlyList<string>? sensitiveValues)
    {
        if (sensitiveValues is not null)
        {
            foreach (string sensitive in sensitiveValues)
            {
                if (sensitive.Length > 0)
                {
                    value = value.Replace(sensitive, "[redacted]", StringComparison.Ordinal);
                }
            }
        }

        return value;
    }

    // The title already says the request failed, so the structured detail states only the status.
    // A parsed body was read in full, so the body read failure suffix never applies here.
    private static string DescribeStatus(XboxPartnerApiException exception) =>
        $"The service returned HTTP {(int)exception.StatusCode}" +
        (string.IsNullOrWhiteSpace(exception.ReasonPhrase) ? "." : $" ({exception.ReasonPhrase}).");

    private static string AppendDetail(string detail, string? additionalDetail) =>
        string.IsNullOrWhiteSpace(additionalDetail) ? detail : $"{detail} {additionalDetail}";

    private static Dictionary<string, string> FormatValidationMessage(
        XboxLiveValidationMessage message,
        IReadOnlyList<string>? sensitiveValues)
    {
        var formatted = new Dictionary<string, string>(StringComparer.Ordinal);
        AddIfPresent("severity", message.Severity);
        AddIfPresent("documentName", message.DocumentName);
        AddIfPresent("error", message.Error);
        AddIfPresent("message", message.Message);
        return formatted;

        void AddIfPresent(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                formatted[name] = Redact(value, sensitiveValues);
            }
        }
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        TryGetProperty(element, propertyName, out JsonElement property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static Dictionary<string, string[]>? ReadFieldErrors(JsonElement root, bool allowPartial)
    {
        if (!TryGetProperty(root, "errors", out JsonElement errors) ||
            errors.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var fields = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (JsonProperty field in errors.EnumerateObject())
        {
            if ((field.Name.Length > 0 && string.IsNullOrWhiteSpace(field.Name)) ||
                field.Value.ValueKind != JsonValueKind.Array)
            {
                if (allowPartial)
                {
                    continue;
                }

                return null;
            }

            var messages = new List<string>();
            foreach (JsonElement message in field.Value.EnumerateArray())
            {
                if (message.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(message.GetString()))
                {
                    if (allowPartial)
                    {
                        continue;
                    }

                    return null;
                }

                messages.Add(message.GetString()!);
            }

            if (messages.Count == 0)
            {
                if (allowPartial)
                {
                    continue;
                }

                return null;
            }

            if (!fields.TryAdd(field.Name, messages.ToArray()))
            {
                return null;
            }
        }

        return fields.Count == 0 ? null : fields;
    }

    private static bool TryGetProperty(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
