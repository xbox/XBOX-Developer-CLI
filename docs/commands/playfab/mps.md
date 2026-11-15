---
title: Multiplayer Servers
description: Manage PlayFab Multiplayer Servers, including builds, servers, assets, container images, and quotas.
---

# Multiplayer Servers

The `playfab mps` command family is Experimental. Its CLI interface might change
or be removed based on feedback.

`playfab mps` manages PlayFab Multiplayer Servers (MPS): builds and their aliases and
regions, servers, assets, certificates, secrets, container images, quotas, and virtual
machines (VMs). It covers the same operations as PlayFab's
[MpsPowershell](https://github.com/PlayFab/MpsPowershell) module; the equivalent of its
`Set-PfTitle` is `xbox context set playfab-title-id <id>`. See [Context](../context.md).

```bash
xbox --playfab-title-id ABC123 --output json-compact --data-only playfab mps build list
```

## Command tree

Each command runs under `xbox playfab mps`.

| Command | PlayFab API path | Description |
| --- | --- | --- |
| `title enable` | `/MultiplayerServer/EnableMultiplayerServersForTitle` | Enable MPS for the title. |
| `title status` | `/MultiplayerServer/GetTitleEnabledForMultiplayerServersStatus` | Check whether MPS is enabled for the title. |
| `build get` | `/MultiplayerServer/GetBuild` | Get a build. |
| `build list` | `/MultiplayerServer/ListBuildSummariesV2` | List one page of builds. |
| `build create` | Depends on `--type` | Create a build. See [`mps build create`](#mps-build-create). |
| `build update-name` | `/MultiplayerServer/UpdateBuildName` | Rename a build. |
| `build delete` | `/MultiplayerServer/DeleteBuild` | Delete a build. |
| `build alias get` | `/MultiplayerServer/GetBuildAlias` | Get a build alias. |
| `build alias list` | `/MultiplayerServer/ListBuildAliases` | List one page of build aliases. |
| `build alias create` | `/MultiplayerServer/CreateBuildAlias` | Create a build alias. |
| `build alias update` | `/MultiplayerServer/UpdateBuildAlias` | Update a build alias. |
| `build alias delete` | `/MultiplayerServer/DeleteBuildAlias` | Delete a build alias. |
| `build region update` | `/MultiplayerServer/UpdateBuildRegion` | Update one of a build's regions. |
| `build region update-all` | `/MultiplayerServer/UpdateBuildRegions` | Update all of a build's regions. |
| `build region delete` | `/MultiplayerServer/DeleteBuildRegion` | Remove a region from a build. |
| `server get` | `/MultiplayerServer/GetMultiplayerServerDetails` | Get a server session's details. |
| `server list` | `/MultiplayerServer/ListMultiplayerServers` | List one page of a build's server sessions in a region. |
| `server list-archived` | `/MultiplayerServer/ListArchivedMultiplayerServers` | List one page of a build's archived server sessions. |
| `server request` | `/MultiplayerServer/RequestMultiplayerServer` | Request a server session. |
| `server shutdown` | `/MultiplayerServer/ShutdownMultiplayerServer` | Shut down a server session. |
| `server get-logs` | `/MultiplayerServer/GetMultiplayerServerLogs` | Get a terminated server's logs. |
| `server get-session-logs` | `/MultiplayerServer/GetMultiplayerSessionLogsBySessionId` | Get a terminated server's logs by session ID. |
| `asset list` | `/MultiplayerServer/ListAssetSummaries` | List one page of assets. |
| `asset upload` | `/MultiplayerServer/GetAssetUploadUrl` | Upload a local file as an asset. See [`mps asset upload`](#mps-asset-upload). |
| `asset delete` | `/MultiplayerServer/DeleteAsset` | Delete an asset. |
| `asset get-upload-url` | `/MultiplayerServer/GetAssetUploadUrl` | Get a URL to upload an asset to. |
| `asset get-download-url` | `/MultiplayerServer/GetAssetDownloadUrl` | Get a URL to download an asset from. |
| `certificate list` | `/MultiplayerServer/ListCertificateSummaries` | List one page of certificates. |
| `certificate upload` | `/MultiplayerServer/UploadCertificate` | Upload a certificate. See [`mps certificate upload`](#mps-certificate-upload-and-mps-secret-upload). |
| `certificate delete` | `/MultiplayerServer/DeleteCertificate` | Delete a certificate. |
| `secret list` | `/MultiplayerServer/ListSecretSummaries` | List one page of secrets. |
| `secret upload` | `/MultiplayerServer/UploadSecret` | Upload a secret. See [`mps secret upload`](#mps-certificate-upload-and-mps-secret-upload). |
| `secret delete` | `/MultiplayerServer/DeleteSecret` | Delete a secret. |
| `container-image list` | `/MultiplayerServer/ListContainerImages` | List one page of custom container images. |
| `container-image list-tags` | `/MultiplayerServer/ListContainerImageTags` | List one page of a container image's tags. |
| `container-image untag` | `/MultiplayerServer/UntagContainerImage` | Remove a tag from a container image. |
| `container-image repository delete` | `/MultiplayerServer/DeleteContainerImageRepository` | Delete a container image repository. |
| `container-registry credentials get` | `/MultiplayerServer/GetContainerRegistryCredentials` | Get the container registry's credentials. |
| `container-registry credentials rollover` | `/MultiplayerServer/RolloverContainerRegistryCredentials` | Replace the container registry's credentials. |
| `quota get` | `/MultiplayerServer/GetTitleMultiplayerServersQuotas` | Get the title's MPS quotas. |
| `quota change get` | `/MultiplayerServer/GetTitleMultiplayerServersQuotaChange` | Get a quota change request. |
| `quota change list` | `/MultiplayerServer/ListTitleMultiplayerServersQuotaChanges` | List all quota change requests. |
| `quota change create` | `/MultiplayerServer/CreateTitleMultiplayerServersQuotaChange` | Request a quota change. |
| `qos-server list` | `/MultiplayerServer/ListQosServersForTitle` | List the title's QoS servers. |
| `party-qos-server list` | `/MultiplayerServer/ListPartyQosServers` | List PlayFab Party QoS servers. |
| `virtual-machine list` | `/MultiplayerServer/ListVirtualMachineSummaries` | List one page of VMs. |
| `remote-user create` | `/MultiplayerServer/CreateRemoteUser` | Create a remote user for signing in to a build's VM. |
| `remote-user delete` | `/MultiplayerServer/DeleteRemoteUser` | Delete a remote user. |
| `remote-login get-endpoint` | `/MultiplayerServer/GetRemoteLoginEndpoint` | Get the remote sign-in endpoint of a VM hosting a build. |
| `party request` | `/Party/RequestPartyService` | Request a PlayFab Party session. |

## Request body

Most MPS commands send the operation's JSON request object, like
[`playfab api`](api.md):

| Option | Description |
| --- | --- |
| `--body` | The JSON request body, inline. Defaults to `{}` when neither body option is passed. |
| `--body-file` | Path to a file holding the JSON request body, or `-` to read it from stdin. |

`--body` and `--body-file` can't be combined. `certificate upload` and `secret upload` take
only `--body-file`, and `asset upload` takes neither; see their sections.

```bash
xbox --playfab-title-id ABC123 playfab mps build get --body '{ "BuildId": "11111111-2222-3333-4444-555555555555" }'
xbox --playfab-title-id ABC123 --data-only playfab mps server list --body '{ "BuildId": "11111111-2222-3333-4444-555555555555", "Region": "EastUs" }'
```

## Destructive commands

These commands ask first in a terminal, and need `--confirm` (`--yes`, `-y`) elsewhere:
`build delete`, `build alias delete`, `build region delete`, `server shutdown`,
`asset delete`, `certificate delete`, `secret delete`,
`container-image repository delete`, and `remote-user delete`. With `--body-file -`, they
need `--confirm` even in a terminal. Other MPS commands that change things run without
asking. See [Safety and destructive operations](../../safety.md).

```bash
xbox --playfab-title-id ABC123 playfab mps build delete --body '{ "BuildId": "11111111-2222-3333-4444-555555555555" }' --confirm
```

## Paging

These list commands return one page of results per call:

- `build list`, `build alias list`
- `server list`, `server list-archived`
- `asset list`, `certificate list`, `secret list`
- `container-image list`, `container-image list-tags`
- `virtual-machine list`

When more results exist, the response's `SkipToken` is set. To get the next page, add
`"SkipToken"` with that value to the request body, keep its other fields, and repeat until
`SkipToken` comes back `null`. Set `"PageSize"` in the request body to change how many
results a page holds.

Get the first page of builds:

```bash
xbox --playfab-title-id ABC123 --data-only playfab mps build list --body '{ "PageSize": 50 }'
```

If its `SkipToken` isn't `null`, get the next page by passing that token:

```bash
xbox --playfab-title-id ABC123 --data-only playfab mps build list --body '{ "PageSize": 50, "SkipToken": "<SkipToken from the previous page>" }'
```

Table output has no column for `SkipToken`, so when more results exist, the table is
followed by a notice on stderr with the token to send.

## Table output

The commands in these tables accept `--output table`; other MPS commands don't. See
[Table output](../../commands.md#table-output).

| Command | Columns |
| --- | --- |
| `build list` | `BUILD ID`, `NAME`, `CREATED` |
| `build alias list` | `ALIAS ID`, `NAME` |
| `server list` | `SERVER ID`, `REGION`, `STATE`, `SESSION ID` |
| `server list-archived` | `SERVER ID`, `REGION`, `STATE`, `LAST TRANSITION` |
| `asset list` | `FILE NAME` |
| `certificate list` | `NAME`, `THUMBPRINT` |
| `secret list` | `NAME`, `VERSION`, `EXPIRES` |
| `container-image list` | `IMAGE` |
| `container-image list-tags` | `TAG` |
| `quota change list` | `REQUEST ID`, `DESCRIPTION`, `PENDING`, `APPROVED` |
| `qos-server list` | `REGION`, `SERVER URL` |
| `party-qos-server list` | `REGION`, `SERVER URL` |
| `virtual-machine list` | `VM ID`, `STATE`, `HEALTH` |

Commands that return a single record show it as one row:

| Command | Columns |
| --- | --- |
| `title status` | `STATUS` |
| `build get` | `BUILD ID`, `NAME`, `STATUS`, `OS`, `VM SIZE` |
| `build alias get` | `ALIAS ID`, `NAME` |
| `server get` | `SERVER ID`, `STATE`, `REGION`, `SESSION ID`, `IPV4 ADDRESS` |
| `asset get-upload-url` | `FILE NAME`, `UPLOAD URL` (redacted) |
| `asset get-download-url` | `FILE NAME`, `DOWNLOAD URL` (redacted) |
| `container-registry credentials get` | `DNS NAME`, `USERNAME`, `PASSWORD` (redacted) |
| `quota change get` | `REQUEST ID`, `DESCRIPTION`, `PENDING`, `APPROVED` |
| `remote-login get-endpoint` | `IPV4 ADDRESS`, `PORT` |

Redacted columns print `********`, because the value is a credential. JSON output returns
the real value.

```bash
xbox --playfab-title-id ABC123 playfab mps build list --output table
```

```text
BUILD ID                              NAME            CREATED
------------------------------------  --------------  ------------------------
11111111-2222-3333-4444-555555555555  combat-sandbox  2026-04-26T22:15:16.7Z
22222222-3333-4444-5555-666666666666  lobby-service   2026-06-23T22:00:37.702Z
```

## `mps build create`

`build create` requires `--type`, which selects the PlayFab API it calls, and takes the
request body like other MPS commands:

| `--type` value | PlayFab API path |
| --- | --- |
| `managed` | `/MultiplayerServer/CreateBuildWithManagedContainer` |
| `custom` | `/MultiplayerServer/CreateBuildWithCustomContainer` |
| `process` | `/MultiplayerServer/CreateBuildWithProcessBasedServer` |

```bash
xbox --playfab-title-id ABC123 playfab mps build create --type managed --body-file managed-build.json
```

## `mps certificate upload` and `mps secret upload`

`certificate upload` and `secret upload` take their request body only with
`--body-file <path>`, or `--body-file -` for stdin, because it holds a PFX password or a
secret value. They refuse `--body`, which would leave the value in shell history and
process listings. Neither accepts `--output table`.

| Command | Request fields |
| --- | --- |
| `certificate upload` | `GameCertificate` (`Name`, `Base64EncodedValue`, and `Password` if the PFX has one), `ForceUpdate`, `CustomTags` |
| `secret upload` | `GameSecret` (`Name`, `Value`, `ExpirationDate`), `ForceUpdate`, `CustomTags` |

`ForceUpdate` replaces a certificate or secret that already has the name. Keep the body
file out of source control, and delete it once it's uploaded.

`certificate.json` holds the Base64-encoded PFX and its password:

```json
{
  "GameCertificate": {
    "Name": "nod-arena-tls",
    "Base64EncodedValue": "<Base64-encoded PFX>",
    "Password": "<PFX password>"
  }
}
```

```bash
xbox --playfab-title-id ABC123 playfab mps certificate upload --body-file certificate.json
```

To build the body without writing the secret to another file, pipe it to `--body-file -`.
Here, `jq` reads the value from `api-key.txt` rather than from its own arguments, so the
value stays out of process listings:

```bash
jq -n --rawfile value api-key.txt \
  '{GameSecret: {Name: "nod-arena-api-key", Value: ($value | rtrimstr("\n"))}}' \
  | xbox --playfab-title-id ABC123 playfab mps secret upload --body-file -
```

## `mps asset upload`

`asset upload` uploads a local file as a multiplayer server asset. It takes options rather
than a request body, and writes the asset name and upload status as JSON.

| Option | Required | Description |
| --- | --- | --- |
| `--file` | Yes | Path to the local file to upload. |
| `--asset-name` | Yes | Name of the asset to create in PlayFab. |
| `--buffer-size` | No | Upload block size in bytes. Defaults to `4194304` (4 MiB). |

```bash
xbox --playfab-title-id ABC123 playfab mps asset upload --file gameserver.zip --asset-name gameserver.zip
```
