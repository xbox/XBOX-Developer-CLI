---
title: XBOX services configuration
description: Manage XBOX services configuration, including its state, documents, publishing, schemas, and achievement images.
---

# XBOX services configuration

The `xbl` command family is Experimental. Its CLI interface might change
or be removed based on feedback.

`xbox xbl` manages XBOX services configuration — working and published configuration
state, configuration documents, publishing, and configuration schemas — as well as
achievement images. To list the sandboxes configuration commands target,
use [`xbox sandbox list`](sandbox.md).

These commands work with the same configuration that Partner Center edits. For what each
XBOX services feature does and how to configure it, see
[Portal configuration overview](https://devdocs.xbox.com/services/xbox-services/fundamentals/portal-config/live-portal-config-overview)
in the XBOX developer documentation.

| Group | Commands | Purpose |
| --- | --- | --- |
| `config` | `list`, `status`, `download`, `save`, `publish` | Inspect working and published configuration state, download and save configuration documents, and publish configuration to a sandbox. |
| `schema` | `list`, `get`, `download` | List configuration schema types and versions, get one version, or download XSDs. |
| [`achievement-image`](xbl/achievement-image.md) | `upload`, `get`, `list`, `download` | Register, inspect, or download achievement images. |

## Shared behavior

### Seller and sandbox

Every command except the `schema` commands takes `--seller-id`. Without it, a command uses
`XBOX_SELLER_ID` or the seller saved with [`xbox context set`](context.md), and then your only
seller; if you have several, pass `--seller-id`.

`config status` and `config publish` take `--sandbox-id` from `XBOX_SANDBOX_ID` or the saved
`sandbox-id` when you leave it out. `config download` and `config save` need `--sandbox-id` on
the command line for sandbox documents.

### Title targeting

`config list`, `config status`, `config publish`, and sandbox-scoped `config download` and
`config save` take the title's service configuration identifier (SCID) with `--scid`, or its
XBOX title ID with `--title-id`, but not both. With neither, they use `XBOX_TITLE_ID` or the
saved `title-id`; see [Context](context.md). `--scid` ignores both.

- `--title-id` is hexadecimal, as [`game get`](game.md) shows it: `00000001` through
  `7FFFFFFF`, with an optional `0x` prefix. For other title IDs, pass the SCID.
- Reads of a title bound to another title's configuration use the primary title's SCID.
- `config save` and `config publish` by a bound title's ID are refused, even with
  `--validate-only` (`bound_title_requires_scid`). The error names the primary title and
  SCID; rerun with that `--scid`.
- Saving or publishing by `--title-id` to configuration that other titles are bound to
  changes it for all of them. At a terminal, the CLI lists the bound titles and asks first;
  without one, it fails. `--allow-shared-configuration` skips the question. A write by
  `--scid` doesn't ask.
- `config save` and `config publish` write the seller and SCID they used to stderr. Read
  commands do with `--verbose`.

These commands save a title ID, then use it to read, download, and validate that title's
configuration in XDKS.1. The last one names the title with `--title-id` instead:

```powershell
xbox context set title-id 1234ABCD
xbox xbl config list --output table
xbox xbl config status --sandbox-id XDKS.1
xbox xbl config download --sandbox-id XDKS.1 --view draft --dest .\xbox-config
xbox xbl config save --sandbox-id XDKS.1 --file ".\xbox-config\*.xml" --validate-only
xbox xbl config status --title-id 1234ABCD --sandbox-id XDKS.1
```

### Output

Every `xbl` command accepts `--output table` as well as `json` (the default) and
`json-compact`. Each command's options table lists its table columns.

### Errors

A failed request exits 1 and writes a JSON error to stderr with `status`, `title`, `detail`,
and `code` when the service sends one. Branch on `status` or `code`, not on the wording of
`detail`. A 429 error includes `retryAfterSeconds`; see
[Rate limits](../cli-for-agents.md#rate-limits).

When XBOX services validation rejects the configuration, the error lists each message under
`validation`:

```json
{
  "status": 409,
  "title": "Validation failed.",
  "detail": "The configuration failed XBOX services validation. See validation for each message. MS-CV: ...",
  "code": "validation_failed",
  "validation": [
    {
      "severity": "Error",
      "documentName": "achievements2017.xml",
      "message": "XblLimited products do not support achievements or stats2013/stats2015 configuration. Please remove the unsupported configuration. Document failing validation: achievements2017.xml."
    }
  ]
}
```

`config save` and `config publish` without `--validate-only` say whether a failed change
could have been applied:

| Exit code | Meaning |
| --- | --- |
| 1 | Nothing was changed: the request was stopped or failed before it was sent, or was rejected with a `4xx` other than `408`. |
| 3 | The outcome is unknown: Ctrl+C, a timeout, or a lost connection after the request was sent, any other error status, or a success response the CLI can't read. |

On exit 3, don't resubmit. stderr prints a read-only command to check with: for a save,
`config download --view draft`, to compare with the files you saved; for a publish,
`config status`.

---

## config

Inspect, download, save, and publish XBOX services configuration for one SCID. A sandbox
holds a working configuration, which `config save` changes, and a published configuration,
which players and test accounts see once `config publish` makes the working configuration
live.

| Command | Description |
| --- | --- |
| `config list` | List working and published state across every accessible sandbox. |
| `config status` | Get working and published state for one sandbox, and its last publish. |
| `config download` | Download and extract a document set. |
| `config save` | Validate or save a working document set. |
| `config publish` | Validate or start publishing one configuration set. |

### `config list` and `config status`

Both commands report the latest working and published versions, each version's commit
time, and whether the working version has unpublished changes. A published commit time is
the commit time of the published version, not when the publish finished.

`config list` returns an array with one object per sandbox. `config status` returns one object
for one sandbox, and adds its last publish under `lastPublish`, or `null` when there's none to
report. For `--sandbox-id ALL`, `lastPublish` is always `null`, because the title-wide
configuration in `ALL` goes live through a publish to `HIDDEN`.

| `lastPublish` field | Description |
| --- | --- |
| `status` | `Running`, `Success`, or `Failed`. `null` when the service doesn't report one. |
| `jobId` | The publish job's ID. |
| `message` | The service's message about the publish, or `null`. |

```json
{
  "sandbox": "SANDBOX1",
  "hasWorkingConfiguration": true,
  "workingVersion": 9,
  "workingCommitTime": "2026-10-29T21:40:12+00:00",
  "hasPublishedConfiguration": true,
  "publishedVersion": 9,
  "publishedCommitTime": "2026-10-29T21:40:12+00:00",
  "hasUnpublishedChanges": false,
  "lastPublish": {
    "status": "Success",
    "jobId": "11111111-2222-3333-4444-555555555555",
    "message": null
  }
}
```

#### `config list` options

| Option | Required | Description |
| --- | --- | --- |
| `--scid <guid>` | Or title | SCID of the title's configuration. |
| `--title-id <hex-id>` | Or SCID | XBOX title ID. See [Title targeting](#title-targeting). |
| `--seller-id <id>` | No | Partner Center seller ID. |
| `--output <format>` | No | `json` (default), `json-compact`, or `table`. |

#### `config status` options

| Option | Required | Description |
| --- | --- | --- |
| `--scid <guid>` | Or title | SCID of the title's configuration. |
| `--title-id <hex-id>` | Or SCID | XBOX title ID. See [Title targeting](#title-targeting). |
| `--sandbox-id <id>` | Yes | Case-sensitive sandbox ID. Defaults to `XBOX_SANDBOX_ID`, then the saved sandbox ID. |
| `--seller-id <id>` | No | Partner Center seller ID. |
| `--output <format>` | No | `json` (default), `json-compact`, or `table`. |

Both commands' tables have the columns `SANDBOX`, `WORKING`, `WORKING COMMIT`,
`PUBLISHED`, `PUBLISHED COMMIT`, and `UNPUBLISHED`. `config status` adds `PUBLISH STATUS`,
the last publish's `status`.

#### `config list` and `config status` examples

```bash
# Every accessible sandbox for one title
xbox xbl config list \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --output table

# One sandbox, single JSON object
xbox xbl config status \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id SANDBOX1

# Compact output for scripting
xbox xbl config status \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id SANDBOX1 --output json-compact
```

### `config download` and `config save`

Download, validate, and save XBOX services configuration document sets. A document set
belongs to a sandbox (the default, which needs a SCID or title and `--sandbox-id`) or to the
account (`--type account`, which takes no SCID, title, or sandbox).

`config download` saves the document set's ETag next to the documents, as `<sandbox>.etag`, or
`Account.etag` for account scope; on Windows the file is hidden. `config save` reads the ETag
file next to the first file `--file` matches, and rewrites it after a save. If the
configuration changed since you downloaded it, the save fails; pass `--force` to save over
those changes, or `--etag` to supply an ETag yourself.

`config save` doesn't ask before it saves, and `--confirm` doesn't apply to it. Run it with
`--validate-only` first.

#### `config download` options

| Option | Required | Description |
| --- | --- | --- |
| `--view <draft\|published>` | Yes | `draft`, the working configuration that `config save` changes, or `published`, the live configuration that players and test accounts see. Partner Center uses the same two names. |
| `--dest <directory>` | Yes | Directory where documents and the ETag file are saved. |
| `--type <sandbox\|account>` | No | Document scope. Defaults to `sandbox`. |
| `--scid <guid>` | Or title for `sandbox` | SCID of the title's configuration. |
| `--title-id <hex-id>` | Or SCID for `sandbox` | XBOX title ID. See [Title targeting](#title-targeting). |
| `--sandbox-id <id>` | For `sandbox` | Case-sensitive sandbox ID. |
| `--version <number>` | No | Positive historical document-set version. Defaults to the latest version. |
| `--document <filename>` | No | Document filename to download. Repeat for more. Defaults to all documents. |
| `--seller-id <id>` | No | Partner Center seller ID. |
| `--output <format>` | No | `json` (default), `json-compact`, or `table` with `TYPE`, `SANDBOX`, `VIEW`, `VERSION`, `FILES`, and `DESTINATION` columns. |

#### `config save` options

| Option | Required | Description |
| --- | --- | --- |
| `--file <path-or-glob>` | Yes | Local file or quoted filename glob. Repeat `--file` for more, one path or pattern each: `--file a.xml --file b.xml`. Each file's name is its document name. A value that matches no file fails the command. |
| `--type <sandbox\|account>` | No | Document scope. Defaults to `sandbox`. |
| `--scid <guid>` | Or title for `sandbox` | SCID of the title's configuration. |
| `--title-id <hex-id>` | Or SCID for `sandbox` | XBOX title ID. See [Title targeting](#title-targeting). |
| `--sandbox-id <id>` | For `sandbox` | Case-sensitive sandbox ID. |
| `--validate-only` | No | Validate without saving. |
| `--allow-shared-configuration` | No | Save by `--title-id` to configuration other titles are bound to, without asking. See [Title targeting](#title-targeting). |
| `--etag <value>` | No | ETag to save against. Defaults to the saved ETag file. |
| `--force` | No | Save over changes made since the saved ETag. |
| `--message <text>` | No | Save message. |
| `--seller-id <id>` | No | Partner Center seller ID. |
| `--output <format>` | No | `json` (default), `json-compact`, or `table` with `TYPE`, `SANDBOX`, `VALIDATE ONLY`, `CAN COMMIT`, `COMMITTED`, `FILES`, and `ETAG FILE` columns. |

When XBOX services can't save the documents (`CanCommit` false with `--validate-only`,
otherwise `Committed` false), the command still writes the result to stdout, writes each
validation message to stderr, leaves the ETag file unchanged, and exits 1:

```text
XBOX services can't save these documents:
Error: achievements2017.xml: The root namespace doesn't match the document type. (NamespaceDocTypeMismatch)
```

Documents that match the latest working ones aren't a failure: nothing is saved,
`ValidationInfo` reports `FilesNotChangedCommit`, and the command exits 0.

#### `config download` and `config save` examples

Download the latest draft sandbox documents into `xbox-config`:

```bash
xbox xbl config download \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id SANDBOX1 --view draft --dest xbox-config
```

After you edit the documents, validate them all, or only the two you changed:

```bash
xbox xbl config save \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id SANDBOX1 \
  --file "xbox-config/*.xml" --validate-only

xbox xbl config save \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id SANDBOX1 \
  --file xbox-config/achievements2017.xml --file xbox-config/localization.xml --validate-only
```

Save them with a message:

```bash
xbox xbl config save \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id SANDBOX1 \
  --file "xbox-config/*.xml" --message "Update achievements"
```

Download one document as it was in published version 7:

```bash
xbox xbl config download \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id SANDBOX1 \
  --view published --version 7 --document achievements.xml --dest xbox-config
```

Download the account's draft documents:

```bash
xbox xbl config download --type account --view draft --dest xbox-config --output table
```

### `config publish`

Publish a working version of XBOX services configuration to a sandbox's published
configuration. Publishing is asynchronous: start it with `config publish`, then run
[`config status`](#config-list-and-config-status) for the destination sandbox until
`lastPublish.status` is `Success` or `Failed`.

#### Source selection

`--sandbox-id` is the destination. The source is a working version of the source sandbox,
which defaults to the destination:

| Scenario | Effect |
| --- | --- |
| `--source-sandbox-id` omitted or equal to `--sandbox-id` | Publishes the sandbox's latest working version. |
| `--source-sandbox-id` differs from `--sandbox-id` | Publishes the source sandbox's latest working version to the destination, and [replaces the destination's global title storage files](#cross-sandbox-publishing-replaces-global-title-storage-files). |
| `--configset <number>` | Publishes that working version of the source sandbox. |

When the source sandbox has unpublished changes, its latest working version isn't what's
live there. A cross-sandbox publish without `--configset` then warns, and names the version
that's live:

```text
Warning: XDKS.1 has unpublished changes. This publish uses its latest working version, 53; what's live in XDKS.1 is working version 52. To publish that instead, add --configset 52.
```

To promote exactly what's live in the source, run with `--validate-only` first and add the
`--configset` it suggests. In the JSON output, `SourceVersion` is the working version used,
`sourceHasUnpublishedChanges` says whether it differs from what's live in the source, and
`sourceLiveVersion` is the version that's live there, when one is found.

A title-wide publish, `--sandbox-id HIDDEN`, always publishes the latest working configuration
in `ALL`, so it takes no `--source-sandbox-id` or `--configset`. To see which version goes
live, run `config status --sandbox-id ALL` first and read `workingVersion`.

#### Confirmation

`config publish` asks before it publishes in these cases:

| Publish | Why it asks |
| --- | --- |
| `--sandbox-id RETAIL` | Players see the change when the publish finishes. |
| `--sandbox-id CERT` or `CERT.DEBUG` | XBOX certification tests in these sandboxes. |
| `--sandbox-id HIDDEN` | Publishes the title-wide configuration, which every sandbox uses, RETAIL included. |
| `--source-sandbox-id` differs from `--sandbox-id` | First replaces the destination's working documents that have the same names, and its [global title storage files](#cross-sandbox-publishing-replaces-global-title-storage-files). |

At a terminal it describes the publish and asks. Without a terminal it refuses and exits 1.
`--confirm` (`--yes`, `-y`) skips the question, and `--validate-only` never asks. See
[Safety and destructive operations](../safety.md).

Before it asks about a RETAIL publish, it validates the publish and lists the
[achievements it adds](#achievements-a-retail-publish-adds). If you answer yes, it publishes
the working version it validated, so what goes live is what was listed:

```console
$ xbox xbl config publish --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --source-sandbox-id CERT --sandbox-id RETAIL
This publish adds 2 achievements to RETAIL: 1, 2. Once they're live in RETAIL, achievements can't be removed, and their unlock rules and gamerscore can't change.
This publishes working version 52 of CERT's configuration to RETAIL for SCID aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee.
RETAIL is live: players see the change when the publish finishes.
Publishing from CERT first replaces RETAIL's working documents that have the same names.
Warning: publishing from CERT to RETAIL replaces RETAIL's global title storage files with CERT's. Files with the same path are overwritten, files only CERT holds are added, and files only RETAIL holds are deleted. Deleted files can't be recovered.

Publish to RETAIL? [y/N]:
```

When that validation fails, it publishes nothing and doesn't ask. So at a terminal, a RETAIL
publish by `--title-id` to configuration other titles are bound to needs
`--allow-shared-configuration`. With `--confirm` there's no list, so run `--validate-only`
first to see it.

#### Achievements a RETAIL publish adds

Once achievements are live in RETAIL, they can't be removed, and their unlock rules and
gamerscore can't change. So a RETAIL publish says which achievement IDs it adds that RETAIL
doesn't have yet:

- With `--validate-only`, the list goes to stderr, and the JSON output carries it in
  `achievementChanges.added`.
- At a terminal without `--validate-only`, the list comes before the question, as shown
  in [Confirmation](#confirmation).

```console
$ xbox xbl config publish --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --source-sandbox-id CERT --sandbox-id RETAIL --validate-only
This publish would add 2 achievements to RETAIL: 1, 2. Once they're live in RETAIL, achievements can't be removed, and their unlock rules and gamerscore can't change.
```

A publish that adds none says so. Other sandboxes don't list achievements.

#### Republishing

`config publish` publishes only changes. When nothing changed since the destination's last
successful publish, no publish starts, and the result's `Status` is `AlreadyPublished`. After
a publish fails, running `config publish` again retries it.

Sometimes XBOX locks RETAIL for a product, and a RETAIL to RETAIL publish then fails with
`retail_locked`. To publish anyway, add `--republish`: it forces the selected
configuration, by default RETAIL's latest working configuration, onto RETAIL. Publishing to
RETAIL from another sandbox, such as CERT, also clears the lock, but it replaces RETAIL's
working documents with that sandbox's.

`--republish` publishes in full, even when nothing changed. It can't be combined with
`--validate-only`.

#### Diagnostics

Validation diagnostics from XBOX services, such as the warning for a first RETAIL publish
without an embargo, go to stderr, one per line. The JSON output keeps them in
`ValidationResult`.

```text
XBOX services warning (FirstRetailPublishWithoutEffectiveEmbargo): This is the first time these Xbox Gameplay Settings will be published to RETAIL, and no embargo is configured.
```

When validation fails, `config publish` exits 1 and lists each message under `validation`, as
shown in [Errors](#errors).

#### Cross-sandbox publishing replaces global title storage files

> [!CAUTION]
> Publishing from one sandbox to another makes the destination's global title storage files
> an exact copy of the source's. Files only the destination has are deleted, and can't be
> recovered.

Global files are the files every player of the game reads, such as news feeds and maps:

| File | Source | Destination before | Destination after |
| --- | --- | --- | --- |
| `news.json` | Present | Present | Overwritten with the source's copy |
| `tuning.cfg` | Present | — | Added |
| `maps.bin` | — | Present | **Deleted** |

The copy takes whatever the source holds when the publish runs. Players' own data isn't
touched, and a same-sandbox publish leaves global files alone. To keep a destination file,
add it to the source sandbox before publishing.

Every cross-sandbox publish writes this warning to stderr, including with `--validate-only`,
and includes it in the [confirmation](#confirmation) question:

```text
Warning: publishing from SANDBOX1 to SANDBOX2 replaces SANDBOX2's global title storage files with SANDBOX1's. Files with the same path are overwritten, files only SANDBOX1 holds are added, and files only SANDBOX2 holds are deleted. Deleted files can't be recovered.
```

#### `config publish` options

| Option | Alias | Required | Description |
| --- | --- | --- | --- |
| `--scid <guid>` | — | Or title | SCID of the title's configuration. |
| `--title-id <hex-id>` | — | Or SCID | XBOX title ID. See [Title targeting](#title-targeting). |
| `--sandbox-id <id>` | — | Yes | Destination sandbox ID. Defaults to `XBOX_SANDBOX_ID`, then the saved sandbox ID. |
| `--source-sandbox-id <id>` | — | No | Source sandbox ID. Defaults to `--sandbox-id`. Not allowed with `--sandbox-id HIDDEN`. |
| `--validate-only` | — | No | Validate without publishing. For RETAIL, also lists the [achievements the publish adds](#achievements-a-retail-publish-adds). |
| `--allow-shared-configuration` | — | No | Publish by `--title-id` to configuration other titles are bound to, without asking. See [Title targeting](#title-targeting). |
| `--configset <number>` | — | No | Positive working version of the source sandbox. Defaults to its latest. Not allowed with `--sandbox-id HIDDEN`. |
| `--republish` | — | No | Publish in full even when nothing changed, or onto a RETAIL locked after a migration. See [Republishing](#republishing). |
| `--confirm` | `--yes`, `-y` | No | Publish without asking. See [Confirmation](#confirmation). |
| `--seller-id <id>` | — | No | Partner Center seller ID. |
| `--output <format>` | `-o` | No | `json` (default), `json-compact`, or `table` with `STATUS`, `SOURCE`, `DESTINATION`, `VALIDATE ONLY`, and `SOURCE VERSION` columns. |

#### `config publish` examples

Validate a publish to SANDBOX1 without publishing:

```bash
xbox xbl config publish \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id SANDBOX1 --validate-only
```

Publish SANDBOX1's latest working configuration, so it's live in SANDBOX1:

```bash
xbox xbl config publish \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id SANDBOX1
```

Publish SANDBOX1's latest working version to SANDBOX2. This also replaces SANDBOX2's global
title storage files with SANDBOX1's:

```bash
xbox xbl config publish \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --source-sandbox-id SANDBOX1 --sandbox-id SANDBOX2
```

Publish working version 42 of SANDBOX1 to SANDBOX2:

```bash
xbox xbl config publish \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee \
  --source-sandbox-id SANDBOX1 --sandbox-id SANDBOX2 --configset 42
```

Validate a publish from CERT to RETAIL, which lists the achievements it adds:

```bash
xbox xbl config publish \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --source-sandbox-id CERT --sandbox-id RETAIL --validate-only
```

Then pass the output's `SourceVersion` as `--configset` to publish the version you validated,
without a prompt:

```bash
xbox xbl config publish \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --source-sandbox-id CERT --sandbox-id RETAIL --configset 52 --confirm
```

Publish SANDBOX1 again although nothing changed:

```bash
xbox xbl config publish \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id SANDBOX1 --republish
```

Check the publish to SANDBOX2:

```bash
xbox xbl config status \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id SANDBOX2 --output table
```

---

## schema

Discover and download XBOX services configuration document schemas.

| Command | Description |
| --- | --- |
| `schema list` | List every schema type. |
| `schema list --type <type>` | List versions for one schema type. |
| `schema get --type <type>` | Get one version of a schema type: its type, version, and namespace. Defaults to the latest version. |
| `schema download --dest <directory>` | Save XSDs into a directory: one version, every version of one type, or every version of every type. |
| `schema download --type <type> --version <n> --dest -` | Write one XSD to stdout. |

### `schema list` options

| Option | Required | Description |
| --- | --- | --- |
| `--type <type>` | No | Schema type whose versions to list. Omit it to list all types. |
| `--output <format>` | No | `json` (default), `json-compact`, or `table` with `TYPE`, `VERSION`, `NAMESPACE`, `PATH`, and `BYTES` columns. |

### `schema get` options

| Option | Required | Description |
| --- | --- | --- |
| `--type <type>` | Yes | Schema type. |
| `--version <number>` | No | Positive schema version. Defaults to the type's latest version. |
| `--output <format>` | No | `json` (default), `json-compact`, or `table` with `TYPE`, `VERSION`, and `NAMESPACE` columns. |

`schema get` writes one JSON object:

```json
{
  "type": "achievements2017",
  "version": 1,
  "namespace": "http://config.mgt.xboxlive.com/schema/achievements2017/1"
}
```

### `schema download` options

| Option | Required | Description |
| --- | --- | --- |
| `--dest <directory>` | Yes | Directory for saved XSD files, or `-` to write one XSD to stdout. `-` requires both `--type` and `--version`. |
| `--type <type>` | No | Schema type. Omit it to download every type. |
| `--version <number>` | No | Positive schema version. Requires `--type`. Omit it to download every version. |
| `--output <format>` | No | `json` (default), `json-compact`, or `table` with `TYPE`, `VERSION`, `NAMESPACE`, `PATH`, and `BYTES` columns. |

Each schema is saved as `<type>_<version>.xsd`, with the type in lowercase, and the command
prints the saved paths. With `--dest -`, it writes the raw XSD to stdout instead, and
`--output` doesn't apply.

### `schema` examples

```bash
# List every schema type
xbox xbl schema list

# List versions of one type, as a table
xbox xbl schema list --type achievements2017 --output table

# Get the latest version of one type
xbox xbl schema get --type achievements2017

# Redirect one XSD straight to a file
xbox xbl schema download --type achievements2017 --version 1 --dest - > achievements2017_1.xsd

# Save one XSD into a directory
xbox xbl schema download --type achievements2017 --version 1 --dest schemas

# Download every version of one schema type
xbox xbl schema download --type achievements2017 --dest schemas

# Download every version of every schema type
xbox xbl schema download --dest schemas
```

---

## A typical configuration round trip

1. Find the sandboxes you can reach:

   ```bash
   xbox sandbox list --output table
   ```

2. See where working and published configuration stand:

   ```bash
   xbox xbl config list \
     --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --output table
   ```

3. Download the draft document set, which also saves `SANDBOX1.etag`:

   ```bash
   xbox xbl config download \
     --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id SANDBOX1 --view draft --dest xbox-config
   ```

4. Edit the documents in `xbox-config`, then validate them:

   ```bash
   xbox xbl config save \
     --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id SANDBOX1 \
     --file "xbox-config/*.xml" --validate-only
   ```

5. Save them:

   ```bash
   xbox xbl config save \
     --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id SANDBOX1 \
     --file "xbox-config/*.xml" --message "Update configuration"
   ```

6. Publish them in SANDBOX1:

   ```bash
   xbox xbl config publish \
     --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id SANDBOX1
   ```

7. Check the publish, and repeat until its status is `Success` or `Failed`:

   ```bash
   xbox xbl config status \
     --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id SANDBOX1 --output table
   ```
