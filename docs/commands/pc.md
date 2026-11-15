---
title: PC
description: Get or switch the XBOX sandbox this Windows PC uses, like XblPCSandbox.exe.
---

# PC

The `pc` commands get or switch the XBOX sandbox that this Windows PC uses. They're
Experimental, and they run on Windows only. See
[Experimental commands](../cli-command-lifecycle.md).

A PC uses one sandbox at a time, for every user and every game on it, and games sign in to that
sandbox. The default is `RETAIL`. To test a game in a development sandbox such as `ABCDEF.1`,
switch the PC to it first. To see which sandboxes your Partner Center account has, use
[`xbox sandbox list`](sandbox.md).

## Commands

Each command runs `XblPCSandbox.exe`, the XBOX services PC Sandbox Switcher, and works the way
[its documentation](https://learn.microsoft.com/gaming/gdk/docs/tools/tools-services/live-pc-sandbox-switcher)
describes.

| Command | Same as | Description |
| --- | --- | --- |
| `pc sandbox get` | `XblPCSandbox /get` | Get the sandbox this PC uses. |
| `pc sandbox set <sandbox-id>` | `XblPCSandbox /set <sandbox-id>`, or `/retail` | Switch this PC to a sandbox, or back to `RETAIL`. |
| `pc sandbox launch-apps` | `XblPCSandbox /launchApps` | Close and reopen the Microsoft Store and XBOX app in this PC's sandbox. |

For the tool's `/feedback` command, run `XblPCSandbox /feedback` directly.

## Switching sandboxes

```bash
xbox pc sandbox set ABCDEF.1 --confirm
```

`pc sandbox set` changes the sandbox for every user on the PC. It asks before it switches, then
shows a User Account Control (UAC) prompt for administrator rights. It restarts the XBOX services
on the PC, and reopens the Microsoft Store and XBOX app if they were open. Then it checks that the
PC uses the new sandbox.

`set` switches even when the PC already uses the sandbox. Running it again restarts the services,
which fixes sign-in that still uses the old sandbox. If that doesn't help, restart the PC and see
[Troubleshooting sandboxes](https://aka.ms/XboxLiveSandboxHelp).

Switch to `RETAIL` before you update the XBOX app or Gaming Services from the Microsoft Store.

## Installing the tool

`XblPCSandbox.exe` ships with the XBOX Game Development Kit (GDK), and in `XboxLiveTools.zip` on
the [XBOX services development tools releases](https://github.com/microsoft/xbox-live-developer-tools/releases)
page. Gaming Services, the Windows service that runs XBOX features on the PC, must be installed.
The CLI looks for the tool in this order:

1. The `XblPCSandbox:Path` setting, such as the `XBOX_XBLPCSANDBOX__PATH` environment variable.
2. The folder that contains the CLI.
3. Each folder on `PATH`.

## Options

| Option | Command | Description |
| --- | --- | --- |
| `--confirm`, `--yes`, `-y` | `set` | Switch without the confirmation prompt. Required outside an interactive terminal. |
| `--no-apps` | `set` | Don't reopen the Microsoft Store and XBOX app, like the tool's `/noApps`. Run `launch-apps` later to reopen them. |
| `--output <format>` | all | `json` (default), `json-compact`, or `table`. |

## Output

`get` and `launch-apps` return `{"sandbox": "ABCDEF.1"}`, and `set` returns
`{"sandbox": "ABCDEF.1", "previousSandbox": "RETAIL"}`. With `--output table`, the columns are
`SANDBOX`, and `PREVIOUS SANDBOX` for `set`. The tool's own output goes to stderr.

## Errors

| Cause | What happens |
| --- | --- |
| The UAC prompt was declined | The sandbox isn't changed. |
| Gaming Services is missing or not working | The tool's output names the cause, and the error links to the Gaming Services Repair Tool. |
| The switch didn't take effect | The tool reported success, but the PC uses another sandbox. Run the switch again, then restart the PC if it still fails. |
| The tool wasn't found | The error says where the CLI looked and how to install the tool. |
| You pressed Ctrl+C | The CLI stops the tool. If you'd accepted the UAC prompt, the switch might still finish; check with `xbox pc sandbox get`. |

The tool's errors link to [Troubleshooting sandboxes](https://aka.ms/XboxLiveSandboxHelp) and
suggest `XblPCSandbox /feedback` to report a problem.

## Examples

Get the sandbox this PC uses:

```bash
xbox pc sandbox get
```

Switch to a development sandbox without reopening the Microsoft Store and XBOX app, then reopen
them:

```bash
xbox pc sandbox set ABCDEF.1 --confirm --no-apps
xbox pc sandbox launch-apps
```

Switch back to `RETAIL`:

```bash
xbox pc sandbox set RETAIL --confirm
```
