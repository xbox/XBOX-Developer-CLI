---
title: XBOX Live
description: Manage XBOX Live service configuration, including schemas, documents, achievement images, working and published state, and publishing.
---

# XBOX Live

The `xbl` command family is Experimental. Its CLI interface may change
or be removed based on feedback.

`xbox xbl` manages XBOX Live service configuration — configuration schemas,
configuration documents, working and published configuration state, and publishing —
as well as achievement images. To list the sandboxes configuration commands target,
use [`xbox sandbox list`](sandbox.md).

These commands sit at the top level rather than under `playfab`, because they don't call a
PlayFab API. The global `--playfab-title-id`
option is accepted everywhere but selects nothing for this family; an XBOX Live title is
identified by its service configuration ID (SCID) instead.

These commands work with the same configuration that Partner Center edits. For what each
XBOX services feature does and how to configure it, see
[Portal configuration overview](https://devdocs.xbox.com/services/xbox-services/fundamentals/portal-config/live-portal-config-overview)
in the XBOX developer documentation. `schema get` downloads the XSD for each configuration
document type.

The command tree is organized noun → verb:

| Noun | Commands | Purpose |
| --- | --- | --- |
| `schema` | `get` | List configuration schema types and versions, or download XSDs. |
| `document` | `download`, `commit` | Download a configuration document set, or validate and commit one. |
| `configuration` | `list`, `get` | Inspect working and published configuration state. |
| `publish` | `start`, `status` | Promote configuration between sandboxes and read publish status. |
| [`achievement-image`](xbl/achievement-image.md) | `upload`, `get`, `list`, `download` | Register, inspect, or download achievement images. |

See [Commands](../commands.md) for the global options shared across the
CLI, and [Authentication](../authentication.md) for signing in.

## Shared behavior

### Seller selection

Every command except `schema get` accepts `--seller-id`:

| Option | Description |
| --- | --- |
| `--seller-id <id>` | Optional positive numeric XBOX seller account ID. Must be authorized for the signed-in developer. Takes precedence over `XBOX_SELLER_ID` and the saved seller. |

The seller resolves from `--seller-id`, then `XBOX_SELLER_ID`, then the seller saved by
[`xbox context set seller-id <id>`](context.md) in the selected profile. The developer's identity
comes from the signed-in account, so there are no `--user-id` or `--user-email` options.
With no seller from any of these, the command uses the developer's only authorized seller; when several are available, pass `--seller-id` or save one with
`xbox context set seller-id <id>`.

### Sandbox selection

`configuration get`, `publish start`, and `publish status` take `--sandbox` as a
[context value](context.md#how-commands-use-saved-values): without the flag, they use
`XBOX_SANDBOX`, then the sandbox saved by `xbox context set sandbox <name>`. The `documents`
commands' `--sandbox` doesn't, since it applies only to sandbox documents.

`documents commit` and `publish start` change configuration, so when the seller or sandbox
doesn't come from its flag, they first write it and its source to stderr:

```text
Using --seller-id 1234567 from saved context.
```

`schema get` reads schema metadata that is not seller-scoped and does not accept
`--seller-id`.

### Output

All `xbl` commands declare table views and accept `--output table`
in addition to the default `json` and to `json-compact`.

| Option | Description |
| --- | --- |
| `--output`, `-o` | `json` (default), `json-compact`, or `table` for commands with a declared table view. |

Table columns are a human convenience, not a stability contract. Use `--output json` or
`--output json-compact` for anything a script or agent parses.

`--data-only` is accepted as a global option but has no effect here: the responses
carry no PlayFab `{code,status,data}` envelope, so there is no wrapper to strip. The
`--base-url` option belongs to the [`seller`](seller.md), [`game`](game.md), and
[`context`](context.md) commands and is not available under `xbl`.

### Errors

A failed request exits 1. When the error response is JSON, stderr gets a sanitized JSON
error with `status`, `title`, `detail`, and the error `code` when the response includes one.
Scripts should branch on `status` or `code`, never on the wording of `detail`.

When XBOX Live validation rejects the configuration, the error also lists each message under
`validation`, with its `severity` and `message`, plus the `documentName` and `error` key when
XBOX Live sends them:

```json
{
  "status": 409,
  "title": "XBOX Live validation failed.",
  "detail": "The configuration failed XBOX Live validation. See validation for each message. MS-CV: ...",
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

### Short aliases differ between commands

Short aliases are not consistent across this family, so prefer the long form in scripts:

| Alias | Means `--scid` on | Means something else on |
| --- | --- | --- |
| `-s` | `configuration list`, `configuration get`, `publish start`, `publish status` | `--sandbox` on `document download` and `document commit` |
| `-c` | `document download`, `document commit` | — |
| `-d` | — | `--sandbox` on `configuration get`, `publish start`, `publish status`; `--dest` on `document download` and `schema get` |
| `-v` | — | `--version` on `schema get`; `--validate-only` on `document commit` and `publish start` |

### Identifier rules

- `--scid` is parsed as a GUID. Non-GUID values are rejected.
- `--sandbox` is case-sensitive and must not contain whitespace. `document download`
  and `document commit` also reject path characters (`/`, `\`, `:`) and the names `.`
  and `..`, because they use the sandbox name in local file names.
- `--type` and `--view` accept any casing and are sent in canonical lowercase.

> [!WARNING]
> `document commit` and `publish start` change XBOX Live state for a real title.
> Both offer `--validate-only` as a dry run. `publish start` asks before it publishes to
> RETAIL, CERT, CERT.DEBUG, or HIDDEN, or from another sandbox
> ([Confirmation](#confirmation)). `document commit` doesn't ask, and the global
> `--confirm`/`--yes`/`-y` flag doesn't gate it. Review the SCID, sandbox, and file
> selection yourself before running either command. See
> [Safety and destructive operations](../safety.md).

---

## schema

Discover and download XBOX Live configuration document schemas. `schema get` has three
modes, selected by which options you supply.

| Invocation | Description |
| --- | --- |
| `schema get` | List every schema type. |
| `schema get --type <type>` | List versions for one schema type. |
| `schema get --type <type> --version <n>` | Download one XSD. |

All three modes are read-only.

### `schema get` options

| Option | Alias | Required | Description |
| --- | --- | --- | --- |
| `--type <type>` | `-t` | No | Schema type. Omitted by default, which lists all types. |
| `--version <number>` | `-v` | No | Positive schema version to download. Requires `--type`. Omitted by default, which lists versions, or with `--dest` saves every version. |
| `--dest <directory>` | `-d` | No | Directory for saved XSD files. Omitted by default; a selected XSD is written to stdout. |
| `--output <format>` | `-o` | No | `json` (default), `json-compact`, or `table` with `TYPE`, `VERSION`, `NAMESPACE`, `PATH`, and `BYTES` columns. |

`--version` without `--type` fails with `--version requires --type.`, and a non-positive
version fails with `--version must be a positive integer.`

### Output modes

- With `--type` and `--version` but no `--dest`, stdout receives the **raw XSD bytes** so
  the content can be redirected straight to a file. Adding `--output table` replaces that
  raw output with a one-row summary carrying the type, version, and byte count.
- With `--dest`, the schema is saved as `<type>_<version>.xsd` (the type lowercased) and
  the command prints saved-file metadata instead of the XSD.
- With `--dest` but no `--version`, every version of the selected type (or of every type,
  without `--type`) is downloaded into the destination directory, and the command prints
  the saved paths.
- Table columns that do not apply to the selected mode are left blank.

### Examples

```bash
# List every schema type
xbox xbl schema get

# List versions of one type, as a table
xbox xbl schema get --type achievements2017 --output table

# Redirect one XSD straight to a file
xbox xbl schema get --type achievements2017 --version 1 > achievements2017_1.xsd

# Save one XSD into a directory
xbox xbl schema get --type achievements2017 --version 1 --dest schemas

# Download every version of one schema type
xbox xbl schema get --type achievements2017 --dest schemas

# Download every version of every schema type
xbox xbl schema get --dest schemas
```

---

## document

Download, validate, and commit XBOX Live configuration document sets. A document set is
scoped either to a sandbox (the default, requiring `--scid` and `--sandbox`) or to the
account (`--type account`, which rejects `--scid` and `--sandbox`).

| Command | Description |
| --- | --- |
| `document download` | Download and extract a document set. Read-only. |
| `document commit` | Validate or commit a working document set. **Mutating.** |

### ETag metadata

`document download` writes the returned ETag alongside the extracted documents as
`<sandbox>.etag`, or `Account.etag` for account scope; on Windows the file is also marked
hidden. `document commit` reads that same file by default — looked up next to the first
file matched by `--file` — and rewrites it with the returned ETag. Pass
`--etag` to supply a value explicitly, or `--force` to refresh from the server and use the
current server ETag instead of the saved one.

Local document and ETag paths reject directories and existing reparse points before any
request is sent. `schema get` and `document download` also reject a symbolic link
anywhere in the `--dest` path, except macOS's own `/tmp`, `/var`, and `/etc` links.

### `document download` options

| Option | Alias | Required | Description |
| --- | --- | --- | --- |
| `--dest <directory>` | `-d` | Yes | Directory where documents and ETag metadata are saved. |
| `--type <sandbox\|account>` | `-t` | No | Document scope. Defaults to `sandbox`. |
| `--scid <guid>` | `-c` | For `sandbox` | Service configuration ID. Required for the default sandbox scope; rejected with `--type account`. |
| `--sandbox <name>` | `-s` | For `sandbox` | Case-sensitive sandbox name. Required for the default sandbox scope; rejected with `--type account`. |
| `--view <working\|published>` | — | No | Document-set view. Defaults to `working`. |
| `--version <number>` | — | No | Positive historical document-set version. Defaults to the latest version. |
| `--document <filename>` | — | No | Document filename filter. Repeat the option to select several. Defaults to all documents. |
| `--seller-id <id>` | — | No | Authorized seller ID override. |
| `--output <format>` | `-o` | No | `json` (default), `json-compact`, or `table` with `TYPE`, `SANDBOX`, `VIEW`, `VERSION`, `FILES`, and `DESTINATION` columns. |

### `document commit` options

| Option | Alias | Required | Description |
| --- | --- | --- | --- |
| `--file <path-or-glob>` | `-f` | Yes | Local file or filename glob pattern. Repeat `--file` for multiple inputs, one path or pattern per occurrence. Quote glob patterns so the CLI expands them. Each base filename is sent as the document name. Every supplied value must match at least one file, or the command fails rather than committing a partial selection. |
| `--type <sandbox\|account>` | `-t` | No | Document scope. Defaults to `sandbox`. |
| `--scid <guid>` | `-c` | For `sandbox` | Service configuration ID. Required for the default sandbox scope; rejected with `--type account`. |
| `--sandbox <name>` | `-s` | For `sandbox` | Case-sensitive sandbox name. Required for the default sandbox scope; rejected with `--type account`. |
| `--validate-only` | `-v` | No | Validate without persisting. Defaults to `false`, which commits. |
| `--etag <value>` | `-e` | No | ETag for optimistic concurrency. Defaults to the saved ETag file. |
| `--force` | — | No | Refresh and use the current server ETag. Defaults to `false`. |
| `--message <text>` | `-m` | No | Commit message. Omitted by default. |
| `--seller-id <id>` | — | No | Authorized seller ID override. |
| `--output <format>` | `-o` | No | `json` (default), `json-compact`, or `table` with `TYPE`, `SANDBOX`, `VALIDATE ONLY`, `CAN COMMIT`, `COMMITTED`, `FILES`, and `ETAG FILE` columns. |

If a document name is rejected without identifying the offending archive entry, the CLI
reports the selected file names and suggests rerunning with `--validate-only` and one
`--file` value at a time to isolate the unsupported name.

When XBOX Live can't commit the documents (`CanCommit` false with `--validate-only`, otherwise
`Committed` false), the command still writes the result to stdout, writes each validation
message to stderr, leaves the saved ETag file unchanged, and exits 1:

```text
XBOX Live can't commit these documents:
Error: achievements2017.xml: The root namespace doesn't match the document type. (NamespaceDocTypeMismatch)
```

Documents that match the latest working ones aren't a failure: XBOX Live commits nothing, reports
`FilesNotChangedCommit` in `ValidationInfo`, and the command exits 0.

Paths containing spaces or commas are literal paths, not lists. Overlapping patterns
and repeated paths select each file only once. The pre-public `--files` spelling is
replaced by `--file`; use `--file a.xml --file b.xml`, not `--file a.xml b.xml`.

### Examples

```bash
# Download the latest working sandbox documents
xbox xbl document download \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox SANDBOX1 --dest xbox-config

# Download a historical published version of one document
xbox xbl document download \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox SANDBOX1 \
  --view published --version 7 --document achievements.xml --dest xbox-config

# Download account-scoped documents
xbox xbl document download --type account --dest xbox-config --output table

# Dry run before committing
xbox xbl document commit \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox SANDBOX1 \
  --file "xbox-config/*.xml" --validate-only

# Validate two selected documents
xbox xbl document commit \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox SANDBOX1 \
  --file xbox-config/achievements2017.xml --file xbox-config/localization.xml --validate-only

# Commit with a message (mutating)
xbox xbl document commit \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox SANDBOX1 \
  --file "xbox-config/*.xml" --message "Update achievements"
```

---

## configuration

Inspect working and published configuration state for one SCID. Both commands report the
latest working and published versions, each version's commit time, and whether the
working version contains unpublished changes. Both are read-only.

| Command | Description |
| --- | --- |
| `configuration list` | List state across every accessible sandbox. |
| `configuration get` | Get state for one sandbox. |

Prefer `get` for focused automation or repeated status checks; it queries only the
selected sandbox and returns a single object instead of an array.

`configuration list` reads the sandboxes a page at a time (at most 20 a
page), following every page, and prints them all as one JSON array. If a request fails, even on
a later page, it prints nothing on stdout. When an error or a page the CLI
can't read is returned, stderr also gets the `x-ms-correlation-id` shared by every page.

### `configuration list` options

| Option | Alias | Required | Description |
| --- | --- | --- | --- |
| `--scid <guid>` | `-s` | Yes | Service configuration ID. No default. |
| `--seller-id <id>` | — | No | Authorized seller ID override. |
| `--output <format>` | `-o` | No | `json` (default), `json-compact`, or `table`. |

### `configuration get` options

| Option | Alias | Required | Description |
| --- | --- | --- | --- |
| `--scid <guid>` | `-s` | Yes | Service configuration ID. No default. |
| `--sandbox <name>` | `-d` | Yes | Case-sensitive sandbox name. Defaults to `XBOX_SANDBOX`, then the saved sandbox. |
| `--seller-id <id>` | — | No | Authorized seller ID override. |
| `--output <format>` | `-o` | No | `json` (default), `json-compact`, or `table`. |

Both commands share the same table columns: `SANDBOX`, `WORKING`, `WORKING COMMIT`,
`PUBLISHED`, `PUBLISHED COMMIT`, and `UNPUBLISHED`.

A published commit time is history metadata for the published changeset. It is not
the exact completion time of the asynchronous publish job.

### Examples

```bash
# Every accessible sandbox for one title
xbox xbl configuration list \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --output table

# One sandbox, single JSON object
xbox xbl configuration get \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox SANDBOX1

# Compact output for scripting
xbox xbl configuration get \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox SANDBOX1 --output json-compact
```

---

## publish

Promote XBOX Live configuration to a destination sandbox's published configuration, and
read publish status. This family intentionally exposes only publishing operations; there
are no product list or get commands.

| Command | Description |
| --- | --- |
| `publish start` | Validate or start publishing one configuration set. **Mutating.** |
| `publish status` | Read publish status for a destination sandbox. Read-only. |

### Source selection

`--sandbox` is always the destination, and the source defaults to the destination. The
source is always one of the source sandbox's working versions: its latest by default, or
the one given with `--configset`. XBOX Live publishes only working versions.

| Scenario | Effect |
| --- | --- |
| `--source-sandbox` omitted or equal to `--sandbox` | Publishes the sandbox's latest working version to its published configuration. |
| `--source-sandbox` differs from `--sandbox` | Publishes the source sandbox's latest working version to the destination. |
| `--configset <number>` | Publishes that working version of the source sandbox. |

A source sandbox with unpublished changes has a latest working version that isn't what's
live there. For a cross-sandbox publish without `--configset`, the command then writes a
warning to stderr naming the working version that matches what's live, found by
comparing document hashes:

```text
Warning: XDKS.1 has unpublished changes. This publish uses its latest working version, 53; what's live in XDKS.1 is working version 52. To publish that instead, add --configset 52.
```

To promote exactly what's live in the source, run with `--validate-only` first, and add
the `--configset` it suggests. The JSON response carries the same facts:
`SourceVersion` is the working version used, `sourceHasUnpublishedChanges` says whether
it differs from what's live in the source, and `sourceLiveVersion` is the working version
that's live there, absent when none of the source's latest 100 working versions matches
or the source was never published.

A title-wide publish, `--sandbox HIDDEN`, always publishes the latest working configuration in
`ALL`, so it takes no `--source-sandbox` or `--configset`, and `publish start` refuses either
before it signs in. To see which `ALL` version goes live, run
`xbox xbl configuration get --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox ALL` first and read
`workingVersion`.

```console
$ xbox xbl publish start --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox HIDDEN --configset 42
A title-wide (HIDDEN) publish always publishes the latest ALL working configuration, so it takes no source or version. Remove --configset.
```

### Confirmation

`publish start` asks before it publishes when the publish reaches certification, players,
or every sandbox, or when it copies from another sandbox:

| Publish | Why it asks |
| --- | --- |
| `--sandbox RETAIL` | Players see the change when the publish finishes. |
| `--sandbox CERT` or `CERT.DEBUG` | XBOX certification tests in these sandboxes. |
| `--sandbox HIDDEN` | Publishes the title-wide configuration, which every sandbox uses, RETAIL included. |
| `--source-sandbox` differs from `--sandbox` | First replaces the destination's working documents that have the same names, and its [global title storage files](#cross-sandbox-publishing-replaces-global-title-storage-files). |

At a terminal it describes the publish and asks `Publish to RETAIL? [y/N]`. Without a
terminal it refuses and exits 1. `--confirm` (`--yes`, `-y`) skips the question.
`--validate-only` never asks. As everywhere else, `--confirm` isn't agent approval.

```console
$ xbox xbl publish start --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --source-sandbox CERT --sandbox RETAIL
This publishes CERT's latest working configuration to RETAIL for SCID aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee.
RETAIL is live: players see the change when the publish finishes.
Publishing from CERT first replaces RETAIL's working documents that have the same names.
Warning: publishing from CERT to RETAIL replaces RETAIL's global title storage files with CERT's. Files with the same path are overwritten, files only CERT holds are added, and files only RETAIL holds are deleted. Deleted files can't be recovered.
Publish to RETAIL? [y/N]:
```

### Republishing

`publish start` publishes only changes. When nothing changed since the destination's last
successful publish, no publish starts, and the result's `Status` is `AlreadyPublished`. After
a publish fails, running `publish start` again retries it without `--republish`.

XBOX also locks RETAIL for a product after a migration, and a RETAIL to RETAIL publish then
fails with `retail_locked`. To publish anyway, add `--republish`: it forces the selected
configuration, by default RETAIL's latest working configuration, onto RETAIL. Publishing to
RETAIL from another sandbox, such as CERT, also clears the lock, but it replaces RETAIL's
working documents with that sandbox's.

`--republish` publishes in full, even when nothing changed. It can't be combined with
`--validate-only`.

### Diagnostics

XBOX Live's validation diagnostics, such as the warning for a first RETAIL publish without
an embargo, go to stderr, one per line, after the publish or validation. The JSON output
keeps them in `ValidationResult`.

```text
XBOX Live warning (FirstRetailPublishWithoutEffectiveEmbargo): This is the first time these Xbox Gameplay Settings will be published to RETAIL, and no embargo is configured.
```

### Cross-sandbox publishing replaces global title storage files

Publishing from one sandbox to another also makes the destination's global title storage
files an exact copy of the source's. Global files are the files every player of the game
reads, such as news feeds and maps.

| File | Source | Destination before | Destination after |
| --- | --- | --- | --- |
| `news.json` | Present | Present | Overwritten with the source's copy |
| `tuning.cfg` | Present | — | Added |
| `maps.bin` | — | Present | **Deleted** |

Global files have no versions, so deleted files can't be recovered, and the copy takes
whatever the source holds when the publish runs. Players' own data isn't touched. A
same-sandbox publish leaves global files alone.

`publish start` writes this to stderr whenever `--source-sandbox` names a different sandbox
from `--sandbox`, including with `--validate-only`. When it asks for
[confirmation](#confirmation), the warning is part of the question, so you see it before you
answer; with `--confirm` it's written before the publish starts. It's written once either way.
Sandbox names that differ only in case are the same sandbox, so they get no warning:

```text
Warning: publishing from SANDBOX1 to SANDBOX2 replaces SANDBOX2's global title storage files with SANDBOX1's. Files with the same path are overwritten, files only SANDBOX1 holds are added, and files only SANDBOX2 holds are deleted. Deleted files can't be recovered.
```

To keep a destination file, add it to the source sandbox before publishing.

### `publish start` options

| Option | Alias | Required | Description |
| --- | --- | --- | --- |
| `--scid <guid>` | `-s` | Yes | Service configuration ID. |
| `--sandbox <name>` | `-d` | Yes | Destination sandbox whose published configuration will be updated. Defaults to `XBOX_SANDBOX`, then the saved sandbox. |
| `--source-sandbox <name>` | — | No | Source sandbox. Defaults to `--sandbox`. Not allowed with `--sandbox HIDDEN`. |
| `--validate-only` | `-v` | No | Validate without publishing. Defaults to `false`, which starts publishing. |
| `--configset <number>` | — | No | Positive working version of the source sandbox. Defaults to its latest working version. Not allowed with `--sandbox HIDDEN`. |
| `--republish` | — | No | Publish in full even when nothing changed. On a RETAIL locked after a migration, forces the selected configuration, by default RETAIL's latest working one, onto RETAIL. Defaults to `false`, which publishes only changes. See [Republishing](#republishing). |
| `--confirm` | `--yes`, `-y` | No | Publish to RETAIL, CERT, CERT.DEBUG, or HIDDEN, or from another sandbox, without asking. See [Confirmation](#confirmation). |
| `--seller-id <id>` | — | No | Authorized seller ID override. |
| `--output <format>` | `-o` | No | `json` (default), `json-compact`, or `table` with `STATUS`, `SOURCE`, `DESTINATION`, `VALIDATE ONLY`, and `SOURCE VERSION` columns. |

### `publish status` options

| Option | Alias | Required | Description |
| --- | --- | --- | --- |
| `--scid <guid>` | `-s` | Yes | Service configuration ID. No default. |
| `--sandbox <name>` | `-d` | Yes | Case-sensitive destination sandbox. Defaults to `XBOX_SANDBOX`, then the saved sandbox. |
| `--seller-id <id>` | — | No | Authorized seller ID override. |
| `--output <format>` | `-o` | No | `json` (default), `json-compact`, or `table` with `STATUS` and `SANDBOX` columns. |

Publishing is asynchronous. Start it with `publish start`, then poll `publish status` for
the destination sandbox.

When validation fails, `publish start` (with or without `--validate-only`) exits 1 and stderr
lists each validation message under `validation`, as shown in [Errors](#errors).

### Examples

```bash
# Dry run first
xbox xbl publish start \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox SANDBOX1 --validate-only

# Same sandbox: latest working configuration -> published configuration
xbox xbl publish start \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox SANDBOX1

# Cross-sandbox: source's latest working version -> destination.
# Also replaces SANDBOX2's global title storage files with SANDBOX1's.
xbox xbl publish start \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --source-sandbox SANDBOX1 --sandbox SANDBOX2

# Publish one working version of the source sandbox
xbox xbl publish start \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee \
  --source-sandbox SANDBOX1 --sandbox SANDBOX2 --configset 42

# Validate a RETAIL publish, then publish it without a prompt (mutating)
xbox xbl publish start \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --source-sandbox CERT --sandbox RETAIL --validate-only
xbox xbl publish start \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --source-sandbox CERT --sandbox RETAIL --confirm

# Publish again although nothing changed
xbox xbl publish start \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox SANDBOX1 --republish

# Poll status
xbox xbl publish status \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox SANDBOX2 --output table
```

---

## A typical configuration round trip

```bash
# 1. Find the sandboxes you can reach
xbox sandbox list --output table

# 2. See where working and published configuration stand
xbox xbl configuration list \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --output table

# 3. Pull the working document set (also saves SANDBOX1.etag)
xbox xbl document download \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox SANDBOX1 --dest xbox-config

# 4. Edit the documents locally, then validate
xbox xbl document commit \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox SANDBOX1 \
  --file "xbox-config/*.xml" --validate-only

# 5. Commit, then publish, then poll
xbox xbl document commit \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox SANDBOX1 \
  --file "xbox-config/*.xml" --message "Update configuration"

xbox xbl publish start \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox SANDBOX1

xbox xbl publish status \
  --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox SANDBOX1 --output table
```

The commit and publish in step 5 change state for a real title. Review the SCID and sandbox
before running them, and see
[Safety and destructive operations](../safety.md).
