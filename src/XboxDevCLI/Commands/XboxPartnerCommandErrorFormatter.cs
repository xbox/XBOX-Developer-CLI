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
        string fallbackTitle = "XboxPartner request failed",
        string? fallbackDetail = null)
    {
        string failureMessage = fallbackDetail ?? exception.Message;
        try
        {
            using var document = JsonDocument.Parse(exception.ResponseBody);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Redact(AppendDetail(failureMessage, additionalDetail), sensitiveValues);
            }

            JsonElement root = document.RootElement;
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
                    ? failureMessage
                    : GetString(root, "detail") ?? failureMessage,
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
        catch (Exception parseException) when (parseException is JsonException or ArgumentException)
        {
            return Redact(AppendDetail(failureMessage, additionalDetail), sensitiveValues);
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
