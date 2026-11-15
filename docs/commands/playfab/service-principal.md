---
title: Service principal
description: Grant, inspect, and revoke an Entra service principal's PlayFab access to a studio and its titles.
---

# Service principal

The `playfab service-principal` command family is Experimental. Its CLI interface might
change or be removed based on feedback.

A continuous integration (CI) pipeline or other unattended job signs in as a Microsoft
Entra ID service principal; see
[Automation (service principal) authentication](../../authentication.md#automation-service-principal-authentication).
PlayFab accepts the service principal only after a person grants it access in a PlayFab
studio. These commands manage those grants.

Only a signed-in person with **Admin** permission on the studio can run these commands. They
refuse to run when `XBOX_AUTHENTICATION__MODE` selects a service-principal mode, so sign in
with `xbox auth login` instead.

| Command | PlayFab API path | Description |
| --- | --- | --- |
| `playfab service-principal list` | `/Studio/ListServicePrincipals` | List the service principals registered in a studio. |
| `playfab service-principal get` | `/Studio/GetServicePrincipalAccess` | Show one service principal's grants. |
| `playfab service-principal set` | `/Studio/SetServicePrincipalAccess` | Register a service principal if needed, and **replace** all of its grants. |
| `playfab service-principal revoke` | `/Studio/DeleteServicePrincipalAccess` | Remove a service principal and all of its grants from a studio. Asks first; see [Safety and destructive operations](../../safety.md). |

| Command | Options |
| --- | --- |
| `service-principal list` | `--studio-id` (required), `--limit` (1-100), `--continuation-token`. |
| `service-principal get` | `--studio-id`, `--principal-tenant-id`, `--principal-object-id` (all required). |
| `service-principal set` | `--studio-id`, `--principal-tenant-id`, `--principal-object-id`, `--display-name`, `--grants-file` (all required), `--expected-version`. |
| `service-principal revoke` | `--studio-id`, `--principal-tenant-id`, `--principal-object-id` (all required). |

`list` accepts `--output table`; the other commands don't. `list` returns one page: when
more exist, the response's `ContinuationToken` is set, and running `list` again with
`--continuation-token` set to it gets the next page.

## Identifiers

- `--studio-id` names the studio that owns the grants. These commands don't take a
  PlayFab title, so `--playfab-title-id` and saved context don't apply.
- `--principal-tenant-id` and `--principal-object-id` identify the service principal: its
  Entra ID tenant, and the object ID of its enterprise application, not of its app
  registration. `xbox auth status` reports it as `objectId` when signed in as the service
  principal.
- You don't pass the application (client) ID. PlayFab records it from the service
  principal's first token, and `list` and `get` report it as `ClientId`.
- The global `--tenant-id` and `--client-id` options configure your own sign-in, not the
  service principal you're granting access to.

## Grants file

> [!WARNING]
> `set` replaces all of the service principal's grants with the contents of
> `--grants-file`, and removes any studio permission or title the file leaves out.

Start from the current grants rather than writing a file from scratch. Save the output of
`get`, edit its `StudioPermissions` and `TitleGrants`, and pass it to `set`, with its
`Version` as `--expected-version`:

```bash
xbox playfab service-principal get \
  --studio-id 1234567890ABCDEF \
  --principal-tenant-id 22222222-3333-4444-5555-666666666666 \
  --principal-object-id 33333333-4444-5555-6666-777777777777 \
  > grants.json
```

A grants file can be the whole output of `get`, its `data`, the `ServicePrincipal` object
inside it, or only the grants:

```json
{
  "StudioPermissions": [],
  "TitleGrants": [
    { "TitleId": "ABC123", "Permissions": ["Admin"] }
  ]
}
```

- Both properties are required. An empty list grants nothing of that kind.
- Each title needs at least one permission. To remove a title's grant, leave the title out.
- Property names aren't case-sensitive. An unrecognized property is an error, so a
  misspelled `TitleGrants` can't remove every title grant.
- The file's `TenantId`, `ObjectId`, and `DisplayName`, when present, must match
  `--principal-tenant-id`, `--principal-object-id`, and `--display-name`. To rename the
  service principal, change `DisplayName` in the file too, or remove it.

### Expected version

Every change to a service principal's grants gives them a new `Version`, which `get` and
`set` return. With `--expected-version`, `set` changes nothing unless the grants are still
at that version, so it can't undo a change someone made after you read them.
`--expected-version 0` requires that the service principal has no access yet. Without
`--expected-version`, `set` replaces the grants whatever they are now.

When the grants file has a `Version`, `--expected-version` is required and must equal it.
If they differ, run `get` again and redo your edits on its output. To replace the grants
regardless, remove `Version` from the file and leave out `--expected-version`.

### If `set` fails

> [!WARNING]
> If `set` fails, the service principal might have no access in the studio until a `set`
> succeeds, which can cut off a pipeline.

A failure doesn't say whether the grants changed, so don't run `set` again with the old
version. Run `get`, check its grants and `Version`, and if they aren't what you meant to
set, edit that output and pass it to `set` with its `Version` as `--expected-version`.

## Examples

List a studio's service principals, and show one's grants:

```bash
xbox playfab service-principal list \
  --studio-id 1234567890ABCDEF

xbox playfab service-principal get \
  --studio-id 1234567890ABCDEF \
  --principal-tenant-id 22222222-3333-4444-5555-666666666666 \
  --principal-object-id 33333333-4444-5555-6666-777777777777
```

Replace the service principal's grants with those in `grants.json`, which you saved from
`get` and edited:

```bash
xbox playfab service-principal set \
  --studio-id 1234567890ABCDEF \
  --principal-tenant-id 22222222-3333-4444-5555-666666666666 \
  --principal-object-id 33333333-4444-5555-6666-777777777777 \
  --display-name "Nod Publishers build" \
  --grants-file grants.json \
  --expected-version 3
```

Remove the service principal and its grants from the studio:

```bash
xbox playfab service-principal revoke \
  --studio-id 1234567890ABCDEF \
  --principal-tenant-id 22222222-3333-4444-5555-666666666666 \
  --principal-object-id 33333333-4444-5555-6666-777777777777 \
  --confirm
```

After `revoke`, the service principal's next PlayFab request in the studio fails. `revoke`
also succeeds when the service principal already has no access. To restore access, run
`set` with the complete grant set.
