---
title: Info
description: Show the installed CLI version, the project URL, and the first command to run.
---

# Info

Show the installed CLI version, the project URL, and the first command to run.

```bash
xbox info
```

Use it to check an installation or an update. `info` writes text for people, whatever
`--output` is, and refuses `--output table`. For a version check a script can read, use
[`xbox upgrade`](upgrade.md).

## First run and terminal output

The first time you run a newly installed version at a terminal, the CLI writes a welcome to
stderr. A bare `xbox` shows a launch screen that points to `xbox auth login` and `xbox --help`;
any other command shows the full welcome before its own output. When telemetry is on, the
welcome includes a notice with how to turn it off; see [Telemetry](../telemetry.md). `xbox info`
shows the logo again.

Color is left out when output is redirected, or when `NO_COLOR`, `TERM=dumb`, or `CLICOLOR=0` is
set.
