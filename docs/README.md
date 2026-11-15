# XBOX CLI documentation

These pages explain how to install the XBOX CLI, sign in, and use its commands to
work with Partner Center games, playtests, XBOX services configuration, and PlayFab titles.

## Start here

> [!NOTE]
> The XBOX CLI is a technical preview. Until version 1.0, commands and output can
> change between releases; see [Experimental commands](cli-command-lifecycle.md).
> [Known issues](known-issues.md) lists what's unfinished in the latest release.

| Page | What it covers |
|------|----------------|
| [Installation](installation.md) | Install and update the CLI on Windows, Linux, or macOS. |
| [Getting started](getting-started.md) | Sign in, choose a seller, and list its games. |
| [Your first playtest](first-playtest.md) | Upload a build, then create, publish, and share a playtest of it. |
| [Use XBOX services in a playtest](xbox-services-in-a-playtest.md) | Prepare a game's achievements, embargo, and XBOX services configuration for a playtest. |
| [Authentication](authentication.md) | Sign in as yourself or as a service principal, check who's signed in, and get PlayFab access. |
| [Known issues](known-issues.md) | Problems and unfinished features in the latest release, and their workarounds. |

## Reference

| Page | What it covers |
|------|----------------|
| [Commands](commands.md) | Global options, output formats, exit codes, and every command below. |
| [PlayFab API integration](playfab-api.md) | Find and call PlayFab APIs from the terminal. |

The command pages follow the command tree:

- [`xbox`](commands.md): global options and output formats
  - [`auth`](commands/auth.md): sign in and out
  - [`context`](commands/context.md): default values, such as the seller and PlayFab title
  - [`game`](commands/game.md): Partner Center games
  - [`seller`](commands/seller.md): Partner Center sellers
  - [`sandbox`](commands/sandbox.md): XBOX sandboxes
  - [`audience`](commands/audience.md): a seller's audiences (Experimental)
  - [`package`](commands/package.md): game packages (Experimental)
  - [`pc`](commands/pc.md): this PC's XBOX sandbox (Experimental)
  - [`test-account`](commands/test-account.md): XBOX test accounts and their batches
  - [`playtest`](commands/playtest.md): XBOX game playtests
  - [`playfab`](commands/playfab.md): PlayFab services
    - [`api`](commands/playfab/api.md): call any API endpoint directly
    - [`mps`](commands/playfab/mps.md): Multiplayer Servers (Experimental)
    - [`file`](commands/playfab/file.md): entity files
    - [`title-data`](commands/playfab/title-data.md): title data
    - [`api-policy`](commands/playfab/api-policy.md): API access policy
    - [`cloudscript`](commands/playfab/cloudscript.md): CloudScript and Azure Functions (Experimental)
    - [`group`](commands/playfab/group.md): groups and membership (Experimental)
    - [`player-data`](commands/playfab/player-data.md): player data
    - [`economy`](commands/playfab/economy.md): Economy v2 inventory
    - [`experiment`](commands/playfab/experiment.md): experiments (Experimental)
    - [`player`](commands/playfab/player.md): player accounts (Experimental)
    - [`privacy`](commands/playfab/privacy.md): privacy exports (Experimental)
    - [`service-principal`](commands/playfab/service-principal.md): service-principal access to a studio (Experimental)
  - [`xbl`](commands/xbl.md): XBOX services configuration (Experimental)
    - [`achievement-image`](commands/xbl/achievement-image.md): achievement images
  - [`info`](commands/info.md): version and links
  - [`upgrade`](commands/upgrade.md): check for a newer release
  - [`feedback`](commands/feedback.md): open a bug-report draft with environment information
  - [`help`](commands/help.md): help for any command

## Behavior

| Page | What it covers |
|------|----------------|
| [Safety and destructive operations](safety.md) | Which commands ask before they change something, and what to do when a change's outcome is unknown. |
| [Configuration](configuration.md) | Settings, the environment variables that set them, and where saved values are kept. |
| [Telemetry](telemetry.md) | What's collected, and how to turn it off. |
| [Audit logging](audit-log.md) | Keep a local record of the PlayFab operations the CLI runs. |
| [Using the XBOX CLI from agents](cli-for-agents.md) | What an AI agent must ask before a change, and how it chooses targets, reads output, and finds APIs. |
| [Experimental commands](cli-command-lifecycle.md) | What compatibility each release promises, and what Experimental means. |

## Contributing

| Page | What it covers |
|------|----------------|
| [Building from source](contributing/building-from-source.md) | Build and run from the repository. |
| [Testing and coverage](contributing/testing.md) | Run the tests, including the end-to-end tests, and measure coverage. |
| [Fuzz testing](contributing/fuzzing.md) | Run and tune the property-based fuzz tests. |
| [Code generation](contributing/code-generation.md) | Regenerate PlayFab models and API metadata. |
| [Editing the docs](contributing/documentation.md) | Where a change's documentation goes, known issues, and how to write a page. |
