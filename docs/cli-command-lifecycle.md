---
title: Experimental commands
description: How much commands can change before and after the 1.0 release, and what the Experimental label means.
---

# Experimental commands

How much a command can change between releases depends on the CLI's version and on whether
Help labels the command `[Experimental]`. A label on a command group applies to every command
in it.

## Before 1.0

Until the 1.0 release, the XBOX CLI is in preview, and any command might change or be
removed between releases, whether or not it's labeled Experimental. That includes its paths,
arguments, options, defaults, structured output, and exit codes. For automation, pin a tested
CLI version and test your workflows before you upgrade.

## From 1.0

From the 1.0 release, commands that aren't labeled Experimental keep their documented
contract. A change that breaks automation comes only in a release that says it's breaking.
The contract covers:

- Command and subcommand paths.
- Argument and option names and meanings.
- Which inputs are required.
- Documented defaults.
- Documented JSON output, including field names and meanings.
- Documented exit codes.

New commands, options, and JSON properties can appear in any release, so your scripts must
ignore JSON properties they don't recognize. Output written for people, such as text and
tables, isn't part of the contract.

### API passthrough

When a command returns a service's response as it is, as `xbox playfab api` does, that
response follows the service's own compatibility contract, not the CLI's. The CLI's inputs,
exit codes, and the output it adds, such as the response envelope that `--data-only`
removes, are still covered.

## Experimental

Experimental commands are released for feedback on their interface. Even after 1.0, their
paths, options, defaults, output, and behavior might change, or the commands might be removed,
so don't rely on automation that uses them staying compatible.

Experimental commands are finished and tested, and follow the same security and safety rules
as every other command, including confirmation before destructive operations. They run like
any other command; you don't need to turn them on.
