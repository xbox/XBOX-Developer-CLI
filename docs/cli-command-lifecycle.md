---
title: Experimental commands
description: Understand pre-1.0 compatibility, GA guarantees, and Experimental command labels.
---

# Experimental commands

## Purpose

Compatibility commitments depend on the CLI release version and command
status. Help labels commands explicitly designated Experimental.

## Before 1.0

Before the first stable 1.0 release, the XBOX CLI is in preview and does not
provide a backward-compatibility guarantee. Commands may change or be removed
between releases, including their paths, arguments, options, defaults,
structured output, and exit behavior.

This applies to all commands, whether or not Help labels them Experimental.
An unlabeled command is not a GA compatibility commitment during preview.
For automation, pin a tested CLI version and validate your workflows before
upgrading.

## GA / Stable

Starting with the stable 1.0 release, commands not labeled Experimental carry
a backward-compatibility promise for their documented CLI contract. Changes
that break existing automation must be treated as an explicitly signaled
breaking release. Existing commitments remain binding.

The contract includes:

- Command and subcommand paths.
- Argument and option names and meanings.
- Required versus optional inputs.
- Documented defaults.
- Documented CLI-owned machine-readable output, including JSON field names
  and semantics.
- Documented exit behavior.

Compatible additions, such as new commands or optional functionality, are
allowed. JSON objects are extensible: consumers must ignore unknown properties.
Adding properties is compatible when existing documented field names, types,
semantics, presence guarantees, and response shapes remain unchanged.

Human-readable presentation and implementation details are outside this contract
unless explicitly documented as machine-readable behavior.

### API passthrough

When a command passes through an API response, as `xbox playfab api`
does, the service-owned payload follows that API's compatibility contract.
The CLI does not add a separate backward-compatibility guarantee for the
payload's field names, types, meanings, or response shape.

The CLI's documented command paths, inputs, defaults, response formatting
and projections (such as `--data-only`), stream routing, and exit behavior
remain covered. Output fields and envelopes defined by the CLI remain
covered, even when their data comes from an API.

## Experimental

Experimental commands are explicitly designated for interface feedback. They
must be functional, tested, and safe to ship. Their CLI contract is outside
the GA backward-compatibility promise, including from 1.0 onward:

- Paths, arguments/options, defaults, structured output, and behavior may
  change based on feedback.
- Commands may be removed.
- Users must not assume existing automation will remain compatible.

Quality, security, authentication, authorization, and safety requirements apply
to every command at every release stage, including production approval and
destructive confirmation. Incomplete, broken, mock, and developer-only
functionality must not ship.
Help must clearly label Experimental commands and explain their compatibility
boundary.

## Availability

All shipped commands must be available through normal discovery and invocation.
Experimental metadata affects Help labeling only. Debug, Release, local, and
published builds use the same command surface.
