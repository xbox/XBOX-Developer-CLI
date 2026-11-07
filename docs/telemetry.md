---
title: Telemetry
description: What anonymous product-usage telemetry the CLI collects, and how to turn it off.
---

# Telemetry

The XBOX Developer CLI collects limited, anonymous product-usage telemetry to help Microsoft understand command adoption, reliability, and performance. Telemetry is best-effort: it never changes command output or exit behavior, and machine-readable stdout remains reserved for command results.

Microsoft handles this data as described in the
[Microsoft privacy statement](https://go.microsoft.com/fwlink/?LinkID=824704). The repository's
[privacy notice](/PRIVACY.md) covers data collection by the software.

## What is collected

- CLI version, operating-system family, and .NET runtime description
- Canonical command path, success/failure outcome, bucketed exit code, and duration
- Authentication mode name (`user`, a service-principal mode such as `githuboidc`, or `invalid`), never the tenant, application, or identity behind it
- For PlayFab calls, only API group and operation names that match the bundled generated API metadata
- Bucketed HTTP status, dependency duration, and bounded failure category
- Counts of unexpected process failures, using a bounded failure category

Dependency traces are sampled independently. The default sampling ratio is 10%.

## What is not collected

The CLI does not collect command arguments, request or response bodies, PlayFab title IDs, usernames, tenant or account IDs, access tokens, file paths, configured secrets, or raw exception messages. Unknown PlayFab paths are reported only as `unknown`.

The `cli.command` dimension contains only canonical command names registered in the command tree,
joined with hyphens (for example, `playfab-economy-inventory-transaction-list`). It never contains
arguments, options, aliases, or user-supplied values, and values longer than 64 characters or
containing unsupported characters are reported as `unknown`. Telemetry schema version 2 changes
this dimension from the former leaf-only token to the canonical path so commands that share verbs
such as `list`, `get`, or `status` remain distinguishable.

## Version header

Separately from telemetry, every request the CLI sends with an access token carries a
`User-Agent: XboxDevCLI/<version>` header, such as `XboxDevCLI/0.1.0.9`, so those services can
tell which CLI versions call them. It holds nothing else, and opting out of telemetry doesn't
remove it.

## Approximate location

The CLI never sends your location. However, Application Insights derives an approximate location (country, region/state, and city) from the network IP that connects to the ingestion endpoint, and stores it on every record (including metrics). The source IP itself is not retained — it is stored as `0.0.0.0`. This coarse geography is used only to understand product adoption across markets. Opting out (see below) stops all telemetry, including this derived location.

## First-run disclosure

The first time telemetry is actually collected on a machine, XBOX CLI prints a concise one-time disclosure to standard error (never to stdout, which stays reserved for command results):

```text
TELEMETRY
  XBOX CLI collects anonymous usage telemetry.
  Learn more or opt out: https://aka.ms/xbox-cli/telemetry
```

The linked documentation provides the complete collection and opt-out details. A sentinel file under the CLI state directory records that the notice was shown so it does not repeat.

## Opt out

Disable telemetry for one command:

```powershell
xbox --disable-telemetry auth status
```

Disable telemetry for all future invocations:

```powershell
$env:XBOX_TELEMETRY_OPTOUT = "1"
```

On bash or zsh:

```bash
export XBOX_TELEMETRY_OPTOUT=1
```

Values `1`, `true`, and `yes` are treated as opt-out. The opt-out always wins over other configuration.

## Deployment configuration

Official release artifacts embed the Microsoft-owned Azure Monitor Application Insights destination at build time. The destination can't be changed; to stop sending telemetry, use the opt-outs above. The standard `APPLICATIONINSIGHTS_CONNECTION_STRING` variable is not read, because a host that sets it for its own application doesn't intend it for the CLI.

Other telemetry settings can be set with `XBOX_` variables:

```powershell
$env:XBOX_TELEMETRY__DEPENDENCYTRACINGENABLED = "true"
$env:XBOX_TELEMETRY__DEPENDENCYTRACESAMPLINGRATIO = "0.1"
```

`DependencyTraceSamplingRatio` is clamped to the range 0 through 1. Set `XBOX_TELEMETRY__ENABLED` to `false` to disable export for an environment or distribution.

Delivery cannot be guaranteed when a device is offline, blocks Azure Monitor endpoints, terminates the process before the bounded flush completes, or the user opts out. The embedded destination ensures zero-configuration delivery whenever telemetry is enabled and the endpoint is reachable.
