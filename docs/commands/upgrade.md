---
title: Upgrade
description: Check for a newer stable release of the CLI and get instructions for updating by hand.
---

# Upgrade

`xbox upgrade` compares the running CLI against the latest public stable GitHub release
and tells you how to update by hand.

It is an **advisory command, not an installer**. It is read-only: it reads release
metadata and nothing else. It does not download binaries, verify or replace files, run an
installer or child process, write update state, or modify the installation. No PlayFab
title, Entra sign-in, confirmation, or interactive terminal is required.

## Command

| Command | Description |
| --- | --- |
| `upgrade` | Check for a newer CLI release and show manual update instructions. |

## Options

| Option | Description |
| --- | --- |
| `--check` | Explicitly request a version check. This is also the default, so `xbox upgrade` and `xbox upgrade --check` behave identically. It never installs an update. |
| `--output <format>` | `json` (default) or `json-compact`. |
| `--disable-telemetry` | Disable anonymous telemetry for this invocation. |

`--confirm`/`--yes`/`-y` are rejected, because nothing is installed; passing one fails
with `This command only checks for updates. Omit --confirm/--yes/-y; nothing will be
installed.`

No table view is declared, so `--output table` is unavailable. `--data-only` is accepted
as a global option but changes nothing — the result is not a PlayFab response envelope.

There is no `--status`, no download or apply mode, no background polling, no cache, no
prerelease selector, and no custom release source.

```bash
xbox upgrade
xbox upgrade --check
xbox upgrade --check --output json-compact
xbox help upgrade
xbox --version
```

From a source checkout:

```powershell
dotnet run --project .\src\XboxDevCLI\XboxDevCLI.csproj -- upgrade --check
dotnet run --project .\src\XboxDevCLI\XboxDevCLI.csproj -- --version
```

## Result and errors

A successful check writes one JSON object to stdout:

| Field | Contract |
| --- | --- |
| `schemaVersion` | `1`. |
| `currentVersion` | The running assembly's informational version, including prerelease and build metadata when present. |
| `latestVersion` | The selected release tag without the leading `v`. |
| `updateAvailable` | Whether the stable release is newer by semantic version precedence. Build metadata is ignored for comparison. |
| `releaseNotesUrl` | The selected release's page in the official repository, not a direct binary URL. |
| `message` | Human-readable summary. |
| `instructions` | Manual update steps when a newer release exists; an empty array otherwise. |

Exit `0` means the lookup and comparison succeeded, **including when an update is
available**. A local version equal to or newer than the release is never told to
downgrade. Development versions can be compared, but the check does not identify Git
commits as newer or older.

`currentVersion` reports the assembly's informational version. The unrelated `--version`
option reports `CliIdentity.Version`, a three-component assembly version that can differ
— for example, a release build carrying a source-revision suffix. The `info` display is
unchanged by this command.

Failures exit `1`, leave stdout empty, and explain the problem on stderr. An inaccessible
release, malformed metadata, an invalid version, a timeout, or a cancellation is never
reported as an up-to-date result.

Discovery requests only
`https://api.github.com/repos/microsoft/XBOX-Developer-CLI/releases/latest`, with a 30-second
end-to-end deadline, a 1 MiB metadata limit, and no automatic retries or redirects.
Draft and prerelease metadata and prerelease tags are rejected.

The request is anonymous: it does not use Entra tokens, `gh` credentials, or a GitHub
token option. Because of that:

- **HTTP 404** can mean there is no published stable release, or that the repository is
  not publicly accessible. Open the releases page in an authenticated browser; the
  command cannot determine a newer version in that case.
- **HTTP 403 or 429** produces access and rate-limit guidance.

## Manual update and distribution boundaries

The CLI deliberately does not replace itself. `upgrade` tells you what to do, and you do
it — which is why the following boundaries matter.

### Portable (ZIP) installations

Follow the returned release link and choose the ZIP matching your OS and CPU: Windows
x64 or Linux x64. The returned instructions and `upgrade --help` link to the
installation steps, and the [Installation](../installation.md) guide has Windows
PowerShell and Linux shell examples for extracting the entire ZIP, setting a persistent
PATH, and diagnosing multiple installations.

To update an existing ZIP installation: extract into a new folder, back up the old
installation, and close running CLI processes first. Only if you customized an
installation-local `appsettings.json` or `appsettings.*.json` should you carry those
customizations forward, after reviewing the new release's defaults. Otherwise keep the
new release's configuration files.

### State is not part of the update

Saved context and the authentication cache live in the user-local CLI state directory
(`%LOCALAPPDATA%\XboxDevCLI` on Windows) and are reused automatically. Do not copy or
import that state as part of an update. Use the same OS user: saved context is scoped to
the signed-in account and the endpoint's host and port, and flags and environment variables
still win. Authentication expiry is independent of updates.

A seller saved with `seller set` by a release from before `context` isn't carried over.
After that one upgrade, save it again with `xbox context set seller-id <id>`.

### PATH

PATH contains **directories**, not executable filenames. Replacing a ZIP installation at
the same path needs no PATH change. If the folder changes, replace its old PATH entry
using Windows **User variables > Path**, or the PATH setting in your Bash or Zsh startup
file. Do not discard unrelated PATH entries, and keep backup installations off PATH.
These are manual steps — `upgrade` never edits environment variables.

Open a new terminal and verify both the selected location and the version:

```powershell
Get-Command xbox -All | Select-Object CommandType, Source, Definition
xbox --version
```

```bash
type -a xbox
xbox --version
```

If an old executable, alias, or shell function is selected, resolve that before treating
the update as complete. You can also invoke the new executable by its full path before
changing PATH.

### Out of scope

Follow the release's installation and signing instructions. This command does not
authenticate downloaded binaries or bypass platform security checks. Binary signing,
notarization, package-manager integration, and automatic replacement are all outside its
scope, as are `xbox-install.json`, `.sha256` sidecars, an updater helper, an
official-build marker, and release-pipeline changes.

For source builds — see [Building from source](../contributing/building-from-source.md)
— or future package-manager distributions, use the original mechanism. The check does not
detect package ownership or invent package IDs, and a version on GitHub may not yet be
available in a package-manager channel.

## End-to-end example

Check for a release, branch on the result, and update by hand:

```bash
# Script-friendly check; exits 0 whether or not an update exists.
xbox upgrade --check --output json-compact

# Example stdout when a newer stable release is published:
# {"schemaVersion":1,"currentVersion":"1.2.0","latestVersion":"1.3.0",
#  "updateAvailable":true,"releaseNotesUrl":"https://github.com/microsoft/XBOX-Developer-CLI/releases/tag/v1.3.0",
#  "message":"...","instructions":["..."]}

# Then follow releaseNotesUrl, extract the ZIP for your OS and CPU into a new
# folder, point PATH at it if the folder changed, open a new terminal, and verify:
xbox --version
```

See [Commands](../commands.md) for the global options that apply across
the CLI, and [Installation](../installation.md) for the full install
and update steps the instructions refer to.
