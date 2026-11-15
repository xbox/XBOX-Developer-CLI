---
title: Test account
description: Create, update, and delete XBOX test account batches, follow their creation jobs, read batches with their accounts, and check whether an account's batch access expired.
---

# Test account

XBOX test accounts are created in batches for a Partner Center seller. These commands create,
update, and delete batches, follow the job that creates the accounts, and read batches and the
accounts in them. They can't change individual accounts, and they never return passwords. Your
account needs the Partner Center test account role for the seller.

Every command takes `--seller-id`. Without it, a command uses `XBOX_SELLER_ID` or the seller
saved with [`xbox context set`](context.md), and then your only seller; if you have several,
pass `--seller-id`.

## Commands

| Command | Description |
| --- | --- |
| `test-account batch list` | List the seller's test account batches, newest first. |
| `test-account batch get <batch-id>` | Show one batch and its accounts. |
| `test-account get <email>` | Find the batch that holds an account and report whether its access expired. See [Looking up an account](#looking-up-an-account). |
| `test-account batch create` | Create a batch of test accounts and wait for its creation job. See [Creating a batch](#creating-a-batch). |
| `test-account batch update <batch-id>` | Change a batch's sandboxes, sandbox access expiry, or tags, and wait until the batch shows them. See [Updating a batch](#updating-a-batch). |
| `test-account batch delete <batch-id>` | Delete a batch and wait until it's gone. See [Deleting a batch](#deleting-a-batch). |
| `test-account job get <job-id>` | Show the state of a creation job once. |
| `test-account job wait <job-id>` | Wait for a creation job until it stops running or the wait times out. |

`batch create`, `batch update`, and `batch delete` ask before they change anything, and are
sent once and never retried. See [Safety and destructive operations](../safety.md).

## Options for reading batches

| Option | Commands | Description |
| --- | --- | --- |
| `--seller-id <id>` | `batch list`, `batch get`, `test-account get` | Partner Center seller ID. |
| `--mine` | `list` | Only batches you created. Can't be combined with `--created-by-id`. |
| `--created-by-id <id>` | `list` | Only batches created by this numeric Partner Center user ID. Can't be combined with `--mine`. |
| `--email-prefix <text>` | `list` | Only batches whose email template starts with this text, ignoring case. |
| `--tag <tag>` | `list` | Only batches with this exact tag. Accepts one value. |
| `--state <state>` | `list` | `complete` or `pending`, in lowercase. |
| `--limit <count>` | `list` | Return at most this many of the newest matching batches. |
| `--include-sandboxes` | `list` | Also return each batch's tags (`keywords`) and `sandboxAccess`. Without `--limit`, lists at most the newest 10 matching batches; set `--limit` to list more. |
| `--expired` | `list` | Only batches whose sandbox access has expired. `--limit` then applies to the expired batches. See [Expired batches](#expired-batches). |
| `--output <format>` | `batch list`, `batch get`, `test-account get` | `json` (default), `json-compact`, or `table`. |

Tags appear as `keywords` in JSON output and in error codes such as
`test_account_keyword_invalid`. `--tag` matches a whole tag, not part of one.

If you can't read every batch for the seller, you get no error: `batch list` returns only the
batches you created.

### Expired batches

A batch is expired once its `accessExpirationDate`, the date its sandbox access ends, has
passed. A batch with no date never expires. Expiry doesn't say whether the accounts can still
sign in.

`batch list --expired` reads the seller's whole list to find the expired batches, so it's
slower for a seller with many batches. It can't be combined with `--include-sandboxes`; run
`batch get` on an expired batch to see its sandbox access.

## Response shape

`list` returns:

```json
{
  "batches": [
    {
      "id": 123456,
      "emailTemplate": "ABCDEF-smoke",
      "batchSize": 2,
      "createdBy": 7654321,
      "familyPermission": "Adult",
      "creationDate": "2026-09-12T04:00:00+00:00",
      "accessExpirationDate": "2027-03-12T04:00:00+00:00",
      "locale": "en-US",
      "keywords": null,
      "sandboxAccess": null,
      "isComplete": true,
      "jobId": null,
      "progress": null
    }
  ]
}
```

- `keywords` and `sandboxAccess` are `null` unless you pass `--include-sandboxes`. `get`
  always returns them.
- `isComplete` means the creation job stopped, not that every account was created. Compare
  the number of accounts from `get` with `batchSize`, and read `progress` for the status
  message.
- `createdBy` is a numeric Partner Center user ID.
- `jobId` is the batch's running job, and is `null` once the job has stopped. Update and delete
  jobs appear here too, but only a creation job can be followed with
  [`job wait`](#following-a-creation-job).

`get` returns the same batch object plus its accounts:

```json
{
  "batch": { "id": 123456, "batchSize": 2, "isComplete": true },
  "accounts": [
    {
      "xuid": "2814600000000001",
      "email": "ABCDEF-smoke-1@example.com",
      "gamertag": "SmokeOne",
      "isDeleted": false
    }
  ]
}
```

Each account has only `xuid`, `email`, `gamertag`, and `isDeleted`. `xuid` is a string.

## Errors when reading batches

`batch list`, `batch get`, and `test-account get` exit 1 on failure. When the service returns
a JSON error, stderr gets a JSON error with `status`, `title`, `detail`, `code` when the
response includes one, and its `correlationId`; most add a hint to `detail`. Other failures
print a line of text. Check the exit code before parsing stderr.

| Status | Meaning |
| --- | --- |
| 400 | A filter was rejected, or you have several sellers and didn't pick one; pass `--seller-id`. |
| 403 | You aren't a member of this seller, or lack the Partner Center test account role for it. For `batch get`, the batch might belong to another seller (pass `--seller-id`), or another developer created it and you can't read every batch for the seller. If the error asks you to sign in again, do that. |
| 404 | No batch with that ID exists, or test accounts aren't available yet. |
| 429 | With code `rate_limited`, you sent too many requests in a short time. The error includes `retryAfterSeconds`; see [Rate limits](../cli-for-agents.md#rate-limits). |
| 502 | The service couldn't complete the request; retry. The service can return 502 rather than 403 when you lack the test account role or, for `batch get`, when another developer created the batch. With a personal Microsoft account, retrying doesn't help; see [Known issues](../known-issues.md#some-commands-dont-work-yet-with-a-personal-microsoft-account). |
| 503 | Test accounts aren't available. |
| 504 | The service didn't answer in time; retry. |

## Looking up an account

`test-account get <email>` says whether the batch holding an account has expired. The email
must end with `@xboxtest.com`. The command searches the newest 10 batches whose email
template matches the account's name. When it doesn't find the account there, it says older
batches weren't searched and prints `batch list` and `batch get` commands to check them
yourself.

It returns the following, shortened here; `batch` holds every batch field that `batch list`
returns:

```json
{
  "account": { "xuid": "2814600000000001", "email": "ABCD-tester00007@xboxtest.com", "gamertag": "TesterSeven", "isDeleted": false },
  "batch": { "id": 123456, "emailTemplate": "ABCD-tester", "accessExpirationDate": "2026-01-01T00:00:00+00:00" },
  "expired": true,
  "expiredOn": "2026-01-01T00:00:00+00:00"
}
```

`expiredOn` is `null` when the batch hasn't expired. A deleted account is reported only when no
live copy exists, with `account.isDeleted` set. The command exits 0 whenever it finds the
account, expired or not, so scripts should read `expired`. It exits 1 when:

- The account isn't in the batches you can see. It might belong to another seller, so try
  `--seller-id`, or another developer created its batch and you can't read every batch for
  the seller.
- The email appears in more than one batch. stderr lists a `batch get` command for each.
- A request fails, as in [Errors when reading batches](#errors-when-reading-batches).

## Creating a batch

`batch create` starts a job that creates the accounts, then waits for it to stop. The accounts
count against the seller's test account limit until the batch is
[deleted](#deleting-a-batch).

In a terminal, the command shows the [security question picker](#choosing-a-security-question),
lists the values it will send, including the defaults for options you left out, and asks you to
confirm. It then [prompts](#secrets) for the password and security answer, signs in, and sends
the request. `--confirm` (`--yes`, `-y`) skips only the confirmation.

Without a terminal, pass `--password-file`, `--security-answer-file`, and `--confirm`. Without
`--confirm`, the command prints the values, refuses, and sends nothing.

| Option | Description |
| --- | --- |
| `--email-prefix <text>` | Required. The sandbox group, a hyphen, and a template, for example `ABCD-tester`. The sandbox group is the part of a sandbox ID before the dot, such as `ABCD` in `ABCD.1`. |
| `--count <count>` | Required. Number of accounts to create; at least 1. The service sets the maximum. |
| `--sandbox-id <id>` | Required. A sandbox the accounts can access. Repeat for more sandboxes. |
| `--password-file <path>` | File holding the account password, or `-` for stdin. See [Secrets](#secrets). |
| `--security-answer-file <path>` | File holding the security answer, or `-` for stdin. See [Secrets](#secrets). |
| `--child-count <count>` | Total child accounts in the batch, all linked to the first account; 0 or more. `--count 5 --child-count 2` creates 5 accounts and 2 children, not 2 for each. The service sets the maximum. |
| `--first-name <text>` | Defaults to `Test`. |
| `--last-name <text>` | Defaults to `Account`. |
| `--security-question <text>` | The security question every account gets. See [Choosing a security question](#choosing-a-security-question). |
| `--alternate-email <text>` | Defaults to your email. |
| `--access-expiration <date>` | When sandbox access expires, such as `2026-12-31`, read as UTC. Defaults to three months from now. |
| `--tag <tag>` | A tag to store on the batch, which `batch list --tag` can find later. Repeat for more tags. |
| `--locale <locale>` | Defaults to `en-US`. |
| `--subscription <name>` | The accounts' tier for XBOX services, `Silver` or `Gold`, ignoring case. Defaults to `Gold`. |
| `--seller-id <id>` | Seller to create the accounts for. |
| `--confirm`, `--yes`, `-y` | Create without the confirmation prompt. |
| `--no-wait` | Return as soon as the service accepts the job. Can't be combined with `--timeout`. |
| `--timeout <duration>` | How long to wait for the job, such as `90s`, `10m`, `2h`, or `1d`. Defaults to `5m`; at most `7d`. |
| `--output <format>` | `json` (default), `json-compact`, or `table`. |

### Choosing a security question

In a terminal, when you pass neither `--security-question` nor `--security-answer-file`, the
command shows a numbered picker of these questions. Enter selects the first. To choose one
without the picker, pass its text with `--security-question`:

1. What is your favorite hobby?
2. Who is your favorite athlete?
3. What is your maternal grandmother's first name?
4. What was your childhood best friend's first name?
5. What was the name of your first pet?
6. What is your current best friend's first name?
7. What was your favorite city to visit as a child?

Without a terminal, or with an answer file and no question, the question is
`What is your favorite hobby?`. Ctrl+C at the picker cancels without creating anything.
Choosing a question doesn't confirm the create.

### Secrets

The password and security answer are never accepted as option values or environment
variables, so they stay out of shell history, process listings, and logs. Each comes from:

- Its `--password-file` or `--security-answer-file`. A single trailing line break is removed.
- `-` in place of a path, which reads it from stdin. Only one of the two can read stdin.
- With no file option, a prompt in the terminal that doesn't echo what you type.

The password must be 8 to 16 characters and contain at least three of: lowercase letters,
uppercase letters, numbers, and symbols. Underscore doesn't count as a symbol. The password
can't match the security answer.

### Waiting and exit codes

While it waits, the command prints progress to stderr. It writes the final job to stdout, in
the same shape as [`job get`](#following-a-creation-job).

| Exit code | Meaning |
| --- | --- |
| 0 | The job succeeded, or was accepted with `--no-wait`. stderr gives the command to follow it. |
| 1 | Nothing was created: the request was refused or rejected, failed or was canceled before it was sent, or got a 503 with `feature_disabled` or `service_configuration_invalid`. |
| 2 | The job failed. Some accounts might have been created; stderr gives the counts and the command to inspect the batch. |
| 3 | The outcome is unknown. See [When the outcome is unknown](#when-the-outcome-is-unknown). |
| 4 | The batch exists, but the wait ended before the job stopped: `--timeout` passed, you pressed Ctrl+C, or reading the job failed. The job keeps running. stderr gives the command to resume waiting. Don't create again. |

### When the outcome is unknown

When the CLI can't tell whether the service created a batch, such as when the connection drops
after the request is sent or the service returns a `5xx` error, it exits 3. It never sends the
create again. stderr prints the time the request started, in UTC, and these steps:

1. Wait a few minutes; a batch still being created might not be listed yet.
2. Run `xbox test-account batch list --mine --email-prefix <prefix>` and look for a batch with
   the same email prefix and template created at or after that time.
3. If that batch's `jobId` is set, follow it with
   `xbox test-account job wait <job-id> --batch-id <batch-id>`. If `jobId` is `null`, the job has
   stopped; inspect it with `xbox test-account batch get <batch-id>`.
4. Create again only if no such batch exists.

The printed commands keep the seller, profile, and sign-in options you used, so you can copy
them as they are.

### Errors when creating

Failures write a JSON error to stderr with `status`, `title`, `detail`, and `code` when the
response includes one. Branch on `code`, not on `detail`.

| Code | Meaning |
| --- | --- |
| `test_account_sandbox_access_denied` | You can't grant access to one of the `--sandbox-id` values. If every sandbox is refused, your account might not be linked to the seller, the seller might have no sandboxes, or Partner Center might not let you manage test accounts. |
| `test_account_count_invalid` | `--count` is outside what the service allows. |
| `test_account_child_count_invalid` | `--child-count` is outside what the service allows. |
| `test_account_email_prefix_invalid` | `--email-prefix` isn't a sandbox group, a hyphen, and a template, such as `ABCD-tester`. |
| `test_account_name_invalid` | `--first-name` or `--last-name` was rejected. |
| `test_account_password_invalid` | The password doesn't meet the password rules. |
| `test_account_security_question_invalid` | `--security-question` was rejected. |
| `test_account_security_answer_invalid` | The security answer was rejected, for example because it equals the password or appears in the security question. |
| `test_account_alternate_email_invalid` | `--alternate-email` was rejected. |
| `test_account_keyword_invalid` | A `--tag` value was rejected, or there are too many. |
| `test_account_locale_invalid` | `--locale` isn't supported; try `en-US`. |
| `test_account_subscription_unavailable` | That `--subscription` isn't available; try `--subscription Silver`. |
| `test_account_limit_exceeded` | The seller has reached its test account limit. |
| `test_account_access_denied` | You lack the Partner Center role to create test accounts for this seller. |
| `test_account_create_outcome_unknown` | Exit code 3; see [When the outcome is unknown](#when-the-outcome-is-unknown). |
| `downstream_request_rejected` | The request was rejected without naming a field, for example because the `--email-prefix` group isn't one of your sandbox groups, or the security question or answer is too long. |
| `downstream_throttled` | Requests are being throttled and nothing was created. Wait, then try again. |
| `rate_limited` | 429: you sent too many requests in a short time and nothing was created. See [Rate limits](../cli-for-agents.md#rate-limits). |
| Any other code, or none, on a 5xx | Exit code 3, because a batch might exist; see [When the outcome is unknown](#when-the-outcome-is-unknown). |
| `feature_disabled`, `service_configuration_invalid` | 503: test account creation isn't available. Exit code 1. |
| `invalid_request`, or a `400` with no code | The request was malformed or a value isn't allowed; `detail` or `errors` names the field. A CLI newer than the service can get this for an option the service doesn't support yet. |

## Following a creation job

`batch create` prints the batch and job IDs as soon as the job is accepted. A job can run for
days, so you can stop waiting at any time and follow it later:

| Option | Commands | Description |
| --- | --- | --- |
| `<job-id>` | `get`, `wait` | Required. The job ID from `batch create`, or the `jobId` of a batch from `batch list`. |
| `--batch-id <id>` | `get`, `wait` | Required. The batch the job belongs to. |
| `--seller-id <id>` | `get`, `wait` | Seller that owns the batch. |
| `--timeout <duration>` | `wait` | How long to wait, such as `90s`, `10m`, `2h`, or `1d`. Defaults to `5m`; at most `7d`. |
| `--output <format>` | `get`, `wait` | `json` (default), `json-compact`, or `table`. |

Both return the job:

```json
{
  "batchId": 123456,
  "jobId": "0f8e5b2a-1c3d-4e5f-8a9b-0c1d2e3f4a5b",
  "state": "running",
  "total": 5,
  "succeeded": 3,
  "failed": 0,
  "errorCode": null,
  "errorMessage": null,
  "progress": "Creating accounts"
}
```

`state` is `running`, `succeeded`, or `failed`. `errorCode` is `test_account_job_failed` when the
job failed. `job wait` retries temporary read failures, such as throttling or a dropped
connection.

| Exit code | `job get` | `job wait` |
| --- | --- | --- |
| 0 | Running or succeeded | Succeeded |
| 1 | The request failed | The first read failed, for example because of a wrong job or batch ID; nothing is written to stdout |
| 2 | Failed | Failed |
| 4 | Not used | The wait ended before the job stopped: `--timeout` passed, you pressed Ctrl+C, or reading the job failed. The job keeps running. stderr gives the command to resume waiting; run it once the error on stderr is fixed or has cleared. |

### Errors when following a job

These error codes point at the job ID, `--batch-id`, or `--seller-id`:

| `code` | Meaning |
| --- | --- |
| `test_account_job_not_in_batch` | 404: the job isn't in the batch that `--batch-id` names. |
| `test_account_batch_not_found` | 404: no such batch for this seller. |
| `downstream_request_rejected` | 404: no job with that ID exists. |
| `test_account_access_denied` | 403: the batch or job belongs to another seller, so `--batch-id` or `--seller-id` might be wrong. Otherwise, another developer started the job and you can't read every batch for the seller. |

## Updating a batch

`batch update` changes every account in a batch: it can give them access to more sandboxes,
take access away, move when that access expires, and add to or replace the batch's tags. It
then waits until the batch shows the change.

In a terminal, the command first shows each requested change next to the batch's current
value, warns when a job is still running on the batch, and asks. Pass `--confirm` (`--yes`,
`-y`) to skip the prompt. Without a terminal and without `--confirm`, it prints the changes,
refuses, and sends nothing.

| Option | Description |
| --- | --- |
| `<batch-id>` | Required. The batch ID, from `batch list` or `batch create`. |
| `--add-sandbox-id <id>` | Sandbox to give the accounts access to. Repeat for more sandboxes. |
| `--remove-sandbox-id <id>` | Sandbox to take the accounts' access away from. Repeat for more sandboxes. A sandbox can't be both added and removed. |
| `--access-expiration <date>` | New date the accounts' sandbox access expires, such as `2026-12-31`. It must be in the future, and the service keeps only the day. The service refuses this for a batch with no sandboxes. |
| `--tag <tag>` | Tag to add to the batch, at most 100 characters. A tag the batch already has, ignoring case, is skipped. Repeat for more tags. Can't be combined with `--clear-tags`. |
| `--replace-tags` | Replace every tag on the batch with the `--tag` values. Needs `--tag`. Case matters here, so replacing `smoke` with `Smoke` is a change. |
| `--clear-tags` | Remove every tag from the batch. |
| `--allow-not-mine` | Allow changing a batch another developer created. Without it, the update is refused with `test_account_batch_not_owned`. Your permissions might still refuse it. |
| `--seller-id <id>` | Seller that owns the batch. |
| `--confirm`, `--yes`, `-y` | Update without the confirmation prompt. |
| `--no-wait` | Return as soon as the service accepts the update. Can't be combined with `--timeout`. |
| `--timeout <duration>` | How long to wait for the update, such as `90s`, `10m`, or `2h`. Defaults to `5m`; at most `7d`. |
| `--output <format>` | `json` (default), `json-compact`, or `table`. |

Pass at least one change. A batch can have at most 20 tags. `--replace-tags` and
`--clear-tags` keep the tag that records which tool created the batch, which doesn't count
toward the limit. Changing sandboxes or the expiry needs access to those sandboxes;
extending the expiry needs access to every sandbox on the batch.

The command returns the batch's values once the update completes:

```json
{
  "batchId": 123456,
  "jobId": "0f8e5b2a-1c3d-4e5f-8a9b-0c1d2e3f4a5b",
  "state": "updated",
  "sandboxes": ["ABCD.1", "ABCD.2"],
  "accessExpirationDate": "2026-12-31T00:00:00+00:00",
  "keywords": ["smoke"]
}
```

`state` is `updated` once the batch shows the change, or `updating` with `--no-wait` or when
the wait ends first. When the batch already has every requested value, nothing changes:
`state` is `unchanged`, `jobId` is `null`, and the command exits 0. Update jobs can't be
followed with `job get` or `job wait`.

### Waiting and exit codes when updating

| Exit code | Meaning |
| --- | --- |
| 0 | The batch shows the change, it already had these values, or the update was accepted with `--no-wait`. |
| 1 | Nothing was changed: the request was refused or rejected, failed before it was sent, the batch couldn't be read before the prompt, or you declined. |
| 2 | The update job ended, but the batch doesn't show the change. stderr names the fields that differ. Read the batch with `batch get`, then update it again. |
| 3 | The outcome is unknown. Read the batch with the printed `batch get` command before updating again. Once no job is running, sending the same change again is safe. |
| 4 | The update was accepted, but the wait ended while it was still running: `--timeout` passed, you pressed Ctrl+C, or reading the batch failed. A `jobId` that never clears suggests the job failed or stalled. |

### Errors when updating

| `code` | Meaning |
| --- | --- |
| `test_account_batch_not_owned` | 403: another developer created the batch. Pass `--allow-not-mine` to lift this check. |
| `test_account_sandbox_access_denied` | 403: you don't have access to a sandbox in the change. Extending the expiry needs access to every sandbox on the batch. |
| `test_account_batch_not_found` | 404: no such batch for this seller. |
| `test_account_batch_busy` | 409: a job is running on the batch. Wait until `batch get` shows `jobId` as `null`. |
| `test_account_keyword_invalid` | 400: a `--tag` value isn't allowed. |
| `test_account_access_denied` | You lack the Partner Center role to manage test accounts for this seller, or, with `--allow-not-mine`, to change a batch you didn't create. |
| `downstream_throttled` | Requests are being throttled. Wait, then try again. |
| `rate_limited` | 429: you sent too many requests in a short time. See [Rate limits](../cli-for-agents.md#rate-limits). |
| `test_account_update_outcome_unknown` | 502: exit code 3. |
| `feature_disabled`, `service_configuration_invalid` | 503: test accounts aren't available. Exit code 1. |

A `404` or `405` with no code means updating batches isn't available.

## Deleting a batch

`batch delete` deletes a whole batch, then waits until it's gone. Every account in the batch
loses its sandbox access, and its test account record is deleted. The accounts' Microsoft
accounts and XBOX profiles aren't closed.

In a terminal, the command first shows the batch's email template, size, and creator, warns
when a job is still running on it, and asks. Pass `--confirm` (`--yes`, `-y`) to skip the
prompt. Without a terminal and without `--confirm`, it prints what it would delete, refuses,
and sends nothing. There's no bulk delete; delete batches one at a time.

| Option | Description |
| --- | --- |
| `<batch-id>` | Required. The batch ID, from `batch list` or `batch create`. |
| `--allow-not-mine` | Allow deleting a batch another developer created. Without it, the delete is refused with `test_account_batch_not_owned`. Your permissions might still refuse it, with `test_account_access_denied`. |
| `--seller-id <id>` | Seller that owns the batch. |
| `--confirm`, `--yes`, `-y` | Delete without the confirmation prompt. |
| `--no-wait` | Return as soon as the service accepts the delete. Can't be combined with `--timeout`. |
| `--timeout <duration>` | How long to wait for the batch to be gone, such as `90s`, `10m`, or `2h`. Defaults to `5m`; at most `7d`. |
| `--output <format>` | `json` (default), `json-compact`, or `table`. |

The command returns:

```json
{
  "batchId": 123456,
  "jobId": "0f8e5b2a-1c3d-4e5f-8a9b-0c1d2e3f4a5b",
  "state": "deleted",
  "emailTemplate": "ABCD-tester",
  "batchSize": 5
}
```

`state` is `deleted` once the batch is gone, or `deleting` with `--no-wait` or when the wait
ends first. Delete jobs can't be followed with `job get` or `job wait`; while the delete runs,
the batch shows its `jobId`.

### Waiting and exit codes when deleting

| Exit code | Meaning |
| --- | --- |
| 0 | The batch is gone, or the delete was accepted with `--no-wait`. |
| 1 | Nothing was deleted: the request was refused or rejected, failed before it was sent, the batch couldn't be read before the prompt, or you declined. |
| 2 | The delete job stopped, but the batch still exists. Inspect it with `batch get`. |
| 3 | The outcome is unknown. Read the batch with the printed `batch get` command: a `404` with `test_account_batch_not_found` means it was deleted, and a set `jobId` means the delete is likely running. If `jobId` is `null`, wait a minute and read it again; delete again only if it's still `null`. |
| 4 | The delete was accepted, but the wait ended while the batch still existed: `--timeout` passed, you pressed Ctrl+C, or reading the batch failed. A `jobId` that stays set with `progress` unchanged suggests the delete stalled. |

### Errors when deleting

| `code` | Meaning |
| --- | --- |
| `test_account_batch_not_owned` | 403: another developer created the batch; the message names their Partner Center user ID. Pass `--allow-not-mine` to lift this check. |
| `test_account_batch_not_found` | 404: no such batch for this seller, or it was already deleted. Only this code means the batch is gone. |
| `test_account_batch_busy` | 409: a job is running on the batch, which might be a delete already in progress. Wait until `batch get` shows `jobId` as `null`, or returns `404` with `test_account_batch_not_found`. |
| `test_account_access_denied` | You lack the Partner Center role to manage test accounts for this seller, or, with `--allow-not-mine`, to delete a batch you didn't create. |
| `downstream_throttled` | Requests are being throttled. Wait, then try again. |
| `rate_limited` | 429: you sent too many requests in a short time. See [Rate limits](../cli-for-agents.md#rate-limits). |
| `test_account_delete_outcome_unknown` | 502 or 504: exit code 3. A 504 with no code is treated the same. |
| `feature_disabled`, `service_configuration_invalid` | 503: test accounts aren't available. Exit code 1. |

A `404` or `405` with no code means deleting batches isn't available.

## Table output

Every command accepts `--output table`. Times are shown in UTC, and summary lines go to
stderr, so stdout holds only the table.

| Command | Columns |
| --- | --- |
| `test-account batch list` | `BATCH ID`, `EMAIL TEMPLATE`, `SIZE`, `STATE` (`complete` or `pending`), `CREATED BY`, `CREATED (UTC)`, `EXPIRES (UTC)`, then `SANDBOXES` with `--include-sandboxes`. |
| `test-account batch get` | One row per account: `EMAIL`, `GAMERTAG`, `XUID`, and `DELETED`. stderr summarizes the batch and its sandbox access. |
| `test-account get` | One row: `EMAIL`, `GAMERTAG`, `BATCH ID`, `EMAIL TEMPLATE`, `EXPIRES (UTC)`, and `EXPIRED` (`yes` or `no`). |
| `test-account batch create`, `job get`, `job wait` | One row: `BATCH ID`, `JOB ID`, `STATE` (`running`, `succeeded`, or `failed`), `TOTAL`, `SUCCEEDED`, `FAILED`. |
| `test-account batch update` | One row: `BATCH ID`, `JOB ID`, `STATE` (`updated`, `updating`, or `unchanged`), `EXPIRES (UTC)`. stderr lists the sandboxes and keywords. |
| `test-account batch delete` | One row: `BATCH ID`, `JOB ID`, `STATE` (`deleted` or `deleting`), `EMAIL TEMPLATE`, `SIZE`. |

## Examples

These commands list and read batches, accounts, and jobs:

```bash
xbox test-account batch list --mine --limit 5

xbox test-account batch list --state pending --output table

xbox test-account batch list --email-prefix ABCDEF-smoke --include-sandboxes

xbox test-account batch get 123456 --output table

xbox test-account batch list --expired --limit 10

xbox test-account get ABCD-tester00007@xboxtest.com --output table

xbox test-account batch get 123456 --seller-id 1234567 --output json-compact

xbox test-account job get 0f8e5b2a-1c3d-4e5f-8a9b-0c1d2e3f4a5b --batch-id 123456 --output table
```

Create a batch of five accounts. The command prompts for the password and security answer,
then asks you to confirm:

```bash
xbox test-account batch create --email-prefix ABCD-tester --count 5 --sandbox-id ABCD.1
```

Create a batch unattended, with the secrets read from files, no prompt, and a wait of up to
30 minutes:

```bash
xbox test-account batch create --email-prefix ABCD-smoke --count 2 --sandbox-id ABCD.1 \
  --password-file ./password.txt --security-answer-file ./answer.txt \
  --subscription Silver --tag smoke --timeout 30m --confirm
```

Wait up to two hours for a create job to finish:

```bash
xbox test-account job wait 0f8e5b2a-1c3d-4e5f-8a9b-0c1d2e3f4a5b --batch-id 123456 --timeout 2h
```

Add a sandbox, and set when sandbox access expires. The command shows the changes, asks you
to confirm, then waits until the batch shows them:

```bash
xbox test-account batch update 123456 --add-sandbox-id ABCD.2 --access-expiration 2026-12-31
```

Add two tags unattended, without waiting:

```bash
xbox test-account batch update 123456 --tag smoke --tag nightly --confirm --no-wait
```

Replace every tag with one:

```bash
xbox test-account batch update 123456 --tag nightly --replace-tags --confirm
```

Delete a batch. The command shows the batch, asks you to confirm, then waits until it's gone:

```bash
xbox test-account batch delete 123456
```

Delete a batch another developer created, unattended and without waiting:

```bash
xbox test-account batch delete 123456 --allow-not-mine --confirm --no-wait
```
