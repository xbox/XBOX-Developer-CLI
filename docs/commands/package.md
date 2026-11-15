---
title: Package
description: List and get the packages of a Partner Center game, check whether a build can be used in a playtest, and find how to upload one.
---

# Package

A package is a build of a game that Partner Center has received and processed. The `package`
commands list and get a game's packages, so you can check whether one is ready for a
[playtest](playtest.md). To upload a package, see [Uploading packages](#uploading-packages).

These commands are Experimental: their options and output might change. See
[Experimental commands](../cli-command-lifecycle.md). They need Partner Center's Packages Read
permission on the game, which the Developer role includes.

## Commands

| Command | Description |
| --- | --- |
| `package list` | List the packages on the game's Main branch. |
| `package get <package-id>` | Get one package. |

## Arguments and options

| Argument or option | Applies to | Required | Description |
| --- | --- | --- | --- |
| `<package-id>` | `get` | Yes | Package ID, from `package list`. |
| `--store-id <id>` | all | One of the two | Store ID of the game, such as `9N0000000000`. |
| `--xbox-title-id <id>` | all | One of the two | XBOX title ID of the game, not its PlayFab title ID, such as `1234ABCD`. When the title ID has several games, the command asks for `--store-id`. |
| `--seller-id <id>` | all | No | Partner Center seller ID. |
| `--base-url <url>` | all | No | Base URL for requests, overriding the `XboxPartner:BaseUrl` configuration key. |
| `--output <format>` | all | No | `json` (default), `json-compact`, or `table`. |

The seller comes from `--seller-id` or your saved seller; see [Context](context.md). Without
one, the commands use your only seller, and ask for `--seller-id` when you belong to several.
`--xbox-title-id` always needs a seller.

## Listing packages

```bash
xbox package list --store-id 9N0000000000 --output json-compact
```

`package list` writes the ID of the game's Main branch and a `packages` array with every field
the service returns. `package get` writes one package in the same shape. An excerpt:

```json
{
  "packageSetId": "22222222-3333-4444-5555-666666666666",
  "packages": [
    {
      "id": "11111111-2222-3333-4444-555555555555",
      "version": "1.0.0.0",
      "platform": "PC",
      "packageType": "Msixvc",
      "processingState": "Processed",
      "isAvailableForPlaytest": true,
      "validationItems": [],
      "fileName": "Nod.Arena_1.0.0.0_x64.msixvc",
      "createdAt": "2026-11-04T18:00:00Z"
    }
  ]
}
```

A package is ready for a playtest when all of these are true:

- `processingState` is `Processed`. It moves from `PendingUpload` through `Uploaded` and
  `Processing` to `Processed`, or to `ProcessFailed`.
- `validationItems`, the problems Partner Center found while processing the package, has no item
  with severity `Error`.
- `isAvailableForPlaytest` is `true`.

`--output table` has `PACKAGE ID`, `VERSION`, `PLATFORM`, `STATE`, `PLAYTEST`, and `CREATED`
columns. It doesn't show validation results, so check `validationItems` in JSON.

## Uploading packages

The CLI doesn't upload packages. Upload them with `makepkg` from the XBOX Game Development Kit
(GDK); see the
[makepkg reference](https://learn.microsoft.com/gaming/gdk/docs/features/common/packaging/deployment/makepkg),
and for uploads from continuous integration (CI),
[Package Uploader](https://learn.microsoft.com/gaming/gdk/docs/features/common/packaging/package-uploader).
Then run `package list` to check that the package is ready.

## Errors

Errors go to stderr as text, whatever `--output` is, with the correlation ID. `--verbose` adds
the response body. A failure exits `1`.

| Error | What to do |
| --- | --- |
| HTTP 401 | Sign in again with `xbox auth login`. |
| HTTP 403, `package_permission_denied` | You don't have Packages Read permission on the game for this seller. Ask a manager of the Partner Center account for the Developer role, or pass `--seller-id` if the game belongs to another seller. |
| HTTP 403, `identity_override_not_authorized` | You don't belong to that seller. Check the seller, and find yours with `xbox seller list`. |
| HTTP 400, `seller_selection_required` | You belong to several sellers. Pass `--seller-id`. |
| HTTP 404, `game_not_found` | The seller has no game with that Store ID. Check `--store-id` and `--seller-id`. |

## Examples

```bash
xbox package list --store-id 9N0000000000
xbox package list --xbox-title-id 1234ABCD --seller-id 1234567 --output table
xbox package get 11111111-2222-3333-4444-555555555555 --store-id 9N0000000000
```
