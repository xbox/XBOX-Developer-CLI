// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

/// <summary>
/// Polls a test account creation job until it stops running or the deadline passes. XboxPartner
/// never polls the job itself, so the CLI owns the cadence.
/// </summary>
/// <remarks>
/// Kept apart from <see cref="XboxPartnerJobPoller"/>: test account jobs can run for days, so the
/// caller sets the deadline, and the last running read is returned on timeout so the user still
/// sees how far the job got. Reads are safe to repeat, so a short run of transient read failures
/// is tolerated; the create itself is never sent from here.
/// </remarks>
internal static class TestAccountJobWaiter
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MaximumInterval = TimeSpan.FromMinutes(5);
    public const int MaxConsecutiveReadFailures = 3;

    public static async Task<TestAccountJobResult> WaitAsync(
        TestAccountJobResult initial,
        Func<CancellationToken, Task<TestAccountJobResult>> read,
        TimeSpan timeout,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        Func<DateTimeOffset> getUtcNow,
        Action<TestAccountJobResult>? onProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(delayAsync);
        ArgumentNullException.ThrowIfNull(getUtcNow);

        DateTimeOffset deadline = getUtcNow() + timeout;
        TestAccountJobResult current = initial;
        TimeSpan? nextDelay = null;
        int failures = 0;
        while (current.Job.IsRunning)
        {
            TimeSpan remaining = deadline - getUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            TimeSpan delay = nextDelay ?? Clamp(current.RetryAfter ?? DefaultInterval);
            await delayAsync(delay < remaining ? delay : remaining, cancellationToken).ConfigureAwait(false);
            remaining = deadline - getUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            // The deadline bounds the read too, so a slow answer cannot stretch the wait past --timeout.
            // WaitAsync abandons a read that does not observe its token.
            using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readCancellation.CancelAfter(remaining);
            try
            {
                current = await read(readCancellation.Token)
                    .WaitAsync(readCancellation.Token)
                    .ConfigureAwait(false);
                failures = 0;
                nextDelay = null;
                onProgress?.Invoke(current);
            }
            catch (Exception) when (readCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (
                IsTransient(exception, cancellationToken) && ++failures < MaxConsecutiveReadFailures)
            {
                nextDelay = exception is XboxPartnerApiException { RetryAfter: { } retryAfter }
                    ? Clamp(retryAfter)
                    : DefaultInterval;
            }
        }

        return current;
    }

    internal static bool IsTransient(Exception exception, CancellationToken cancellationToken) =>
        exception switch
        {
            XboxPartnerApiException { StatusCode: HttpStatusCode.TooManyRequests } => true,
            // Reads are safe to repeat, so any 502 or 504 is retried: that covers XboxPartner's
            // downstream_* transport codes and the codeless answers from the network in front of it.
            XboxPartnerApiException { StatusCode: HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout } => true,
            // A 503 with a code such as feature_disabled or service_configuration_invalid does not
            // clear by waiting a few seconds; one without a code came from the network in front of XboxPartner.
            XboxPartnerApiException { StatusCode: HttpStatusCode.ServiceUnavailable } api =>
                TestAccountCommandFactory.ReadProblemString(api.ResponseBody, "code") is null or "downstream_throttled",
            // DNS and TLS failures do not clear by waiting a few seconds; other network failures, such
            // as a dropped or refused connection, may.
            HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError } => false,
            HttpRequestException or IOException => true,
            OperationCanceledException => !cancellationToken.IsCancellationRequested,
            _ => false,
        };

    internal static TimeSpan Clamp(TimeSpan value) =>
        value < MinimumInterval ? MinimumInterval : value > MaximumInterval ? MaximumInterval : value;
}
