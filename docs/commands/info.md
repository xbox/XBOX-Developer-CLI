---
title: Info
description: Show the installed CLI version, the project URL, and the first command to run.
---

# Info

Show the installed CLI version, the project URL, and the first command to run.

```bash
xbox info
```

Use this command for a quick local sanity check after installing or updating the
CLI. It does not call PlayFab.

`info` has no machine-readable form. It prints the same banner for `json` and
`json-compact`, and refuses `--output table`. Use [`xbox upgrade`](upgrade.md)
when you need a version check that a script can parse.

## First run and terminal output

The first interactive bare `xbox` invocation of each installed version shows a
focused launch screen with the colored XBOX sphere, `xbox auth login`, and
`xbox --help`. When telemetry is enabled, the one-time disclosure follows with
links to details and opt-out instructions. Explicit help invocations show the
complete command overview. The first interactive invocation of another command
retains the full welcome, and `xbox info` replays the logo.

First-run output goes to standard error to keep command results on standard
output clean. ANSI color is omitted when output is redirected and respects
`NO_COLOR`, `TERM=dumb`, and `CLICOLOR=0`. See [Telemetry](../telemetry.md) for
the disclosure and opt-out policy.
