---
title: Upgrade
description: Check for a newer stable release of the CLI and get instructions for updating by hand.
---

# Upgrade

`xbox upgrade` checks GitHub for a newer stable release and, if there is one, tells you how to
install it. It doesn't download or install anything, and you don't need to sign in.

```bash
xbox upgrade
```

## Options

| Option | Description |
| --- | --- |
| `--check` | Check for a newer release. This is the default, so `xbox upgrade` and `xbox upgrade --check` do the same thing. |
| `--output <format>` | `json` (default) or `json-compact`. `table` isn't available. |

`--confirm` (`--yes`, `-y`) is refused, because nothing is installed.

## Result

A successful check writes one JSON object to stdout:

| Field | Description |
| --- | --- |
| `schemaVersion` | `1`. |
| `currentVersion` | The running version, the same one `xbox --version` shows. |
| `latestVersion` | The latest stable release, without the leading `v`. |
| `updateAvailable` | `true` when the latest stable release is newer than the running version. |
| `releaseNotesUrl` | The release's page on GitHub. |
| `message` | A summary for people to read. |
| `instructions` | How to update, when `updateAvailable` is `true`; otherwise an empty array. |

```bash
xbox upgrade --output json-compact
```

```json
{"schemaVersion":1,"currentVersion":"1.2.0","latestVersion":"1.3.0","updateAvailable":true,"releaseNotesUrl":"https://github.com/xbox/cli/releases/tag/v1.3.0","message":"A newer stable XBOX CLI release is available. Update manually using the instructions below.","instructions":["..."]}
```

To install the update, see [Update a ZIP installation](../installation.md#update-a-zip-installation),
or for WinGet, [Windows: install with WinGet](../installation.md#windows-install-with-winget). A
release can reach WinGet a few hours after it appears on GitHub, so `winget upgrade` might not
offer it yet.

## Exit codes

| Exit code | Meaning |
| --- | --- |
| `0` | The check succeeded, whether or not an update is available. Read `updateAvailable`. |
| `1` | The check failed, and stdout is empty. |

The check doesn't sign in to GitHub. An HTTP 404 means it found no public stable release, and an
HTTP 403 or 429 means GitHub refused or rate-limited the request. In either case, open the
[releases page](https://github.com/xbox/cli/releases) in your browser instead.
