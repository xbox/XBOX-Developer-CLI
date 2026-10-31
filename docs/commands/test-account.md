---
title: Test account
description: Create, update, and delete XBOX test account batches, follow their creation jobs, read batches with their accounts, and check whether an account's batch access expired.
---

# Test account

XBOX test accounts are created in batches for a Partner Center seller. These commands create,
update, and delete batches, follow the job that creates the accounts, and read batches and the accounts
in them. They never change individual accounts, and they never return passwords.

`test-account` is a root command rather than part of `xbl` for the same reason
[`sandbox`](sandbox.md) is: test accounts belong to the Partner Center seller, not to one
service, and are used across Store, XBOX Live, and sandbox testing.

## Commands

| Command | Description |
| --- | --- |
| `test-account batch list` | List the seller's test account batches, newest first. Read only. |
| `test-account batch get <batch-id>` | Show one batch and its accounts. Read only. |
| `test-account get <email>` | Find the batch that holds an account and report whether its access expired. Read only. See [Looking up an account](#looking-up-an-account). |
| `test-account batch create` | Create a batch of test accounts and wait for its creation job. **Creates accounts in production.** See [Creating a batch](#creating-a-batch). |
| `test-account batch update <batch-id>` | Change a batch's sandboxes, sandbox access expiry, or tags and wait until the batch shows them. **Changes account access in production.** See [Updating a batch](#updating-a-batch). |
| `test-account batch delete <batch-id>` | Delete a batch and wait until it is gone. **Removes accounts in production and cannot be undone.** See [Deleting a batch](#deleting-a-batch). |
| `test-account job get <job-id>` | Show the state of a creation job once. Read only. |
| `test-account job wait <job-id>` | Poll a creation job until it stops running or the wait times out. Read only. |

## Options for reading batches

| Option | Commands | Description |
| --- | --- | --- |
| `--seller-id <id>` | `batch list`, `batch get`, `test-account get` | Optional positive numeric XBOX seller account ID. Must be authorized for the signed in developer. Takes precedence over `XBOX_SELLER_ID` and the saved seller. |
| `--mine` | `list` | Only batches created by the signed in developer. Cannot be combined with `--created-by-id`. |
| `--created-by-id <id>` | `list` | Only batches created by this positive numeric Partner Center user ID. Cannot be combined with `--mine`. |
| `--email-prefix <text>` | `list` | Only batches whose email template starts with this text, ignoring case. Must not be blank. |
| `--tag <tag>` | `list` | Only batches with this exact tag. Accepts one value, which must not be blank. |
| `--state <state>` | `list` | `complete` or `pending`, in lowercase. |
| `--limit <count>` | `list` | Return at most this many of the newest matching batches. Must be positive. |
| `--include-sandboxes` | `list` | Also return each batch's tags (`keywords`) and `sandboxAccess`. Without `--limit`, lists at most the newest 10 matching batches. |
| `--expired` | `list` | Only batches whose `accessExpirationDate` has passed. `--limit` then applies to the expired batches. See [Expired batches](#expired-batches). |
| `--output <format>` | `batch list`, `batch get`, `test-account get` | `json` (default), `json-compact`, or `table`. |

With no `--seller-id`, the CLI uses `XBOX_SELLER_ID`, then the seller saved by
[`xbox context set seller-id <id>`](context.md) in the selected profile. With neither, the service
uses the developer's only authorized seller; when several are available, pass `--seller-id` or
save one with `xbox context set seller-id <id>`.

Tags are stored on the batch as `keywords`, the name used in JSON output and error codes such
as `test_account_keyword_invalid`. `--tag` is the only filter applied when the service reads the
seller's batches; it matches a whole tag, not a substring of the batch name.

`--include-sandboxes` reads each listed batch's details separately, one call to the test account
service per batch, and that service allows a developer 60 calls in 15 seconds. So without
`--limit`, the command lists at most the newest 10 matching batches and says so on stderr; set
`--limit` to list more. Table output shows the sandboxes as a `SANDBOXES` column; the tags appear
only in JSON output.

`list` reads the batches a page at a time, following every page, and prints them all as one
list. If a request fails, even on a later page, the CLI prints nothing on stdout and doesn't
retry. When a request returned an error or a page the CLI can't read, stderr also gets the
`x-ms-correlation-id` shared by every page of the listing.

`--data-only` is accepted as a global option but has no effect here: the response
carries no PlayFab response envelope.

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

* `keywords` and `sandboxAccess` are `null` in the list unless `--include-sandboxes` is set.
  `get` reads them from the batch detail.
* `isComplete` means the creation job stopped. It does not mean every account was created;
  compare the number of accounts from `get` with `batchSize`, and read `progress` for the
  status message.
* `createdBy` is a numeric Partner Center user ID. Display names are not returned.
* `emailTemplate` joins the batch's email prefix and template with `-`.
* `jobId` is the batch's running job, and is `null` once the job has stopped. Update and delete
  jobs are also reported here, so only a creation job can be followed with
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

Each account has only `xuid`, `email`, `gamertag`, and `isDeleted`. `xuid` is a string so its
full precision survives every JSON parser.

## Errors when reading batches

`batch list` and `batch get` exit with code 1 on every failure and write to stderr. When a request returns a JSON error,
stderr gets a sanitized JSON error with `status`, `title`, `detail`, and the error
`code` when the response includes one. For most statuses
below, `detail` ends with a hint about what to check. A 400 gets none, and neither does a 403
that already asks you to sign in again. When a request returns an error, or `batch list` gets a
response it can't read, stderr starts with an `x-ms-correlation-id: <id>` line before the error.

Other failures print plain text, not JSON: an error response whose body is not a JSON object
(such as an HTML page from a proxy) prints its message and the hint, and the remaining
failures (sign in, network, seller lookup, or a response that is not valid JSON) print a
`Request failed: <message>` line. Scripts should check the exit code before parsing stderr.

| Status | Meaning |
| --- | --- |
| 400 | A filter was rejected, or no seller was selected and the developer has several; pass `--seller-id`. |
| 403 | The signed in developer is not a member of this seller or lacks the Partner Center test account role for it; for `batch get`, the batch belongs to a different seller (pass `--seller-id`), or another developer created it and the developer lacks seller wide test account read permission; for `test-account get`, `--seller-id` helps only when the account belongs to another seller; or the sign in needs to be refreshed, as the error detail says. |
| 404 | No batch with that ID exists, or test accounts aren't available yet. |
| 502 | The service could not complete the request; retry first. Signing in again does not help. Some service versions also return 502, rather than 403, when the developer lacks the test account role or, for `batch get`, when another developer created the batch. |
| 503 | Test accounts are disabled or not configured for this service. |
| 504 | The service did not answer in time; retry. |

A developer without seller wide test account read permission gets no error; only
the batches that developer created are returned.

### Expired batches

The only expiry the test account service reports is a batch's `accessExpirationDate`, the date its
sandbox access ends. `--expired` and `test-account get` call a batch expired when that date is before
the current UTC time, and a batch with no date is never expired. The CLI computes this itself,
because the service's expired status is not returned. It is not a statement about
whether the accounts can still sign in.

`batch list --expired` does not send `--limit` with the request. It reads every page of the list,
keeps the expired batches, and then applies `--limit`, so the limit means the newest N expired
batches. Because no limit is sent, a large seller costs the full list. `--expired` can't
be combined with `--include-sandboxes`, which reads each batch's details; run `batch get` on an
expired batch to see its sandbox access. A batch whose `accessExpirationDate` is present but not a
date makes the command fail rather than being skipped.

## Looking up an account

`test-account get <email>` answers whether the batch holding an account has expired. The email must end
with `@xboxtest.com`, and the part before it may contain only letters, digits, `_`, `-` and single
dots, must start with a letter or digit, must not end with a dot, and may be at most 64
characters; otherwise the command is a parse error and sends nothing.

Accounts are named `{prefix}-{template}{5 digit index}@xboxtest.com`, and a batch's
`emailTemplate` is usually `{prefix}-{template}`, though some batches store it as `-{template}`
without the prefix. The command lists all the seller's batches and keeps those whose
`emailTemplate` equals the local part with or without its trailing five digit index, each with or
without the prefix. For `ABCD-tester00007@xboxtest.com`, that is `ABCD-tester`, `ABCD-tester00007`,
`-tester` or `-tester00007` (case insensitive). It reads the matching batches in the order the
service lists them (newest first) and checks each one for the exact email. It doesn't filter the
list by `emailPrefix`, because that filter misses the batches without a prefix.

At most 10 batches are read. When more matched, stderr says so, and when the account isn't found
in the batches read, it says the older ones weren't searched and prints a `batch list` command
and a `batch get <batch-id>` command for checking them yourself. When the account is in more than one
batch, the command exits 1 and prints a `batch get` command for each. A deleted account is
reported only when no live copy exists, with `account.isDeleted` set and a note on stderr. A batch
counts as expired once its `accessExpirationDate` is earlier than the current time.

It returns the following, shortened here; `batch` holds every batch field that `batch list` returns:

```json
{
  "account": { "xuid": "2814600000000001", "email": "ABCD-tester00007@xboxtest.com", "gamertag": "TesterSeven", "isDeleted": false },
  "batch": { "id": 123456, "emailTemplate": "ABCD-tester", "accessExpirationDate": "2026-01-01T00:00:00+00:00" },
  "expired": true,
  "expiredOn": "2026-01-01T00:00:00+00:00"
}
```

`expiredOn` is `null` when the batch has not expired. The command exits 0 whenever it finds the
account, expired or not, so scripts should read `expired`. It exits 1 when the account is not found
among the batches this seller can list (a developer without seller wide read permission sees only
their own batches, so try `--seller-id`), when the email appears in more than one batch (stderr
lists them; use `batch get`), or on any request failure, which uses hints written for a lookup that
point to `--seller-id` rather than a batch ID. On an error response, or a batch list it can't read,
stderr starts with an `x-ms-correlation-id: <id>` line, as it does for `batch list` and `batch get`. The cost is one list request per 100 batches of the
seller, plus one read per candidate batch.

## Creating a batch

`batch create` sends one `POST` that starts a creation job, then by default waits for the
job to stop. The accounts count against the seller's test account limit until the batch is
[deleted](#deleting-a-batch), so the command asks for confirmation in a terminal; pass `--confirm` (`--yes`,
`-y`) to skip the prompt. Before asking, it lists the values it will send, including the
security question and the defaults the service will apply for options you left out. Without a
terminal and without `--confirm`, it prints that list, refuses, and sends nothing; this needs both
`--password-file` and `--security-answer-file`, because without a terminal the command otherwise
fails with "No terminal is available" before printing anything. The password and security answer
are read only after you confirm, so a declined create never asks for them.

| Option | Description |
| --- | --- |
| `--email-prefix <text>` | Required. The sandbox group, a hyphen, and a template, for example `ABCD-tester`. |
| `--count <count>` | Required. Number of accounts to create; at least 1. The service sets the maximum. |
| `--sandbox-id <id>` | Required. A sandbox the accounts can access. Repeat for more sandboxes. |
| `--password-file <path>` | File holding the account password, or `-` for stdin. See [Secrets](#secrets). |
| `--security-answer-file <path>` | File holding the security answer, or `-` for stdin. See [Secrets](#secrets). |
| `--child-count <count>` | Total child accounts in the batch, all linked to the first account; 0 or more. It is not a number per account: `--count 5 --child-count 2` creates 5 accounts and 2 children. The service sets the maximum. |
| `--first-name <text>` | Defaults to `Test`. |
| `--last-name <text>` | Defaults to `Account`. |
| `--security-question <text>` | The security question every account gets. The service requires one and has no default, so the CLI sends `What is your favorite hobby?` when you omit it. Must not be empty. |
| `--alternate-email <text>` | Defaults to the signed in developer's email. |
| `--access-expiration <date>` | When sandbox access expires, such as `2026-12-31`, read as UTC. Defaults to three months from now. |
| `--tag <tag>` | A tag to store on the batch, which `batch list --tag` can find later. Repeat for more tags. Tags appear as `keywords` in JSON output. |
| `--locale <locale>` | Defaults to `en-US`. |
| `--subscription <name>` | `Silver` or `Gold`, ignoring case. Defaults to `Gold`. |
| `--seller-id <id>` | Seller to create the accounts for, chosen the same way as for `batch list`. With no seller given, configured, or saved, the service uses your only seller and refuses when you have more than one. |
| `--confirm`, `--yes`, `-y` | Create without the confirmation prompt. |
| `--no-wait` | Return as soon as the service accepts the job. Cannot be combined with `--timeout`. |
| `--timeout <duration>` | How long to wait for the job, such as `90s`, `10m`, `2h`, or `1d`. Defaults to `5m`; at most `7d`. |
| `--output <format>` | `json` (default), `json-compact`, or `table`. |

The CLI checks only what it must to build the request. Limits such as the maximum `--count`,
`--child-count`, number of sandboxes, and number of tags are set by the service, which rejects
a request that exceeds them.

### Secrets

The password and security answer are never accepted as option values or environment variables,
so they stay out of shell history, process listings, and logs. Each comes from, in order:

1. Its `--password-file` or `--security-answer-file`. A single trailing line break is removed.
2. `-` in place of a path reads it from stdin. Only one of the two can read stdin.
3. With no file option, a prompt in the terminal that does not echo what you type. The security
   answer prompt shows the security question it answers.

Without a terminal and without the file option, the command fails before sending anything. Text
read from a file or stdin is decoded as UTF-8. The CLI never prints either value.

### Waiting and exit codes

While waiting, the CLI polls the job as the service's `Retry-After` asks, kept between 2 seconds
and 5 minutes, and prints a progress
line to stderr whenever the created or failed counts change. The final job is written to stdout
in the same shape as [`job get`](#following-a-creation-job).

| Exit code | Meaning |
| --- | --- |
| 0 | The job succeeded, or was accepted with `--no-wait`. stderr gives the command to follow a job accepted with `--no-wait`. |
| 1 | Nothing was created: the request was refused, rejected before it reached the service, failed before it was sent (for example when you are not signed in), got a 503 with `feature_disabled` or `service_configuration_invalid`, or was canceled at the confirmation prompt or with Ctrl+C before it was sent. |
| 2 | The job failed. Some accounts may still have been created; stderr gives the counts and the command to inspect the batch. |
| 3 | The outcome is unknown. See [When the outcome is unknown](#when-the-outcome-is-unknown). |
| 4 | The batch exists, but the wait ended before the job stopped. stderr gives the batch and job IDs and the command to resume waiting. Do not create again. |

The wait ends with exit 4 when `--timeout` passes with the job still running, when you press
Ctrl+C, or when a job read fails after waiting began (including a sign in that expired). A
temporary failure is retried, and the wait ends on the third in a row; any other failure ends the wait at once. None of
these stops the job. On a timeout the last job read is written to stdout; otherwise stdout is empty.
`--timeout` bounds the whole wait: a read still in flight at the deadline is abandoned, and none
starts after it.

Each request can take around 25 seconds, mostly to obtain the
XBOX credentials it needs, so even a `--no-wait` create is not instant.

### When the outcome is unknown

The create is not idempotent, so the CLI sends it once and never retries it. When it cannot tell
whether the service created a batch, it exits with code 3 instead of 1. That happens when:

* any 5xx comes back, with or without an error code, except a 503 with `feature_disabled` or
  `service_configuration_invalid`. This includes `test_account_create_outcome_unknown`, any 502
  (the service can fail after the batch was accepted), and a 503 or 504 with no error code,
  which can mean the connection reset after the request was forwarded;
* the connection fails or drops, or the request times out or is canceled, after it was sent. Only
  a DNS or TLS failure is treated as the request never reaching the service; a failed connect is
  not, because the HTTP handler can resend on a new connection after a pooled one drops; or
* the service accepts the request but its response cannot be read.

stderr then prints the time the request started, in UTC, and these steps:

1. Wait a few minutes; a batch still being created may not be listed yet.
2. Run `xbox test-account batch list --mine --email-prefix <prefix>` and look for a batch with
   the same email prefix and template created at or after that time.
3. If that batch's `jobId` is set, follow it with
   `xbox test-account job wait <job-id> --batch-id <batch-id>`. If `jobId` is `null`, the job has
   stopped; inspect it with `xbox test-account batch get <batch-id>`.
4. Create again only if no such batch exists.

The printed commands keep the `--seller-id`, `--config-env`, `--client-id`, `--tenant-id`,
`--scopes`, and `--profile` you used, so they can be copied as they are. A non-default profile
chosen through `XBOX_PROFILE` is written out as `--profile`.

### Errors when creating

Failures write a sanitized JSON error to stderr with `status`, `title`, `detail`, and the
error `code` when the response includes one. Scripts should branch on `code`, never on `detail`.
Most codes add a hint to `detail`.

A `400` that the service raises before it reads the request (malformed JSON, a field it does not
know, or a missing or out of range field) has no `code`; treat it as `invalid_request`. Its
per field messages are kept under `errors`. The service rejects body fields it does not know, so
a CLI newer than the service can get this `400` for an option the service does
not support yet.

| Code | Meaning |
| --- | --- |
| `test_account_sandbox_access_denied` | The developer cannot grant access to one of the `--sandbox-id` values. If every sandbox is refused, the developer token for that seller may grant no sandboxes at all: the account is not linked to the seller, the seller has no sandboxes, or Partner Center does not allow the developer to manage test accounts. |
| `test_account_count_invalid` | `--count` is outside what the service allows. |
| `test_account_child_count_invalid` | `--child-count` is outside what the service allows. |
| `test_account_email_prefix_invalid` | `--email-prefix` is not a sandbox group, a hyphen, and a template, such as `ABCD-tester`. |
| `test_account_name_invalid` | `--first-name` or `--last-name` was rejected. |
| `test_account_password_invalid` | The password does not meet the password rules. |
| `test_account_security_question_invalid` | `--security-question` was rejected. |
| `test_account_security_answer_invalid` | The security answer was rejected, for example because it equals the password or appears in the security question. |
| `test_account_alternate_email_invalid` | `--alternate-email` was rejected. |
| `test_account_keyword_invalid` | A `--tag` value was rejected, or there are too many. |
| `test_account_locale_invalid` | `--locale` is not supported, for example use `en-US`. |
| `test_account_subscription_unavailable` | `--subscription` is not available in this environment. Development environments reject `Gold`; use `--subscription Silver`. |
| `test_account_limit_exceeded` | The seller has reached its test account limit. |
| `test_account_access_denied` | The developer lacks the Partner Center role to create test accounts for this seller. |
| `test_account_create_outcome_unknown` | Exit code 3; see [When the outcome is unknown](#when-the-outcome-is-unknown). |
| `downstream_request_rejected` | The request was rejected without naming a field, for example because the `--email-prefix` group is not one of your sandbox groups (the part of a sandbox ID before the dot), or the security question or answer is too long. On `job get` or `job wait`, a `404` with this code means no such job exists in that batch. |
| `downstream_throttled` | Requests are being throttled and nothing was created. Wait, then try again. |
| Any other code, or none, on a 5xx | Exit code 3, because a batch may exist; see [When the outcome is unknown](#when-the-outcome-is-unknown). |
| `feature_disabled`, `service_configuration_invalid` | 503: test account creation is turned off or not configured. Exit code 1. |
| `invalid_request`, or a `400` with no code | The request was malformed or a value is not allowed; `detail` or `errors` names the field. |

## Following a creation job

`batch create` prints the batch and job IDs as soon as the job is accepted. A job can run for
days, so you can stop waiting at any time and follow it later:

| Option | Commands | Description |
| --- | --- | --- |
| `<job-id>` | `get`, `wait` | Required. The job ID from `batch create`, or the `jobId` of a batch from `batch list`. |
| `--batch-id <id>` | `get`, `wait` | Required positive batch ID the job belongs to. |
| `--seller-id <id>` | `get`, `wait` | Seller that owns the batch, chosen the same way as for `batch list`. |
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
job failed. `job get` reads the job once and, while it is running, prints on stderr how long the
service suggests waiting before checking again. `job wait` polls as `Retry-After` asks, kept
between 2 seconds and 5 minutes, or every 10 seconds when the service does not say, and retries a
transient read failure: a 429, any 502 or 504, a 503 without an error code or with
`downstream_throttled`, a network failure other than DNS or TLS (such as a dropped connection), or
a request timeout. A DNS or TLS failure is not retried, because waiting does not fix it. A failed
read that carries `Retry-After` is retried when the header asks, within the same limits;
otherwise after 10 seconds. It gives up on the third such failure in a row. The first read, made
before waiting starts, is not retried. A job read never changes anything, so retrying it is safe.

| Exit code | `job get` | `job wait` |
| --- | --- | --- |
| 0 | Running or succeeded | Succeeded |
| 1 | The request failed | The first read failed; nothing is written to stdout |
| 2 | Failed | Failed |
| 4 | Not used | The wait ended before the job stopped: `--timeout` passed, you pressed Ctrl+C, or a job read failed after waiting began (a temporary failure is retried, and the wait ends on the third in a row). The job keeps running; on a timeout the last job read is written to stdout, otherwise stdout is empty |

When `job wait` stops before the job does, it prints the command to resume waiting. Exit 4 means
the job may still be running: read the error on stderr, and run `job wait` again once it is fixed
or has cleared. A failure that does not clear, such as a lost access, makes the next `job wait`
exit 1. Exit 1 means the first read failed, so fix the request (for example a wrong job or batch
ID) first.

## Updating a batch

`batch update` sends one `PATCH` that starts an update job for the whole batch, then by
default reads the batch until its `jobId` clears and checks that it shows the requested values.
It can give the accounts access to more sandboxes, take access away, move when that access
expires, and add to or replace the batch's tags. Every change applies to every account in the batch.

In a terminal, the command first reads the batch, shows each requested change next to the
batch's current value, warns when a job is still running on it, and asks before changing it.
Pass `--confirm` (`--yes`, `-y`) to skip the prompt. Without a terminal and without `--confirm`,
it prints the requested changes, refuses, and sends nothing. If the batch cannot be read before
the prompt, nothing is changed.

| Option | Description |
| --- | --- |
| `<batch-id>` | Required positive batch ID, from `batch list` or `batch create`. |
| `--add-sandbox-id <id>` | Sandbox to give the accounts access to. Repeat for more sandboxes. |
| `--remove-sandbox-id <id>` | Sandbox to take the accounts' access away from. Repeat for more sandboxes. A sandbox cannot be both added and removed. |
| `--access-expiration <date>` | New date the accounts' sandbox access expires, such as `2026-12-31`, or an ISO 8601 date and time. This is not when the accounts themselves expire. It must be in the future, and the service keeps only the day. A batch with no sandboxes has no access to expire, so the service refuses an expiry only change on it. |
| `--tag <tag>` | Tag to add to the batch, shown as `keywords` in JSON. The batch keeps its other tags, and a tag it already has (ignoring case) is skipped. Each tag is trimmed and can be at most 100 characters. Repeat for more tags. Cannot be combined with `--clear-tags`. |
| `--replace-tags` | Replace every tag on the batch with the `--tag` values instead of adding them. Needs `--tag`. Here case matters, so replacing `smoke` with `Smoke` is a change. |
| `--clear-tags` | Remove every tag from the batch. |
| `--allow-not-mine` | Allow changing a batch another developer created. Without it, the update is refused with `test_account_batch_not_owned`. It may still be refused, depending on your permissions. |
| `--seller-id <id>` | Seller that owns the batch, chosen the same way as for `batch create`. |
| `--confirm`, `--yes`, `-y` | Update without the confirmation prompt. |
| `--no-wait` | Return as soon as the service accepts the update. Cannot be combined with `--timeout`. |
| `--timeout <duration>` | How long to wait for the update, such as `90s`, `10m`, or `2h`. Defaults to `5m`; at most `7d`. |
| `--output <format>` | `json` (default), `json-compact`, or `table`. |

Pass at least one change. A batch can have at most 20 tags; the service refuses an update that
would leave more. The service keeps the marker that records which tool created the batch, so
`--replace-tags` and `--clear-tags` never remove it, and it does not count toward the limit.
Changing sandboxes or the expiry needs
access to those sandboxes in your XBOX developer token; extending the expiry needs access to
every sandbox on the batch.

The command returns the values the batch has once the update completes, including every tag
it keeps:

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

The service answers with `state` set to `updating`. The CLI sets it to `updated` once two reads
in a row show the batch with no running job and the requested values: added tags must be
present ignoring case, and replaced tags must match exactly, including case. When the batch already
has every requested value, the service changes nothing and answers `unchanged` with a `null`
`jobId`, and the command exits 0 without waiting. With `--no-wait`, or when the wait ends first,
`state` stays `updating`. Update jobs cannot be followed with `job get` or `job wait`, which
read only creation jobs.

### Waiting and exit codes when updating

While waiting, the CLI reads the batch as the update's `Retry-After` asks, kept between 2
seconds and 5 minutes, or every 10 seconds when the service does not say. Temporary read failures
are retried as for [`job wait`](#following-a-creation-job), and the wait ends on the third in a
row. A failed update job can leave its `jobId` on the batch, so a `jobId` that never clears
suggests the job failed or stalled.

| Exit code | Meaning |
| --- | --- |
| 0 | The batch shows the change, it already had these values, or the update was accepted with `--no-wait`. |
| 1 | Nothing was changed: the request was refused or rejected, failed before it was sent, the batch could not be read before the prompt, or the update was canceled at the prompt. |
| 2 | The update job ended, but the batch does not show the change. stderr names the fields that differ. Read the batch with `batch get`, then update it again. stdout is empty. |
| 3 | The outcome is unknown. Read the batch with the printed `batch get` command before updating again. Sending the same change again is safe once no job is running: tags the batch already has are skipped, and the other changes set values rather than adding to them. |
| 4 | The update was accepted, but the wait ended while its `jobId` was still set: `--timeout` passed, you pressed Ctrl+C, or a read failed. On a timeout, stderr reports the `jobId` from the last read, and stdout holds the accepted response. After Ctrl+C or a failed read, stdout is empty. |

The update is sent once and never retried. Exit code 3 follows the same rules as
[When the outcome is unknown](#when-the-outcome-is-unknown) for `batch create`, and includes
`test_account_update_outcome_unknown`, a `5xx` with no error code, and a connection that drops
while waiting for the answer.

### Errors when updating

Failures write the same sanitized JSON error as other commands. These codes add a hint:

| `code` | Meaning |
| --- | --- |
| `test_account_batch_not_owned` | 403: another developer created the batch. Pass `--allow-not-mine` to lift this check; the update may still be refused, depending on your permissions. |
| `test_account_sandbox_access_denied` | 403: your XBOX developer token does not grant access to a sandbox in the change. Extending the expiry needs access to every sandbox on the batch. |
| `test_account_batch_not_found` | 404: no such batch for this seller. |
| `test_account_batch_busy` | 409: a job is running on the batch. Wait until `batch get` shows `jobId` as `null`. |
| `test_account_keyword_invalid` | 400: a `--tag` value is not allowed. |
| `test_account_access_denied` | The developer lacks the Partner Center role to manage test accounts for this seller. With `--allow-not-mine`, changing a batch you did not create was refused. |
| `downstream_throttled` | Requests are being throttled. Wait, then try again. |
| `test_account_update_outcome_unknown` | 502: exit code 3. |
| `feature_disabled`, `service_configuration_invalid` | 503: test accounts are turned off or not configured. Exit code 1. |

A `404` or `405` with no code means the service may not support updating batches
yet.

## Deleting a batch

`batch delete` sends one `DELETE` that starts a delete job for the whole batch, then by
default reads the batch until the service reports it gone. Every account in the batch loses its
sandbox access, and its test account record is deleted. Their Microsoft accounts and XBOX profiles
are not closed. A delete cannot be undone.

In a terminal, the command first reads the batch and shows its email template, size, and
creator, warns when a job is still running on it, and asks before deleting. Pass `--confirm`
(`--yes`, `-y`) to skip the prompt. Without a terminal and without `--confirm`, it prints what it
would delete, refuses, and sends nothing. If the batch cannot be read before the prompt, nothing
is deleted.

| Option | Description |
| --- | --- |
| `<batch-id>` | Required positive batch ID, from `batch list` or `batch create`. |
| `--allow-not-mine` | Allow deleting a batch another developer created. Without it, the delete is refused with `test_account_batch_not_owned`. It lifts only that check: the delete may still be refused, depending on your permissions, with `test_account_access_denied`. Your own batches are deleted with or without it. |
| `--seller-id <id>` | Seller that owns the batch, chosen the same way as for `batch create`. |
| `--confirm`, `--yes`, `-y` | Delete without the confirmation prompt. |
| `--no-wait` | Return as soon as the service accepts the delete. Cannot be combined with `--timeout`. |
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

The service answers with `state` set to `deleting`. The CLI sets it to `deleted` once a read of
the batch returns `404` with `test_account_batch_not_found`; with `--no-wait`, or when the wait
ends first, `state` stays `deleting`. The job ID cannot be followed with `job get` or `job wait`,
which read only creation jobs. While the delete runs, the batch still shows its `jobId`.

There is no bulk delete. To remove several batches, delete them one at a time; every seller
shares the same test account rate limit.

### Waiting and exit codes when deleting

While waiting, the CLI reads the batch as the delete's `Retry-After` asks, kept between 2
seconds and 5 minutes, or every 10 seconds when the service does not say. Temporary read failures
are retried as for [`job wait`](#following-a-creation-job), and the wait ends on the third in a
row.

| Exit code | Meaning |
| --- | --- |
| 0 | The batch is gone, or the delete was accepted with `--no-wait`. |
| 1 | Nothing was deleted: the request was refused or rejected, failed before it was sent, the batch could not be read before the prompt, or the delete was canceled at the prompt. |
| 2 | The delete job stopped, but the batch still exists: two reads in a row showed it with no running job. Inspect it with `batch get`. stdout is empty. |
| 3 | The outcome is unknown. Read the batch with the printed `batch get` command before deleting again: a `404` with `test_account_batch_not_found` means it was deleted, a set `jobId` means the delete is likely running, and a `null` `jobId` means it may not have started yet. Wait a minute and read it again; delete again only if `jobId` is still `null`. |
| 4 | The delete was accepted, but the wait ended while the batch still existed: `--timeout` passed, you pressed Ctrl+C, or a read failed. On a timeout, stderr reports the batch as not yet deleted, with the `jobId` and `progress` from the last read, and stdout holds the accepted response. After Ctrl+C or a failed read, stdout is empty. A delete job can fail or stall and leave its `jobId` on the batch indefinitely, so a `jobId` that stays set with `progress` unchanged suggests the delete stalled. |

The delete is sent once and never retried. Exit code 3 follows the same rules as
[When the outcome is unknown](#when-the-outcome-is-unknown) for `batch create`, and includes
`test_account_delete_outcome_unknown`. It also includes a `504` with no error code, returned when the delete outlasts its timeout while the
service may still be deleting, and a
connection that drops while waiting for the answer.

### Errors when deleting

Failures write the same sanitized JSON error as other commands. These codes add a hint:

| `code` | Meaning |
| --- | --- |
| `test_account_batch_not_owned` | 403: another developer created the batch; the message names their Partner Center user ID. Pass `--allow-not-mine` to lift this check; the delete may still be refused, depending on your permissions. |
| `test_account_batch_not_found` | 404: no such batch for this seller, or it was already deleted. Only this code means the batch is gone; a 404 with `downstream_request_rejected` does not. |
| `test_account_batch_busy` | 409: a job is running on the batch, which includes a delete already in progress. Wait until `batch get` shows `jobId` as `null`. A `jobId` that never clears may be a stalled job. |
| `test_account_access_denied` | The developer lacks the Partner Center role to manage test accounts for this seller. With `--allow-not-mine`, deleting a batch you did not create was refused. |
| `downstream_throttled` | Requests are being throttled. Wait, then try again. |
| `test_account_delete_outcome_unknown` | 502 or 504: exit code 3. A 504 with no code is treated the same. |
| `feature_disabled`, `service_configuration_invalid` | 503: test accounts are turned off or not configured. Exit code 1. |

A `404` or `405` with no code means the service may not support deleting batches
yet.

## Table output

`--output table` is supported for every command. Times are shown in UTC, and `STATE` shows
`complete` or `pending` from `isComplete`. Summary lines go to stderr, so stdout holds only the
table.

| Command | Columns |
| --- | --- |
| `test-account batch list` | `BATCH ID`, `EMAIL TEMPLATE`, `SIZE`, `STATE`, `CREATED BY`, `CREATED (UTC)`, `EXPIRES (UTC)`, then `SANDBOXES` with `--include-sandboxes`. stderr gives the number of batches. |
| `test-account batch get` | One row per account: `EMAIL`, `GAMERTAG`, `XUID`, and `DELETED`, sorted by email. stderr summarizes the batch (ID, email template, state, accounts returned, batch size), its sandbox access, and the progress message of a pending batch. Use JSON for the other batch fields. |
| `test-account get` | One row: `EMAIL`, `GAMERTAG`, `BATCH ID`, `EMAIL TEMPLATE`, `EXPIRES (UTC)`, and `EXPIRED` (`yes` or `no`). |
| `test-account batch create`, `job get`, `job wait` | One row: `BATCH ID`, `JOB ID`, `STATE`, `TOTAL`, `SUCCEEDED`, `FAILED`. Here `STATE` is the job's `running`, `succeeded`, or `failed`. stderr gives the progress message and, for a failed job, its error. |
| `test-account batch update` | One row: `BATCH ID`, `JOB ID`, `STATE`, `EXPIRES (UTC)`. Here `STATE` is `updated`, `updating`, or `unchanged`. stderr lists the sandboxes and keywords. |
| `test-account batch delete` | One row: `BATCH ID`, `JOB ID`, `STATE`, `EMAIL TEMPLATE`, `SIZE`. Here `STATE` is `deleted` or `deleting`. |

Table output is a human convenience, not a stability contract; use `--output json-compact` for
anything parsed by a script.

## Examples

```bash
xbox test-account batch list --mine --limit 5

xbox test-account batch list --state pending --output table

xbox test-account batch list --email-prefix ABCDEF-smoke --include-sandboxes

xbox test-account batch get 123456 --output table

xbox test-account batch list --expired --limit 10

xbox test-account get ABCD-tester00007@xboxtest.com --output table

xbox test-account batch get 123456 --seller-id 1234567 --output json-compact

# Prompts for the password and security answer, then asks to confirm
xbox test-account batch create --email-prefix ABCD-tester --count 5 --sandbox-id ABCD.1

# Unattended: secrets from files, no prompt, wait up to 30 minutes
xbox test-account batch create --email-prefix ABCD-smoke --count 2 --sandbox-id ABCD.1 \
  --password-file ./password.txt --security-answer-file ./answer.txt \
  --subscription Silver --tag smoke --timeout 30m --confirm

xbox test-account job get 0f8e5b2a-1c3d-4e5f-8a9b-0c1d2e3f4a5b --batch-id 123456 --output table

xbox test-account job wait 0f8e5b2a-1c3d-4e5f-8a9b-0c1d2e3f4a5b --batch-id 123456 --timeout 2h

# Shows the changes, asks to confirm, then waits until the batch shows them
xbox test-account batch update 123456 --add-sandbox-id ABCD.2 --access-expiration 2026-12-31

# Unattended: add two tags without waiting
xbox test-account batch update 123456 --tag smoke --tag nightly --confirm --no-wait

# Replace every tag with one
xbox test-account batch update 123456 --tag nightly --replace-tags --confirm

# Shows the batch, asks to confirm, then waits until it is gone
xbox test-account batch delete 123456

# Unattended delete of a batch another developer created, without waiting
xbox test-account batch delete 123456 --allow-not-mine --confirm --no-wait
```

See [Commands](../commands.md) for the global options that apply across the CLI.
