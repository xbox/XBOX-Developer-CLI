// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;

namespace XboxDevCLI.Services;

internal sealed partial class XboxPartnerClient
{
    internal static void ValidateConfigurationTarget(Guid? scid, string? titleId)
    {
        if (scid.HasValue == (titleId is not null))
        {
            throw new ArgumentException("Pass exactly one of --scid or --title-id.");
        }

        if (scid is { } value)
        {
            ValidateRequiredGuid(value, nameof(scid));
        }
        else if (!IsSupportedConfigurationTitle(titleId))
        {
            throw new ArgumentException(
                "Title targeting supports hexadecimal IDs from 00000001 through 7FFFFFFF. " +
                "Pass --scid explicitly for a title outside this range.", nameof(titleId));
        }
    }

    private static void ValidateDocumentTarget(string documentType, Guid? scid, string? titleId)
    {
        if (!string.Equals(documentType, "account", StringComparison.OrdinalIgnoreCase))
        {
            ValidateConfigurationTarget(scid, titleId);
        }
    }

    /// <summary>
    /// Sends a configuration operation and parses its body. A success body that can't be read or
    /// fails <paramref name="parse"/> is a <see cref="XboxPartnerResponseException"/>
    /// carrying the correlation ID, since the server may have acted on the request.
    /// <paramref name="onSending"/> gets the correlation ID the request is sent with.
    /// </summary>
    private async Task<XboxPartnerOperationResult<T>> SendConfigurationOperationAsync<T>(
        string path,
        HttpContent content,
        long? sellerId,
        Guid? scid,
        string? titleId,
        Func<string, T> parse,
        CancellationToken cancellationToken,
        Action<XboxLiveResolvedConfigurationTarget>? targetResolved,
        Action<string>? onSending = null)
    {
        string correlationId = Guid.NewGuid().ToString();
        bool sent = false;
        HttpResponseMessage sentResponse;
        try
        {
            sentResponse = await SendResponseAsync(
                HttpMethod.Post, path, content, "application/json", cancellationToken,
                correlationId, () =>
                {
                    sent = true;
                    onSending?.Invoke(correlationId);
                }).ConfigureAwait(false);
        }
        catch (Exception exception) when (sent && (exception is HttpRequestException or IOException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)))
        {
            // The request may have reached the service, so keep the ID it was sent with for tracing.
            throw new XboxPartnerResponseException(string.Empty, correlationId, exception);
        }

        using HttpResponseMessage response = sentResponse;
        correlationId = ReadCorrelationId(response) ?? correlationId;
        if (scid.HasValue || titleId is not null)
        {
            var target = ReadConfigurationTarget(response, sellerId, scid, titleId, correlationId);
            if (target is not null)
            {
                targetResolved?.Invoke(target);
            }
        }

        string body;
        try
        {
            using var bodyTimeout = CreateResponseBodyTimeout(cancellationToken);
            body = await ReadResponseBodyWithLimitAsync(response.Content, bodyTimeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException
            or HttpRequestException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new XboxPartnerResponseException(string.Empty, correlationId, exception);
        }

        try
        {
            return new XboxPartnerOperationResult<T>(parse(body), body);
        }
        catch (Exception exception) when (exception is InvalidDataException or System.Text.Json.JsonException)
        {
            throw new XboxPartnerResponseException(body, correlationId, exception);
        }
    }

    private static XboxLiveResolvedConfigurationTarget? ReadConfigurationTarget(
        HttpResponseMessage response, long? sellerId, Guid? scid, string? titleId,
        string? correlationId = null, bool requireMetadata = false)
    {
        // The service returns the seller it used. Only a titleId request also gets the SCID and the title
        // whose configuration it used, which differs from the requested title when that title is bound.
        // The metadata is display-only, except that a titleId response must name the SCID it used.
        const string prefix = "X-XboxPartner-";
        string[] names = ["Scid", "Seller-Id", "Resolved-Title-Id"];
        if (titleId is null && !requireMetadata && !names.Any(name => response.Headers.Contains(prefix + name)))
        {
            return null;
        }

        string? Header(string name) =>
            response.Headers.TryGetValues(prefix + name, out var values)
                ? values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                : null;

        long? seller = long.TryParse(Header("Seller-Id"), NumberStyles.None, CultureInfo.InvariantCulture,
            out long parsedSeller) && parsedSeller > 0 && (!sellerId.HasValue || parsedSeller == sellerId.Value)
            ? parsedSeller
            : sellerId;
        if (titleId is null)
        {
            return scid is { } requested
                ? new XboxLiveResolvedConfigurationTarget(requested, seller, null, null)
                : null;
        }

        if (!Guid.TryParse(Header("Scid"), out Guid effectiveScid) || effectiveScid == Guid.Empty)
        {
            throw new XboxPartnerResponseException(
                string.Empty, ReadCorrelationId(response) ?? correlationId,
                new InvalidDataException(
                    "The response did not name the SCID it used. Verify that the service supports title " +
                    "targeting on this operation, or pass --scid explicitly."));
        }

        string? resolved = Header("Resolved-Title-Id");
        return new XboxLiveResolvedConfigurationTarget(
            effectiveScid, seller, titleId, IsSupportedConfigurationTitle(resolved) ? resolved : null);
    }

    internal static bool IsSupportedConfigurationTitle(string? titleId) =>
        titleId is not null &&
        XboxTitleId.TryNormalize(titleId, out var normalized) && titleId == normalized &&
        uint.Parse(titleId, NumberStyles.HexNumber, CultureInfo.InvariantCulture) is > 0 and <= int.MaxValue;
}

/// <summary>A parsed operation response and the body it came from, which JSON output prints as sent.</summary>
internal sealed record XboxPartnerOperationResult<T>(T Value, string Body);

public sealed record XboxLiveResolvedConfigurationTarget(
    Guid Scid, long? SellerId, string? TitleId, string? ResolvedTitleId);
