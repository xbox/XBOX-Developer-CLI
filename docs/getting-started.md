---
title: Getting started
description: Install the XBOX Developer CLI, sign in, select a Partner Center seller, and list its games.
---

# Getting started

Set up the XBOX Developer CLI and verify access to your organization's Partner Center
games. By the end, you will have signed in, saved a default seller, and listed its games.

## Prerequisites

- An account with access to an existing Partner Center seller.

## 1. Install the XBOX Developer CLI

Download and extract the CLI as described in [Installation](installation.md), then
confirm it runs:

```bash
xbox --help
```

> [!NOTE]
> The examples in these docs assume `xbox` is on your `PATH`. If you are
> running from source, replace `xbox` with
> `dotnet run --project src/XboxDevCLI --`. See
> [Building from Source](contributing/building-from-source.md).

## 2. Sign in

Sign in with the account that has access to your intended Partner Center seller account.
This opens your system browser to complete the interactive sign-in:

```bash
xbox auth login
```

After a successful sign-in, the CLI shows your account and token expiration:

```text
Successfully signed in as: dev@nodpublishers.com
Token expires at: YYYY-MM-DD HH:MM:SS
```

Check the signed-in identity:

```bash
xbox auth status
```

If the identity is not the one you intended, run
`xbox auth login` again and choose the correct account. See
[Authentication](authentication.md) for details.

## 3. Choose a seller

A seller is the Partner Center organization whose games you want to work with.
List the sellers available to your signed-in account:

```bash
xbox seller list --output table
```

Choose the intended organization using the `NAME` and `SELLER ID` columns, and copy
its seller ID.

## 4. Save the seller

Replace `<SELLER_ID>` with the ID you copied, without the angle brackets:

```bash
xbox context set seller-id <SELLER_ID>
xbox context get --output table
```

Confirm that the saved `seller-id` matches your selection. The CLI saves it as your
local default but does not check access to the seller with Partner Center. The game
listing in the next step checks access through a service request.

> [!IMPORTANT]
> A command uses its explicit `--seller-id` first, then `XBOX_SELLER_ID`, then the
> saved seller. `context get` shows saved values, not necessarily the effective target.
> Run `xbox game list --output table --verbose` to see where the implicit seller
> value comes from.

See [Context](commands/context.md) for details about saved defaults.

## 5. List games

List the games belonging to the selected seller:

```bash
xbox game list --output table
```

## Next steps

- [Inspect an existing game](commands/game.md#listing-and-lookup) by Store ID,
  XBOX title ID, or name.
- [Create a game](commands/game.md#creating-a-game).
- Review [Safety and destructive operations](safety.md) before making changes.
- Explore the [Commands](commands.md) reference.
- Explore PlayFab workflows in [PlayFab API integration](playfab-api.md)
  or [Multiplayer Servers](commands/playfab/mps.md).
