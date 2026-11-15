---
title: Your first playtest
description: Upload a build of an XBOX game, then create, publish, and share a playtest of it with the XBOX CLI and Partner Center.
---

# Your first playtest

A playtest lets a chosen group of players install a build of your game on XBOX consoles and PCs,
before or after release, without certification or a Microsoft Store listing. This page takes you from
uploading a build to sharing the playtest with testers.
[Playtest overview](https://learn.microsoft.com/gaming/game-publishing/concepts/playtest) explains
how playtests work.

Most commands on this page are Experimental, so their options and output might change. See
[Experimental commands](cli-command-lifecycle.md).

## Before you begin

- A Partner Center account set up to develop XBOX games, with a game in it. Playtests need no
  separate access request.
- Age ratings for the game. Complete the International Age Rating Coalition (IARC) questionnaire
  once, on the game's **Age ratings** page in Partner Center. A playtest can't be published without
  them.
- Permission to manage the game's packages in Partner Center. Without it, the `package` and
  `playtest` commands fail with 403 `package_permission_denied`. Ask a manager of your Partner
  Center account for it, or pass `--seller-id` if the game belongs to another seller.
- Sign in with `xbox auth login`, and choose a seller, as in [Getting started](getting-started.md).

A playtest runs in the RETAIL sandbox. If the build uses XBOX sign-in or other XBOX services,
follow [Use XBOX services in a playtest](xbox-services-in-a-playtest.md) first.

The examples use the Store ID `9N0000000000`. Find your game's Store ID with
`xbox game list --output table`.

## 1. Upload a package

The CLI doesn't upload packages. Upload the build with `makepkg` from the October 2026 XBOX
Game Development Kit (GDK) or later. For a PC game whose build is in the `layout` folder:

```text
makepkg upload /d layout /msixvc2 /pc /storeid 9N0000000000 /branch Main
```

The [makepkg reference](https://learn.microsoft.com/gaming/gdk/docs/features/common/packaging/deployment/makepkg)
covers the other package formats. Partner Center then processes the package, which can take a
while. To check on it:

```bash
xbox package list --store-id 9N0000000000 --output table
```

The package is ready when `STATE` is `Processed` and `PLAYTEST` is `true`. Note its `PACKAGE ID`.
The table doesn't show validation results, so check in JSON that its `validationItems` contain no
item with severity `Error`:

```bash
xbox package list --store-id 9N0000000000
```

If `STATE` is `ProcessFailed`, `PLAYTEST` stays `false`, or there's an `Error` item, check the
package requirements in [Prerequisites for a playtest](https://learn.microsoft.com/gaming/game-publishing/concepts/playtest-preview#prerequisites-for-a-playtest).

## 2. Create an audience

An audience decides who can install the playtest. Partner Center calls it a customer group. In
Partner Center's **Apps and games** workspace, select **Customer groups**, then
**Create new group**. Choose **Known user group**, add your testers, and save it.

Or list your testers' email addresses in `testers.txt`, one per line, and create it with the
Experimental [`audience create`](commands/audience.md#creating-an-audience) command:

```bash
xbox audience create --name "Nod Arena testers" --members-file testers.txt
```

Note the audience's `id` for when you add it to the playtest.

## 3. Create the playtest

If you created the audience with `audience create`, use the `id` it printed. If you created the
group in Partner Center, find its `ID`:

```bash
xbox audience list --output table
```

If `xbox audience list` times out, a [known issue](known-issues.md#xbox-audience-list-can-time-out),
leave out `--audience-id` when you create the playtest. Afterward, in Partner Center, open the
playtest from the game's Playtest page under **Services**, choose the group from step 2, and save
it without publishing.

Create the playtest:

```bash
xbox playtest create --store-id 9N0000000000 --name "Nod Arena playtest" --start 2026-12-01 --end 2026-12-15 --pc-package-id 11111111-2222-3333-4444-555555555555 --audience-id 1152921504606846976
```

- `--pc-package-id` takes the package ID from step 1. Use `--console-package-id` for a console
  package.
- `--audience-id` takes the audience's ID. Repeat it to add more audiences.
- Testers can install the playtest from `--start` until `--end`. A date without a time means
  midnight UTC.

The command asks before it creates the playtest, then prints it as JSON. Note the playtest's ID,
which is `playtestId` under `details`.

If the command exits with `3`, it couldn't tell whether the playtest was created. Retry with the
command it prints, which won't create a second playtest.

## 4. Publish it

Publish the playtest with the ID from step 3:

```bash
xbox playtest publish 11111111-2222-3333-4444-555555555555 --store-id 9N0000000000
```

The command waits until the playtest is `Live`, or `Scheduled` if its start date hasn't come yet,
and exits with `0`. Other exit codes mean the playtest isn't published yet:

| Exit code | Meaning |
| --- | --- |
| 1 | Partner Center's checks failed, for example because the playtest has no package, audience, or age ratings. Nothing is published. Fix the checks the command lists, and publish again. |
| 2 | The publish failed. |
| 3 | The outcome is unknown. Check the playtest with `xbox playtest get` before you publish again. |
| 4 | The playtest is still publishing after 5 minutes. Check on it later with `xbox playtest get`. |

See [Publishing a playtest](commands/playtest.md#publishing-a-playtest).

## 5. Share it

Once the playtest is live, testers in its audience can find it in their library:

- On a console, in **Full Library**, under the **Playtest** filter.
- On a PC, in the XBOX app's **Library**, under the **Access** filter.

Newly added testers need to sign out and back in first.

You can also send testers a link. On the game's Playtest page in Partner Center, select
**Share** on the playtest, and copy the link, which starts with `msxbox://playtest/`. The CLI can't
show this link yet. It works only for people in the playtest's audience, and on a PC it opens the
playtest in the XBOX app. See
[Share a playtest with testers](https://learn.microsoft.com/gaming/game-publishing/concepts/playtest-preview#share-a-playtest-with-testers).

## Scripts and continuous integration

You can upload packages from continuous integration (CI) pipelines with a service principal. See
[Uploading packages](commands/package.md#uploading-packages). The `xbox package` and
`xbox playtest` commands don't accept a service principal yet, so they need a signed-in user. See
[Automation (service principal) authentication](authentication.md#automation-service-principal-authentication).

In a script that runs while you're signed in, pass `--confirm` to `playtest create` and
`playtest publish`, because without a terminal they can't ask you, and make no change. Read the
JSON they print, and check their exit codes. To wait longer than 5 minutes for a publish, pass
`--timeout`.
