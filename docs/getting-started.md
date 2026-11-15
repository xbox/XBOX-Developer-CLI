---
title: Getting started
description: Install the XBOX CLI, sign in, select a Partner Center seller, and list its games.
---

# Getting started

Set up the XBOX CLI and check that you can reach your organization's Partner Center
games. By the end, you'll have signed in, saved a default seller, and listed its games.

## Before you begin

- An account with access to an existing Partner Center seller.

## 1. Install the XBOX CLI

Install the CLI as described in [Installation](installation.md), then confirm it runs:

```bash
xbox --help
```

## 2. Sign in

Sign in with the account that has access to your Partner Center seller. The command opens your
browser:

```bash
xbox auth login
```

The CLI shows the account you signed in as:

```text
Successfully signed in as: dev@nodpublishers.com
Token expires at: YYYY-MM-DD HH:MM:SS
```

If it's the wrong account, run `xbox auth login` again and choose the right one. See
[Authentication](authentication.md).

## 3. Choose a seller

A seller is the Partner Center organization whose games you work with. List the sellers your
account can use:

```bash
xbox seller list --output table
```

Find your organization in the `NAME` column, and copy its `SELLER ID`.

## 4. Save the seller

Save the seller ID you copied as your default, so later commands use it:

```bash
xbox context set seller-id <SELLER_ID>
```

To use another seller for one command, pass `--seller-id`. See [Context](commands/context.md).

## 5. List games

List the seller's games:

```bash
xbox game list --output table
```

If the list shows another seller's games, see the known issue
[`xbox game` commands ignore `--seller-id`](known-issues.md#xbox-game-commands-ignore---seller-id).

## Next steps

- [Inspect an existing game](commands/game.md#listing-and-lookup) by Store ID,
  XBOX title ID, or name.
- [Create a game](commands/game.md#creating-a-game).
- [Run your first playtest](first-playtest.md).
- Review [Safety and destructive operations](safety.md) before making changes.
- Explore the [Commands](commands.md) reference.
- Explore PlayFab workflows in [PlayFab API integration](playfab-api.md)
  or [Multiplayer Servers](commands/playfab/mps.md).
