// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace XboxDevCLI.Services;

internal static class XboxPartnerJobPoller
{
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// The shortest wait between polls. A <c>Retry-After</c> of zero or a date in the past would
    /// otherwise poll the gateway in a tight loop.
    /// </summary>
    internal static readonly TimeSpan MinimumDelay = TimeSpan.FromSeconds(1);

    internal static async Task<TJob?> PollAsync<TJob>(
        TJob initialResponse,
        Func<CancellationToken, Task<TJob>> getStatusAsync,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        CancellationToken cancellationToken,
        Func<DateTimeOffset>? getUtcNow = null,
        TimeSpan? interval = null,
        TimeSpan? timeout = null)
        where TJob : XboxPartnerCreationJob
    {
        ArgumentNullException.ThrowIfNull(initialResponse);
        ArgumentNullException.ThrowIfNull(getStatusAsync);
        ArgumentNullException.ThrowIfNull(delayAsync);

        if (initialResponse.Completed)
        {
            return initialResponse;
        }

        var pollInterval = interval ?? DefaultInterval;
        var pollTimeout = timeout ?? DefaultTimeout;
        if (pollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(interval),
                "The polling interval must be greater than zero.");
        }

        if (pollTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "The polling timeout must be greater than zero.");
        }

        getUtcNow ??= () => DateTimeOffset.UtcNow;
        var deadline = getUtcNow().Add(pollTimeout);
        var currentResponse = initialResponse;

        using var timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(pollTimeout);

        try
        {
            while (true)
            {
                var remaining = deadline - getUtcNow();
                if (remaining <= TimeSpan.Zero)
                {
                    return null;
                }

                var requestedDelay = currentResponse.RetryAfter is { } retryAfter
                    ? (retryAfter > MinimumDelay ? retryAfter : MinimumDelay)
                    : pollInterval;
                var boundedDelay = requestedDelay < remaining
                    ? requestedDelay
                    : remaining;

                await delayAsync(boundedDelay, timeoutSource.Token);
                if (getUtcNow() >= deadline)
                {
                    return null;
                }

                currentResponse = await getStatusAsync(timeoutSource.Token);
                if (currentResponse.Completed)
                {
                    return currentResponse;
                }
            }
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested
                  && timeoutSource.IsCancellationRequested)
        {
            return null;
        }
    }
}
