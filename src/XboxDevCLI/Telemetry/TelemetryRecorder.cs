// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace XboxDevCLI.Telemetry;

/// <summary>
/// What one invocation's components learned for its telemetry event: the last service status,
/// the most specific failure reason, and which saved context keys supplied a value. Never a
/// value, a message, or a path.
/// </summary>
internal sealed class InvocationTelemetry
{
    private readonly object _gate = new();
    private readonly SortedSet<string> _contextKeys = new(StringComparer.Ordinal);
    private int? _httpStatusCode;
    private string? _componentErrorType;
    private string? _explicitErrorType;

    internal int? HttpStatusCode
    {
        get { lock (_gate) { return _httpStatusCode; } }
    }

    /// <summary>
    /// The failure reason: one the invocation itself decided, such as <c>usage</c>, else the last
    /// one a component recorded, else <c>null</c>.
    /// </summary>
    internal string? ErrorType
    {
        get { lock (_gate) { return _explicitErrorType ?? _componentErrorType; } }
    }

    internal IReadOnlyList<string> ContextKeys
    {
        get { lock (_gate) { return [.. _contextKeys]; } }
    }

    internal void RecordHttpStatus(int statusCode)
    {
        lock (_gate)
        {
            _httpStatusCode = statusCode;
        }
    }

    internal void RecordError(string errorType)
    {
        lock (_gate)
        {
            _componentErrorType = errorType;
        }
    }

    internal void RecordErrorIfUnset(string errorType)
    {
        lock (_gate)
        {
            _componentErrorType ??= errorType;
        }
    }

    internal void SetError(string errorType)
    {
        lock (_gate)
        {
            _explicitErrorType = errorType;
        }
    }

    internal void RecordContextKey(string key)
    {
        lock (_gate)
        {
            _contextKeys.Add(key);
        }
    }
}

/// <summary>
/// The current invocation's <see cref="InvocationTelemetry"/>, which <c>Program.Main</c> starts
/// before anything runs. It flows to the async calls the invocation makes, so the PlayFab and
/// service clients can record what they saw without a reference threaded through every command.
/// Every method does nothing when no invocation was started, such as in a unit test.
/// </summary>
internal static class TelemetryRecorder
{
    private static readonly AsyncLocal<InvocationTelemetry?> CurrentInvocation = new();

    internal static InvocationTelemetry? Current => CurrentInvocation.Value;

    internal static InvocationTelemetry Start()
    {
        var invocation = new InvocationTelemetry();
        CurrentInvocation.Value = invocation;
        return invocation;
    }

    internal static void Stop() => CurrentInvocation.Value = null;

    internal static void RecordHttpStatus(int statusCode) =>
        CurrentInvocation.Value?.RecordHttpStatus(statusCode);

    /// <summary>
    /// Records a service response's status and, when it's a 4xx or 5xx, <c>service_rejected</c> or
    /// <c>service_error</c>, for a caller that handles the failure itself.
    /// </summary>
    internal static void RecordResponseStatus(int statusCode)
    {
        RecordHttpStatus(statusCode);
        var errorType = TelemetryInstrumentation.ClassifyStatus(statusCode);
        if (errorType != TelemetryErrorTypes.Other)
        {
            RecordError(errorType);
        }
    }

    /// <summary>Records <paramref name="errorType"/>, one of <see cref="TelemetryErrorTypes"/>, as the latest failure.</summary>
    internal static void RecordError(string errorType) =>
        CurrentInvocation.Value?.RecordError(errorType);

    /// <summary>
    /// Records what <paramref name="exception"/> says about the failure, unless it says nothing
    /// more specific than <see cref="TelemetryErrorTypes.Other"/>, which never replaces a reason a
    /// component already recorded.
    /// </summary>
    internal static void RecordError(Exception exception)
    {
        if (CurrentInvocation.Value is not { } invocation)
        {
            return;
        }

        var errorType = TelemetryInstrumentation.ClassifyException(exception);
        if (errorType != TelemetryErrorTypes.Other)
        {
            invocation.RecordError(errorType);
        }
    }

    /// <summary>Records <paramref name="errorType"/> only when no component recorded a reason.</summary>
    internal static void RecordErrorIfUnset(string errorType) =>
        CurrentInvocation.Value?.RecordErrorIfUnset(errorType);

    /// <summary>Records a reason the invocation itself decided, which wins over any a component recorded.</summary>
    internal static void SetError(string errorType) =>
        CurrentInvocation.Value?.SetError(errorType);

    /// <summary>Records that the saved context key <paramref name="key"/> supplied a value. Never the value.</summary>
    internal static void RecordContextKey(string key) =>
        CurrentInvocation.Value?.RecordContextKey(key);
}
