---
title: PC
description: Get or switch the XBOX sandbox this Windows PC uses, like XblPCSandbox.exe.
---

# PC

The `pc` commands work on the PC the CLI runs on, not on your Partner Center account.
They are **Experimental** and run on **Windows only**. On other platforms they fail with an
error.

A PC uses one XBOX sandbox at a time, for every user and every game on it. Games and the XBOX
services on the PC sign in to that sandbox. The default is `RETAIL`. To test a title in a
development sandbox such as `ABCDEF.1`, switch the PC to that sandbox first.

This is a different resource from the [`sandbox`](sandbox.md) commands, which list the
sandboxes in your Partner Center account. `xbox sandbox list` shows which sandboxes exist;
`xbox pc sandbox set` chooses which one this PC uses.

## Commands

Each command runs the same command of `XblPCSandbox.exe`, the PC Sandbox Switcher from the GDK
and the XBOX Live developer tools, so the CLI behaves the way the
[tool's documentation](https://learn.microsoft.com/gaming/gdk/docs/tools/tools-services/live-pc-sandbox-switcher)
describes.

| Command | Same as | Description |
| --- | --- | --- |
| `pc sandbox get` | `XblPCSandbox /get` | Get the sandbox this PC uses. Read-only. Needs no sign-in or elevation. |
| `pc sandbox set <sandbox-id>` | `XblPCSandbox /set <sandbox-id>`, or `/retail` | Switch this PC to a sandbox, or back to `RETAIL`. Restarts XBOX services and shows a UAC prompt. |
| `pc sandbox set <sandbox-id> --no-apps` | `... /noApps` | Switch without reopening the Microsoft Store and XBOX app. |
| `pc sandbox launch-apps` | `XblPCSandbox /launchApps` | Close and reopen the Microsoft Store and XBOX app in this PC's sandbox. Needs no elevation. |

None of these commands call an API. The tool's `/feedback` command has no CLI equivalent; run
`XblPCSandbox /feedback` directly.

## How it works

`get` asks Gaming Services which sandbox is in use, so it reports what games actually sign in
to. Gaming Services is the `GamingServices` Windows service, installed from the Microsoft Store,
that runs XBOX features on the PC.

`set` first runs `/get`, so a missing or broken Gaming Services fails before any prompt. It then
asks for confirmation and runs `/set` (or `/retail`). The tool asks for administrator rights,
saves the sandbox, and restarts the XBOX services (XblAuthManager, GamingServices, and
DiagTrack). Unless you pass `--no-apps`, it then reopens the Microsoft Store and XBOX app if
they were open. Tool output goes to stderr. When the tool finishes, the CLI checks the sandbox
the tool reports and fails if the PC does not use the requested one.

`set` always switches, even when the PC already uses the requested sandbox. Running it again
restarts the services, which is the documented fix when sign-in still uses the old sandbox. If
that does not help, restart the PC and see
[Troubleshooting sandboxes](https://aka.ms/XboxLiveSandboxHelp).

Switch to `RETAIL` before you update the XBOX app or Gaming Services from the Microsoft Store.

The CLI looks for `XblPCSandbox.exe` in this order:

1. The `XblPCSandbox:Path` setting, for example the `XBOX_XBLPCSANDBOX__PATH` environment
   variable.
2. The folder that contains the CLI.
3. Each folder on `PATH`.

The tool ships with the GDK. You can also get it from `XboxLiveTools.zip` on the
[XBOX Live developer tools releases](https://github.com/microsoft/xbox-live-developer-tools/releases)
page. Gaming Services must be installed.

## Options

| Option | Command | Description |
| --- | --- | --- |
| `--confirm`, `--yes`, `-y` | `set` | Switch without prompting. Required outside an interactive terminal. |
| `--no-apps` | `set` | Don't reopen the Microsoft Store and XBOX app. Same as the tool's `/noApps`. Run `launch-apps` later to reopen them. |
| `--output <format>` | all | `json` (default), `json-compact`, or `table`. |

Sandbox IDs are case-insensitive here and are sent to the tool in upper case.

## Output

`get` and `launch-apps` return `{"sandbox": "ABCDEF.1"}`.

`set` returns `{"sandbox": "ABCDEF.1", "previousSandbox": "RETAIL"}`.

## Table output

`--output table` renders a `SANDBOX` column for `get` and `launch-apps`, and `SANDBOX` and
`PREVIOUS SANDBOX` columns for `set`. Table output is a human convenience, not a stability
contract; use `--output json-compact` for anything parsed by a script.

## Errors

When `XblPCSandbox.exe` fails, or a switch does not take effect, the error ends with a link to
[Troubleshooting sandboxes](https://aka.ms/XboxLiveSandboxHelp) and suggests
`XblPCSandbox /feedback` to report the problem. If the tool's own output already shows the link,
the CLI doesn't repeat it.

| Cause | Message |
| --- | --- |
| UAC prompt declined | The administrator prompt was declined, so the sandbox was not changed. |
| Gaming Services missing or not working | Gaming Services is missing, out of date, or not working on this PC. The tool's output, shown above the message, names the cause. Links to the Gaming Services Repair Tool. |
| Switch did not take effect | The tool reported success, but the PC uses a different sandbox. Run the switch again, then restart the PC if it still fails. |
| Tool not found | Explains where the CLI looked and how to install the tool. |
| Cancelled with Ctrl-C | The CLI stops the tool. If the administrator prompt was already accepted, the switch may still complete; run `xbox pc sandbox get` to check. |

## Examples

```bash
xbox pc sandbox get

xbox pc sandbox set ABCDEF.1 --confirm

xbox pc sandbox set ABCDEF.1 --confirm --no-apps
xbox pc sandbox launch-apps

xbox pc sandbox set RETAIL --confirm
```

See [Safety](../safety.md) for how confirmation works, and [Commands](../commands.md) for the
global options that apply across the CLI.
