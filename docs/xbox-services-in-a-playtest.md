---
title: Use XBOX services in a playtest
description: Prepare a game's XBOX services configuration for a playtest, from Full XBOX services and achievements to the embargo and the RETAIL publish.
---

# Use XBOX services in a playtest

A playtest runs in the RETAIL sandbox, the live environment players use. When the build uses
XBOX sign-in, achievements, presence, or other XBOX services, the game needs Full XBOX services,
and its XBOX services configuration must be published to RETAIL. Testers then use the same live
XBOX services as players of a released game. See
[XBOX services and Playtest](https://docs.xbox.com/publishing/game-publishing/concepts/playtest-preview#xbox-services-and-playtest).

Do these steps once, before you [create the playtest](first-playtest.md):

1. [Get Full XBOX services](#1-get-full-xbox-services).
2. [Choose what happens to achievements](#2-choose-what-happens-to-achievements).
3. [Set the embargo](#3-set-the-embargo).
4. [Publish the configuration to RETAIL](#4-publish-the-configuration-to-retail).

The examples use the XBOX title ID `1234ABCD` and a development sandbox, `XDKS.1`, that holds
the configuration the build was tested with.

## 1. Get Full XBOX services

A new game starts with XBOX services disabled. Limited XBOX services work only in development
sandboxes, and don't include achievements or stats. After your game concept is approved, send the
game's Store ID to your XBOX representative and ask them to onboard the product. Then select
**Enable full XBOX services** on the game's **Game setup** page in Partner Center. See
[Enable XBOX services](https://docs.xbox.com/publishing/game-publishing/concepts/xbox-services#enable-xbox-services).

Until then, configuration with achievements fails validation:

```text
XblLimited products do not support achievements or stats2013/stats2015 configuration.
```

## 2. Choose what happens to achievements

Achievements published to RETAIL are permanent. You can still change their names, descriptions,
and art, but you can't remove them or change their unlock rules or gamerscore. Stats published to
RETAIL can't be deleted either. The game's achievement system, title-managed or event-based, is
fixed by the first RETAIL publish. Before that publish, choose one of these:

| Choice | Use it when | How |
|--------|-------------|-----|
| Publish final achievements | The achievements are final. | Publish as usual. For an unreleased game, the embargo in the next step hides them from other players. |
| Ask for a validation override | Achievement design needs to change during the playtest. | Ask your XBOX representative for a development-time validation override. Remove it before you submit the game for certification. |

A game for PC or XBOX consoles can't skip publishing its achievements to RETAIL. In Partner
Center, answering **No** to **Publish Achievements to Retail?** fails with:

```text
The specified Platform:Windows can be used only when game is configured to Publish Achievements.
```

All published achievements must meet the XBOX Requirements for achievements and gamerscore. See
[Achievements](https://docs.xbox.com/publishing/game-publishing/concepts/playtest-preview#achievements).

## 3. Set the embargo

The embargo hides the game's achievements, presence, and other XBOX services data from players in
the XBOX app and console UI until a date you choose, and blocks screenshots and video capture.
Without one, players outside the playtest can see the game's configuration.

Set an embargo only for a game that isn't released yet. On a released game, a new embargo hides
its XBOX services data from its players.

In Partner Center, open **XBOX services** > **XBOX settings**, go to **Embargo date**, and set
a UTC date and time later than the playtest's end, and no later than the game's public release.
If the playtest runs past the release, end the embargo at the release. Select
**Allow Multiplayer Invites in Embargo** if testers invite each other to multiplayer sessions.
See [Configure embargo date](https://docs.xbox.com/publishing/game-publishing/tutorial-xbox-managed/how-to-configure-xbox-services#configure-embargo-date).

The embargo is a title-wide setting, stored in the `ALL` sandbox, and applies to every sandbox.
To check it, download `ALL`:

```bash
xbox xbl config download --title-id 1234ABCD --sandbox-id ALL --view draft --dest xbox-config-all
```

`productdeveloper.xml` holds the embargo:

```xml
<XboxLiveEmbargoDate>2027-03-01T17:00:00Z</XboxLiveEmbargoDate>
<EmbargoInvitesEnabled>true</EmbargoInvitesEnabled>
```

The embargo goes live when the title-wide configuration is published. If Partner Center shows
unpublished changes, publish them there, or with `--sandbox-id HIDDEN`:

> [!WARNING]
> A title-wide publish publishes every pending title-wide change, not only the embargo, and those
> changes reach RETAIL too. Review the pending changes first.

```bash
xbox xbl config publish --title-id 1234ABCD --sandbox-id HIDDEN
```

The command asks before it publishes. See
[Source selection](commands/xbl.md#source-selection).

Publishing is asynchronous. Before you publish to RETAIL, check that `lastPublish` shows the
title-wide publish succeeded:

```bash
xbox xbl config status --title-id 1234ABCD --sandbox-id HIDDEN
```

## 4. Publish the configuration to RETAIL

Publish the development sandbox's configuration straight to RETAIL. RETAIL doesn't require a
publish to CERT first, or certification.

> [!WARNING]
> A RETAIL publish is live for players as soon as it finishes, and published achievements can't
> be removed. See [Safety and destructive operations](safety.md).

Validate first. Validation changes nothing, and lists the achievements the publish adds:

```bash
xbox xbl config publish --title-id 1234ABCD \
  --source-sandbox-id XDKS.1 --sandbox-id RETAIL --validate-only
```

If validation shows the `FirstRetailPublishWithoutEffectiveEmbargo` warning, no embargo is in
effect yet. Finish [step 3](#3-set-the-embargo), including the title-wide publish.

Publishing from `XDKS.1` replaces RETAIL's working documents that have the same names, and
RETAIL's global title storage files, deleting any files only RETAIL holds. See
[Cross-sandbox publishing](commands/xbl.md#cross-sandbox-publishing-replaces-global-title-storage-files).

When the validation is clean, publish. The command describes the publish and asks first:

```bash
xbox xbl config publish --title-id 1234ABCD --source-sandbox-id XDKS.1 --sandbox-id RETAIL
```

Check `lastPublish` until the publish finishes:

```bash
xbox xbl config status --title-id 1234ABCD --sandbox-id RETAIL
```

Then [create and publish the playtest](first-playtest.md).
