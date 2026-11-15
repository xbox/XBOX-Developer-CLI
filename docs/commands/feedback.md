---
title: Feedback
description: Open a GitHub bug-report draft with CLI environment information.
---

# Feedback

`xbox feedback` shows a summary of your environment, warns that GitHub issues are public, and
asks whether to open a bug-report draft in your browser. Enter `Y` to open it, or `N` or nothing to
cancel.

```bash
xbox feedback
```

The draft opens in the [XBOX CLI's GitHub issues](https://github.com/xbox/cli/issues),
with the environment summary filled in. Describe the problem in the form, review everything, and
submit it yourself; the CLI never submits an issue. For feature requests and documentation
problems, choose another template in the issue chooser.

## Options

| Option | Description |
| --- | --- |
| `--no-browser` | Print the environment summary and the draft's URL without asking or opening a browser. Use it in scripts and on headless machines. |

Without `--no-browser`, the command needs an interactive terminal, and otherwise fails and
suggests `--no-browser`. If no browser opens, open the printed URL yourself.

## What the draft includes

The summary has only the CLI version, the OS and its architecture, the process architecture, and
the .NET runtime version. It doesn't include command history, logs, settings, saved context,
credentials, or account details, and the command doesn't sign in or call any service. Opening the
draft sends the summary to GitHub in the URL.

## Output

The summary and URL are text on stdout, whatever `--output` is, and `--output table` is refused.
Prompts, warnings, and errors go to stderr. The command exits `0` when it opens or prints the
draft or you decline, and nonzero if it fails or you press Ctrl+C.

`xbox report-issue` is a hidden alias for this command; see [Report issue](report-issue.md).
