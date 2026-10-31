// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

/// <summary>How a wait for a batch update ended.</summary>
internal enum TestAccountBatchUpdateOutcome
{
    /// <summary>The batch no longer shows a job, so the update job completed.</summary>
    Finished,

    /// <summary>The deadline passed with a job, possibly stalled, still on the batch.</summary>
    Updating,
}

/// <summary>How a wait for a batch update ended, with the last batch it read, if any.</summary>
internal readonly record struct TestAccountBatchUpdateWait(
    TestAccountBatchUpdateOutcome Outcome,
    TestAccountBatch? LastBatch);

/// <summary>
/// Polls a test account batch until its update job completes or the deadline passes.
/// </summary>
/// <remarks>
/// XboxPartner cannot read update jobs, so the batch itself is the progress signal: it shows the
/// job's ID while the job runs, and clears it only when the job completes. A job that fails for
/// good keeps its ID on the batch, so the deadline is the only way such a wait ends. Reads are
/// safe to repeat, so transient read failures are tolerated as in <see cref="TestAccountJobWaiter"/>;
/// the update itself is never sent from here.
/// </remarks>
internal static class TestAccountBatchUpdateWaiter
{
    public static async Task<TestAccountBatchUpdateWait> WaitAsync(
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
                return new(TestAccountBatchUpdateOutcome.Updating, last);
            }

            TimeSpan delay = nextDelay ?? interval;
            await delayAsync(delay < remaining ? delay : remaining, cancellationToken).ConfigureAwait(false);
            remaining = deadline - getUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return new(TestAccountBatchUpdateOutcome.Updating, last);
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
                // One read without a job could race the service recording the update job, so only
                // two in a row mean it completed.
                last = TestAccountBatchDeletionWaiter.ReadBatch(body);
                idleReads = string.IsNullOrWhiteSpace(last.JobId) ? idleReads + 1 : 0;
                if (idleReads >= TestAccountBatchDeletionWaiter.ReadsWithoutJobBeforeStopped)
                {
                    return new(TestAccountBatchUpdateOutcome.Finished, last);
                }
            }
            catch (Exception) when (readCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return new(TestAccountBatchUpdateOutcome.Updating, last);
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

    /// <summary>
    /// Names the fields the update asked to change that the batch does not show, or returns an
    /// empty list when it shows them all. Only requested fields are compared, so a value the
    /// service filled in for an unrequested field cannot cause a false mismatch. Sandboxes compare
    /// as sets ignoring case, and must also show every sandbox added and none removed, so an answer
    /// that dropped part of the request cannot confirm it. Replaced keywords compare as sets with case,
    /// since the service treats a case only change as a real one, and must also include every
    /// requested keyword with its case; added keywords need only be present ignoring case, so a
    /// tag another change added meanwhile is not a mismatch. The expiry compares by UTC day with both
    /// the answer and the request, since the service keeps only the day. A requested field the update's answer left out counts as a mismatch,
    /// since the change cannot be confirmed.
    /// </summary>
    internal static IReadOnlyList<string> FindMismatches(
        UpdateTestAccountBatchRequest request,
        TestAccountBatchUpdate expected,
        TestAccountBatch batch)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(batch);
        var mismatches = new List<string>();
        bool sandboxesRequested = request.AddSandboxes is { Count: > 0 } || request.RemoveSandboxes is { Count: > 0 };
        if (sandboxesRequested &&
            (expected.Sandboxes is not { } sandboxes || !SameSet(sandboxes, batch.SandboxAccess) ||
                !ShowsSandboxChange(request, batch.SandboxAccess)))
        {
            mismatches.Add("sandboxes");
        }

        if (request.AccessExpirationDate is { } requestedExpiry &&
            (expected.AccessExpirationDate is not { } expiry ||
                batch.AccessExpirationDate?.UtcDateTime.Date != expiry.UtcDateTime.Date ||
                batch.AccessExpirationDate?.UtcDateTime.Date != requestedExpiry.UtcDateTime.Date))
        {
            mismatches.Add("access expiration");
        }

        if (request.Keywords is { } requestedKeywords &&
            !(request.ReplaceKeywords == true
                ? expected.Keywords is { } keywords && batch.Keywords is { } actualKeywords &&
                    keywords.ToHashSet(StringComparer.Ordinal).SetEquals(actualKeywords) &&
                    requestedKeywords.ToHashSet(StringComparer.Ordinal).IsSubsetOf(actualKeywords)
                : requestedKeywords.ToHashSet(StringComparer.OrdinalIgnoreCase).IsSubsetOf(batch.Keywords ?? [])))
        {
            mismatches.Add("keywords");
        }

        return mismatches;
    }

    // Checked against the request as well as the service's answer, so an answer that dropped part
    // of the change cannot confirm it.
    private static bool ShowsSandboxChange(UpdateTestAccountBatchRequest request, IReadOnlyList<string>? actual)
    {
        if (actual is null)
        {
            return false;
        }

        var shown = actual.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (request.AddSandboxes ?? []).All(shown.Contains) && !(request.RemoveSandboxes ?? []).Any(shown.Contains);
    }

    // A batch read without the list cannot confirm the change, even when the expected set is empty.
    private static bool SameSet(IReadOnlyList<string> expected, IReadOnlyList<string>? actual) =>
        actual is not null && expected.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(actual);
}
