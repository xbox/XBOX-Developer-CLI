// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using XboxDevCLI.Presentation;
using XboxDevCLI.Services;
using XboxDevCLI.Telemetry;

namespace XboxDevCLI.Commands;

internal sealed class XboxLiveConfigurationTarget
{
    private readonly Option<Guid?> _scid;
    private readonly Option<string?> _title;

    public XboxLiveConfigurationTarget(Func<ParseResult, bool>? applies = null)
    {
        _scid = new Option<Guid?>("--scid")
        {
            Description = "Service configuration ID. Use this or --title-id; skips title context and lookup.",
        };
        _scid.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 &&
                (!Guid.TryParse(result.Tokens[0].Value, out var value) || value == Guid.Empty))
            {
                result.AddError($"--scid must be a non-empty GUID; got '{result.Tokens[0].Value}'.");
            }
        });
        _title = new Option<string?>("--title-id")
        {
            Description = "XBOX title ID: one to eight hexadecimal digits from 00000001 through 7FFFFFFF, with optional 0x prefix. Use --scid for other title IDs. Reads of a bound title use the primary title's shared configuration; writes to a bound title need the primary's --scid.",
        }.AsContextValue(ContextEndpoint.XboxPartner,
            result => (applies?.Invoke(result) ?? true) && result.GetResult(_scid) is not { Implicit: false });
        _title.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 &&
                !(XboxTitleId.TryNormalize(result.Tokens[0].Value, out var normalized) &&
                  XboxPartnerClient.IsSupportedConfigurationTitle(normalized)))
            {
                result.AddError(
                    "--title-id must be a hexadecimal title ID from 00000001 through 7FFFFFFF, with an optional 0x prefix. " +
                    "Use --scid for other titles.");
            }
        });
    }

    public void AddTo(Command command)
    {
        command.Options.Add(_scid);
        command.Options.Add(_title);
        command.Validators.Add(result =>
        {
            if (result.GetResult(_scid) is { Implicit: false } &&
                result.GetResult(_title) is { Implicit: false })
            {
                result.AddError("--scid and --title-id cannot be combined.");
            }
        });
    }

    public async Task<SelectedTarget> SelectAsync(ParseResult result, long? sellerId)
    {
        if (result.GetValue(_scid) is { } scid)
        {
            return new SelectedTarget(sellerId, scid, null);
        }

        if (result.GetValue(_title) is not { } title)
        {
            throw new InvalidOperationException(await ContextResolver.GetMissingMessageAsync(result, _title));
        }

        XboxTitleId.TryNormalize(title, out var normalized);
        return new SelectedTarget(sellerId, null, normalized);
    }

    public static void WriteResolved(
        XboxLiveResolvedConfigurationTarget target, long? selectedSeller, TextWriter stderr) =>
        stderr.WriteLine(
            "Effective configuration target: " +
            (target.SellerId is { } seller
                ? $"seller {seller.ToString(CultureInfo.InvariantCulture)}" +
                    (selectedSeller.HasValue ? "" : " (only authorized seller)")
                : "seller unknown") +
            (target.TitleId is null ? "" : $", title {target.TitleId}") +
            (target.ResolvedTitleId is not null && target.ResolvedTitleId != target.TitleId
                ? $", primary title {target.ResolvedTitleId}" : "") +
            $", SCID {target.Scid:D}");

    public const string AllowSharedConfigurationOptionName = "--allow-shared-configuration";

    public const string AllowSharedConfigurationHint =
        $"Rerun with {AllowSharedConfigurationOptionName} to change the shared configuration.";

    public static Option<bool> CreateAllowSharedConfigurationOption() => new(AllowSharedConfigurationOptionName)
    {
        Description =
            "Change configuration that other titles are bound to, and share, by --title-id without asking. " +
            "Default: false, which asks at a terminal and refuses otherwise. Writes by --scid aren't checked.",
    };

    /// <summary>
    /// Reads the service's refusal of a write to configuration other titles share, which it returns until the
    /// request sends allowSharedConfiguration. Returns null for any other error.
    /// </summary>
    public static SharedConfiguration? ReadSharedConfiguration(XboxPartnerApiException exception)
    {
        if (exception.StatusCode != System.Net.HttpStatusCode.Conflict)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(exception.ResponseBody);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                GetString(root, "code") != XboxPartnerCommandErrorFormatter.SharedConfigurationRequiresConfirmationCode ||
                !Guid.TryParse(GetString(root, "scid"), out Guid scid) || scid == Guid.Empty ||
                !root.TryGetProperty("boundTitleIds", out JsonElement bound) || bound.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var titles = new List<string>();
            foreach (JsonElement title in bound.EnumerateArray())
            {
                if (title.ValueKind != JsonValueKind.String || !XboxTitleId.TryNormalize(title.GetString()!, out var id))
                {
                    return null;
                }

                titles.Add(id);
            }

            string? owner = GetString(root, "titleId") is { } rawOwner && XboxTitleId.TryNormalize(rawOwner, out var normalized)
                ? normalized
                : null;
            return titles.Count == 0 ? null : new SharedConfiguration(scid, owner, titles);
        }
        catch (JsonException)
        {
            return null;
        }

        static string? GetString(JsonElement element, string name) =>
            element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    public static string SharedConfigurationMessage(SharedConfiguration shared, bool validateOnly = false)
    {
        const int Listed = 10;
        int count = shared.BoundTitleIds.Count;
        string titles = string.Join(", ", shared.BoundTitleIds.Take(Listed)) +
            (count > Listed ? $", and {count - Listed} more" : "");
        return $"This configuration (SCID {shared.Scid:D}" +
            (shared.TitleId is null ? "" : $", title {shared.TitleId}") +
            $") is shared by {count} bound title{(count == 1 ? "" : "s")}: {titles}. " +
            (validateOnly
                ? "Validation checks it against all of them; nothing is saved."
                : "Changes apply to all of them.");
    }

    /// <summary>
    /// Explains that the write changes configuration other titles share and asks whether to go ahead. Returns
    /// true only when the user says yes at a terminal. Without one it writes the service's error, which says how
    /// to opt in, and returns false.
    /// </summary>
    public static async Task<bool> ConfirmSharedConfigurationAsync(
        SharedConfiguration shared,
        XboxPartnerApiException refusal,
        bool compact,
        bool validateOnly,
        PublishConfirmationDependencies? dependencies,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        stderr.WriteLine(SharedConfigurationMessage(shared, validateOnly));
        if (dependencies is null || !dependencies.IsInteractive())
        {
            stderr.WriteLine(XboxPartnerCommandErrorFormatter.Format(refusal, compact));
            TelemetryRecorder.RecordError(TelemetryErrorTypes.NotConfirmed);
            return false;
        }

        ConfirmationPrompt.Write(stderr, validateOnly
            ? "Validate the shared configuration? [y/N]: "
            : "Change the shared configuration? [y/N]: ");
        string? answer;
        try
        {
            answer = (await dependencies.ReadLineAsync(cancellationToken).ConfigureAwait(false))?.Trim();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stderr.WriteLine();
            answer = null;
        }

        if (string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        stderr.WriteLine(validateOnly ? "Canceled. Nothing was validated." : "Canceled. Nothing was changed.");
        XboxPartnerCommandSupport.WriteCorrelationId(refusal.CorrelationId, stderr);
        TelemetryRecorder.RecordError(TelemetryErrorTypes.NotConfirmed);
        return false;
    }

    /// <summary>A configuration other titles are bound to: its SCID, the title that owns it, and theirs.</summary>
    internal sealed record SharedConfiguration(Guid Scid, string? TitleId, IReadOnlyList<string> BoundTitleIds);

    internal sealed record SelectedTarget(long? SellerId, Guid? Scid, string? TitleId)
    {
        public string Describe() =>
            $"seller {(SellerId?.ToString(CultureInfo.InvariantCulture) ?? "(only authorized seller)")}" +
            (TitleId is null ? "" : $", title {TitleId}") +
            (Scid.HasValue ? $", SCID {Scid:D}" : "");
    }
}
