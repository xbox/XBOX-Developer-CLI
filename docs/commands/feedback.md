---
title: Feedback
description: Open a GitHub bug-report draft with CLI environment information.
---

# Feedback

```bash
xbox feedback
```

Shows an environment summary, warns that GitHub issues are public, and asks whether
to open a bug-report draft in your default browser. Enter `Y` or `yes` to proceed;
`N`, `no`, `q`, an empty answer, or end-of-input cancels without opening a browser.
Ctrl-C cancels the command.

The draft opens under <https://github.com/microsoft/XBOX-Developer-CLI/issues>,
using the repository's bug-report form with **Environment Summary** prefilled.
Describe the problem, reproduction steps, expected behavior, and errors in the form,
and review everything before submitting. The CLI never creates or submits an issue.
For feature requests and documentation problems, use the other templates in the
repository's issue chooser.

## Options

| Option | Description |
| --- | --- |
| `--no-browser` | Print the environment summary and prefilled issue URL without prompting or opening a browser. Works in scripts and headless terminals. |

```bash
xbox feedback --no-browser
```

Without `--no-browser`, stdin and stderr must both be interactive terminals.
Otherwise the command exits 1 with guidance to use `--no-browser`. Declining the
prompt exits 0; Ctrl-C exits nonzero. If the OS cannot launch a browser, the command
prints a warning and leaves the URL available for manual use, exiting 0 because
the draft link is still usable. The URL is always printed when a draft is requested,
even when a browser opens.

## Environment information and privacy

The form includes only the CLI version, OS description, OS architecture, process
architecture, and .NET runtime version. It does not read command history, logs,
request bodies, configuration values, saved context, credentials, account details,
or environment variables. No PlayFab or Partner Center API is called, and no
sign-in or title ID is required.

The environment summary is displayed before consent. Opening the draft sends that
summary to GitHub in the URL; `--no-browser` only prints it locally. Review it and
anything you add for sensitive information before submitting the public report.

## Output and compatibility

This is an informational command: the summary and URL are human-readable stdout,
including with `--output json` or `--output json-compact`. Prompts, warnings, and
errors go to stderr. It does not accept `--output table`.

[`report-issue`](report-issue.md) remains a hidden alias with identical behavior.
It does not appear in root Help or command suggestions.
