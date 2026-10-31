---
title: Help
description: Show the help for any command, the same text that passing --help prints.
---

# Help

Show help for any command.

```bash
xbox help [command...]
```

`xbox help <command> [subcommand...]` prints the same text as
`xbox <command> [subcommand...] --help`. With no command, it prints the root help.

```bash
xbox help playfab file list
xbox playfab file list --help   # same output
```

Help is plain text for people to read. `--output` does not change it.

Help labels Experimental commands and explains that their CLI interface may
change or be removed based on feedback.

## Root and nested help

`xbox help`, `xbox --help`, and `xbox -h` show the command overview. A bare
`xbox` normally shows it too; the first interactive invocation of an installed
version instead shows the [launch screen](info.md#first-run-and-terminal-output).
Help is available at every command depth:

```bash
xbox auth --help
xbox playfab --help
xbox playfab mps build --help
xbox help playfab economy inventory collection list
```

The last example is equivalent to
`xbox playfab economy inventory collection list --help`.

## Output and exit codes

Successful help writes human-readable text to stdout and exits `0`. An unknown
command writes an error and concise, contextual usage to stderr and exits `1`,
without writing help to stdout. Scripts should check both the exit code and
stderr rather than assuming any output means success.
