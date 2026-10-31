// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

/// <summary>How a wait for a batch delete ended.</summary>
internal enum TestAccountBatchDeletionOutcome
{
    /// <summary>The batch read answered 404 <c>test_account_batch_not_found</c>.</summary>
    Deleted,

    /// <summary>The batch still exists and no job is running on it, so the delete stopped without removing it.</summary>
    Stopped,

    /// <summary>The deadline passed with the batch still there and a job, possibly stalled, still on it.</summary>
    Deleting,
}

/// <summary>How a wait for a batch delete ended, with the last batch it read, if any.</summary>
internal readonly record struct TestAccountBatchDeletionWait(
    TestAccountBatchDeletionOutcome Outcome,
    TestAccountBatch? LastBatch);

/// <summary>
/// Polls a test account batch until a delete removes it, stops, or the deadline passes.
/// </summary>
/// <remarks>
/// XboxPartner cannot read delete jobs, so the batch itself is the progress signal: it shows the
/// delete's job ID while the job runs, and reads answer 404 <c>test_account_batch_not_found</c> once
/// it is gone. Reads are safe to repeat, so transient read failures are tolerated as in
/// <see cref="TestAccountJobWaiter"/>; the delete itself is never sent from here.
/// </remarks>
internal static class TestAccountBatchDeletionWaiter
{
    internal const string BatchNotFoundCode = "test_account_batch_not_found";

    internal const int ReadsWithoutJobBeforeStopped = 2;

    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNameCaseInsensitive = true };

    public static async Task<TestAccountBatchDeletionWait> WaitAsync(
        TimeSpan? retryAfter,
        Func<CancellationToken, Task<string>> readBatch,
        TimeSpan timeout,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        Func<DateTimeOffset> getUtcNow,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readBatch);
        ArgumentNullException.ThrowIfNull(delayAsync);
        ArgumentNullException.ThrowIfNull(getUtcNow);

        DateTimeOffset deadline = getUtcNow() + timeout;
        TimeSpan interval = TestAccountJobWaiter.Clamp(retryAfter ?? TestAccountJobWaiter.DefaultInterval);
        TimeSpan? nextDelay = null;
        int failures = 0;
        int idleReads = 0;
        TestAccountBatch? last = null;
        while (true)
        {
            TimeSpan remaining = deadline - getUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return new(TestAccountBatchDeletionOutcome.Deleting, last);
            }

            TimeSpan delay = nextDelay ?? interval;
            await delayAsync(delay < remaining ? delay : remaining, cancellationToken).ConfigureAwait(false);
            remaining = deadline - getUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return new(TestAccountBatchDeletionOutcome.Deleting, last);
            }

            // The deadline bounds the read too, so a slow answer cannot stretch the wait past --timeout.
            using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readCancellation.CancelAfter(remaining);
            try
            {
                string body = await readBatch(readCancellation.Token)
                    .WaitAsync(readCancellation.Token)
                    .ConfigureAwait(false);
                failures = 0;
                nextDelay = null;
                // One read without a job could race the service recording the delete job, or the
                // batch disappearing just after the job ends, so only two in a row mean it stopped.
                last = ReadBatch(body);
                idleReads = string.IsNullOrWhiteSpace(last.JobId) ? idleReads + 1 : 0;
                if (idleReads >= ReadsWithoutJobBeforeStopped)
                {
                    return new(TestAccountBatchDeletionOutcome.Stopped, last);
                }
            }
            catch (XboxPartnerApiException exception) when (IsGone(exception))
            {
                return new(TestAccountBatchDeletionOutcome.Deleted, null);
            }
            catch (Exception) when (readCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return new(TestAccountBatchDeletionOutcome.Deleting, last);
            }
            catch (Exception exception) when (
                TestAccountJobWaiter.IsTransient(exception, cancellationToken) &&
                ++failures < TestAccountJobWaiter.MaxConsecutiveReadFailures)
            {
                nextDelay = exception is XboxPartnerApiException { RetryAfter: { } after }
                    ? TestAccountJobWaiter.Clamp(after)
                    : interval;
            }
        }
    }

    /// <summary>True when a batch read says the batch no longer exists.</summary>
    internal static bool IsGone(XboxPartnerApiException exception) =>
        exception.StatusCode == HttpStatusCode.NotFound &&
        TestAccountCommandFactory.ReadProblemString(exception.ResponseBody, "code") == BatchNotFoundCode;

    /// <summary>Reads the batch from a batch response, or throws <see cref="InvalidDataException"/>.</summary>
    internal static TestAccountBatch ReadBatch(string body)
    {
        TestAccountBatchResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<TestAccountBatchResponse>(body, SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The test account batch response was not valid JSON.", exception);
        }

        return response?.Batch
            ?? throw new InvalidDataException("The test account batch response did not contain a batch.");
    }
}
