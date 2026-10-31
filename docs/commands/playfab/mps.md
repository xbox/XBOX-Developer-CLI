---
title: Multiplayer Servers
description: Manage PlayFab Multiplayer Servers, including builds, servers, assets, container images, and quotas.
---

# Multiplayer Servers

The `playfab mps` command family is Experimental. Its CLI interface may change
or be removed based on feedback.

`playfab mps` exposes PlayFab Multiplayer Server commands using a noun → verb
structure. This page owns the complete MPS command tree and detailed examples. The high-level map is:

| Area | Example commands | Notes |
| --- | --- | --- |
| Title | `title enable`, `title status` | Enable MPS for a title or check enablement status. |
| Builds | `build list`, `build get`, `build create`, `build update-name`, `build delete` | `build create` requires `--type managed`, `--type custom`, or `--type process`. |
| Build aliases and regions | `build alias list`, `build alias create`, `build region update` | Manage build routing and regional configuration. |
| Servers | `server list`, `server get`, `server request`, `server shutdown`, `server get-logs` | Inspect, allocate, shut down, and retrieve logs for server sessions. |
| Assets, certificates, and secrets | `asset list`, `asset upload`, `certificate upload`, `secret upload` | Manage files and credentials used by MPS builds. |
| Container images and registry | `container-image list`, `container-image repository delete`, `container-registry credentials get` | Inspect or manage container image resources and registry credentials. |
| Quotas | `quota get`, `quota change list`, `quota change create` | View and request changes to multiplayer server quotas. |
| QoS, virtual machines, and remote access | `qos-server list`, `party-qos-server list`, `virtual-machine list`, `remote-login get-endpoint` | Operational lookup and VM access helpers. |
| Party | `party request` | Request a PlayFab Party session. |

Most MPS leaf commands accept `--body` or `--body-file`, like `playfab api`.
`certificate upload` and `secret upload` accept only `--body-file`, because
their request body holds a credential. The special `playfab mps asset upload`
command accepts `--file`, `--asset-name`, and optional `--buffer-size` instead.
The detailed reference follows below.

The `xbox` binary exposes the PlayFab Multiplayer Server (MPS) APIs through the
`playfab mps` command group. The commands use a noun → verb structure, such as
`mps build list`, `mps build alias create`, and `mps server shutdown`.

This command group covers the surface of the
[MpsPowershell](https://github.com/PlayFab/MpsPowershell) module. Its `Set-PfTitle` equivalent is
[`xbox context set playfab-title-id <id>`](../context.md): sign in once with
`xbox auth login`, then save the title, set `XBOX_PLAYFAB_TITLE_ID`, or pass
`--playfab-title-id` to a single command. See [Commands](../../commands.md) and
[PlayFab API Integration](../../playfab-api.md) for the shared command model.

## Global output and request options

Most API-backed `playfab mps` commands route through the same PlayFab API
invocation path as `playfab api`, so they share its configuration, output,
and confirmation behavior.

`mps asset upload` is the exception: it resolves an upload URL through
`GetAssetUploadUrl` and then streams the file to that URL through its own client
rather than the shared invocation path. Its Help classifies it as mutating for
that reason — the operation name alone would report a write as a read.

```bash
xbox --playfab-title-id ABC123 --output json-compact --data-only playfab mps build list
```

Useful global options include:

| Option | Meaning |
| --- | --- |
| `--playfab-title-id` | Selects the PlayFab title to target. |
| `--output`, `-o` | `json` (default) for pretty JSON, `json-compact` for single-line JSON, or `table` on the commands listed in the **Table output** section below. |
| `--data-only` | Emits only the PlayFab `data` payload instead of the full envelope. |
| `--confirm`, `--yes`, `-y` | Confirms a catastrophic destructive operation such as a `Delete*` or `Shutdown*` API. |
| `--verbose` | Writes diagnostic details to stderr without dumping request-body contents. |

Every MPS command except `asset upload` accepts these request-body options.
[`certificate upload` and `secret upload`](#mps-certificate-upload-and-mps-secret-upload)
accept only `--body-file`.

| Option | Meaning |
| --- | --- |
| `--body` | Inline JSON request body. Defaults to `{}` if neither body option is provided. |
| `--body-file` | Path to a file containing the JSON request body, or `-` to read UTF-8 from stdin until EOF. |

`--body` and `--body-file` are mutually exclusive.
An explicitly supplied body, including empty or whitespace-only content, is
sent unchanged; only omitting both options defaults to `{}`. Stdin supplies
one complete body and uses the same request-size limit and cancellation as
[`playfab api`](api.md#read-the-request-body-from-stdin).
Destructive operations require `--confirm` when the body comes from stdin.

```bash
xbox --playfab-title-id ABC123 playfab mps build get --body '{ "BuildId": "11111111-2222-3333-4444-555555555555" }'

xbox --playfab-title-id ABC123 --data-only playfab mps server list --body '{ "BuildId": "11111111-2222-3333-4444-555555555555", "Region": "EastUs" }'
```

> [!WARNING]
> MPS commands can affect production multiplayer infrastructure. Only operations
> whose PlayFab operation name starts with `Get`, `List`, or `Search` are
> read-only by convention. See [Safety and destructive operations](../../safety.md) before
> running `Create*`, `Update*`, `Delete*`, `Request*`, `Shutdown*`, or other
> mutating operations.

## Paging

These list commands return one page of results per call, not necessarily all of
them:

- `build list`, `build alias list`
- `server list`, `server list-archived`
- `asset list`, `certificate list`, `secret list`
- `container-image list`, `container-image list-tags`
- `virtual-machine list`

When more results exist, the response's `SkipToken` is set. To get the next
page, add `"SkipToken"` with that value to the request body, keep the body's
other fields, and repeat until `SkipToken` comes back `null`. Set `"PageSize"`
in the request body to change how many results a page holds. Each command's Help
says the same under a `PAGING` heading.

```bash
xbox --playfab-title-id ABC123 --data-only playfab mps build list --body '{ "PageSize": 50 }'

xbox --playfab-title-id ABC123 --data-only playfab mps build list --body '{ "PageSize": 50, "SkipToken": "<SkipToken from the previous page>" }'
```

JSON output includes `SkipToken` as the service returned it. Table output has no
column for it, so when more results exist the table is followed by a notice on
stderr with the token to send:

```console
$ xbox --playfab-title-id ABC123 playfab mps build list --output table
BUILD ID  NAME            CREATED
--------  --------------  ------------------------
ABC123    combat-sandbox  2026-04-26T22:15:16.7Z
More results exist; this table shows one page. To get the next page, add "SkipToken": "<token>" to the request body, keeping its other fields. Set "PageSize" there to change how many results a page holds.
```

`quota change list` takes no paging fields and returns its results in one
response.

## Table output

`--output table` is opt-in per command. An MPS command that declares a table
view accepts it; every other MPS command refuses `table` rather than quietly
returning JSON:

```console
$ xbox --playfab-title-id ABC123 playfab mps quota get --output table
Output format 'table' is not available for this command, because it does not declare a
table view. Omit --output to get this command's default output.
```

Each command's own Help lists its columns under a `TABLE OUTPUT` heading. The
columns are a human convenience and **not a stability contract** — use `json` or
`json-compact` for scripts and agents.

Commands returning a collection render one row per record:

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

Commands returning a single record render a one-row table with the same headers
and separator:

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

```bash
xbox --playfab-title-id ABC123 playfab mps build list --output table
```

```text
BUILD ID                              NAME            CREATED
------------------------------------  --------------  ------------------------
11111111-2222-3333-4444-555555555555  combat-sandbox  2026-04-26T22:15:16.7Z
22222222-3333-4444-5555-666666666666  lobby-service   2026-06-23T22:00:37.702Z
```

A collection view prints `No results.` when the collection is empty, rather than
headers with no rows. A paged command's table is followed by a notice on stderr
when more results exist; see [Paging](#paging).

The three redacted columns print `********`. A registry password is a reusable
credential, and a pre-authenticated asset URL carries its own signature, so it is
a bearer credential too; a table is the format that ends up in screenshots and
terminal scrollback. The JSON formats — including the default — still return the
real values, so scripting a `docker login` or an asset upload is unaffected.

`--data-only` has no effect with `--output table`: a table has no response
envelope to strip.

`quota get` deliberately has **no** view. Its response is a single `Quotas`
object wrapping a `CoreCapacities` array, so a flat table would have to drop the
capacities — the part worth reading. It stays JSON instead.


## Command tree

Each row shows the command under `xbox playfab mps`
and the verified PlayFab API path it calls.

| Command | PlayFab API path | Description |
| --- | --- | --- |
| `title enable` | `/MultiplayerServer/EnableMultiplayerServersForTitle` | Enables the multiplayer server feature for a title. |
| `title status` | `/MultiplayerServer/GetTitleEnabledForMultiplayerServersStatus` | Gets whether a title is enabled for the multiplayer server feature. |
| `build get` | `/MultiplayerServer/GetBuild` | Gets a multiplayer server build. |
| `build list` | `/MultiplayerServer/ListBuildSummariesV2` | Lists one page of summarized details of multiplayer server builds for a title. |
| `build update-name` | `/MultiplayerServer/UpdateBuildName` | Updates a multiplayer server build's name. |
| `build delete` | `/MultiplayerServer/DeleteBuild` | Deletes a multiplayer server build. |
| `build alias get` | `/MultiplayerServer/GetBuildAlias` | Gets a multiplayer server build alias. |
| `build alias list` | `/MultiplayerServer/ListBuildAliases` | Lists one page of build alias details for a title. |
| `build alias create` | `/MultiplayerServer/CreateBuildAlias` | Creates a multiplayer server build alias. |
| `build alias update` | `/MultiplayerServer/UpdateBuildAlias` | Updates a multiplayer server build alias. |
| `build alias delete` | `/MultiplayerServer/DeleteBuildAlias` | Deletes a multiplayer server build alias. |
| `build region update` | `/MultiplayerServer/UpdateBuildRegion` | Updates a multiplayer server build's region. |
| `build region update-all` | `/MultiplayerServer/UpdateBuildRegions` | Updates all of a multiplayer server build's regions in bulk. |
| `build region delete` | `/MultiplayerServer/DeleteBuildRegion` | Removes a multiplayer server build's region. |
| `server get` | `/MultiplayerServer/GetMultiplayerServerDetails` | Gets multiplayer server session details for a build. |
| `server list` | `/MultiplayerServer/ListMultiplayerServers` | Lists one page of multiplayer server sessions for a build in a region. |
| `server list-archived` | `/MultiplayerServer/ListArchivedMultiplayerServers` | Lists one page of archived multiplayer server sessions for a build. |
| `server request` | `/MultiplayerServer/RequestMultiplayerServer` | Requests a multiplayer server session. |
| `server shutdown` | `/MultiplayerServer/ShutdownMultiplayerServer` | Shuts down a multiplayer server session. |
| `server get-logs` | `/MultiplayerServer/GetMultiplayerServerLogs` | Gets multiplayer server logs after a server has terminated. |
| `server get-session-logs` | `/MultiplayerServer/GetMultiplayerSessionLogsBySessionId` | Gets multiplayer server logs by session ID after a server has terminated. |
| `asset list` | `/MultiplayerServer/ListAssetSummaries` | Lists one page of multiplayer server game assets for a title. |
| `asset delete` | `/MultiplayerServer/DeleteAsset` | Deletes a multiplayer server game asset for a title. |
| `asset get-upload-url` | `/MultiplayerServer/GetAssetUploadUrl` | Gets the URL to upload assets to. |
| `asset get-download-url` | `/MultiplayerServer/GetAssetDownloadUrl` | Gets a URL that can be used to download the specified asset. |
| `certificate list` | `/MultiplayerServer/ListCertificateSummaries` | Lists one page of multiplayer server game certificates for a title. |
| `certificate upload` | `/MultiplayerServer/UploadCertificate` | Uploads a multiplayer server game certificate. |
| `certificate delete` | `/MultiplayerServer/DeleteCertificate` | Deletes a multiplayer server game certificate. |
| `secret list` | `/MultiplayerServer/ListSecretSummaries` | Lists one page of multiplayer server game secrets for a title. |
| `secret upload` | `/MultiplayerServer/UploadSecret` | Uploads a multiplayer server game secret. |
| `secret delete` | `/MultiplayerServer/DeleteSecret` | Deletes a multiplayer server game secret. |
| `container-image list` | `/MultiplayerServer/ListContainerImages` | Lists one page of custom container images for a title. |
| `container-image list-tags` | `/MultiplayerServer/ListContainerImageTags` | Lists one page of the tags for a custom container image. |
| `container-image untag` | `/MultiplayerServer/UntagContainerImage` | Untags a container image. |
| `container-image repository delete` | `/MultiplayerServer/DeleteContainerImageRepository` | Deletes a container image repository. |
| `container-registry credentials get` | `/MultiplayerServer/GetContainerRegistryCredentials` | Gets the credentials to the container registry. |
| `container-registry credentials rollover` | `/MultiplayerServer/RolloverContainerRegistryCredentials` | Rolls over the credentials to the container registry. |
| `quota get` | `/MultiplayerServer/GetTitleMultiplayerServersQuotas` | Gets the quotas for a title in relation to multiplayer servers. |
| `quota change get` | `/MultiplayerServer/GetTitleMultiplayerServersQuotaChange` | Gets a title's server quota change request. |
| `quota change list` | `/MultiplayerServer/ListTitleMultiplayerServersQuotaChanges` | Lists all of a title's server quota change requests. |
| `quota change create` | `/MultiplayerServer/CreateTitleMultiplayerServersQuotaChange` | Creates a request to change a title's multiplayer server quotas. |
| `qos-server list` | `/MultiplayerServer/ListQosServersForTitle` | Lists quality of service servers for the title. |
| `party-qos-server list` | `/MultiplayerServer/ListPartyQosServers` | Lists quality of service servers for party. |
| `virtual-machine list` | `/MultiplayerServer/ListVirtualMachineSummaries` | Lists one page of virtual machines for a title. |
| `remote-user create` | `/MultiplayerServer/CreateRemoteUser` | Creates a remote user to log on to a VM for a multiplayer server build. |
| `remote-user delete` | `/MultiplayerServer/DeleteRemoteUser` | Deletes a remote user to log on to a VM for a multiplayer server build. |
| `remote-login get-endpoint` | `/MultiplayerServer/GetRemoteLoginEndpoint` | Gets a remote login endpoint to a VM that is hosting a multiplayer server build. |
| `party request` | `/Party/RequestPartyService` | Requests a party session. |

## `mps build create`

`mps build create` is a convenience command, not a one-to-one command from the
API table. It requires `--type`, which selects the matching PlayFab build-create
API:

| `--type` value | PlayFab API path |
| --- | --- |
| `managed` | `/MultiplayerServer/CreateBuildWithManagedContainer` |
| `custom` | `/MultiplayerServer/CreateBuildWithCustomContainer` |
| `process` | `/MultiplayerServer/CreateBuildWithProcessBasedServer` |

The command still accepts `--body` and `--body-file` for the request payload.

```bash
xbox --playfab-title-id ABC123 playfab mps build create --type managed --body-file managed-build.json
```

## `mps certificate upload` and `mps secret upload`

`mps certificate upload` and `mps secret upload` call
`/MultiplayerServer/UploadCertificate` and `/MultiplayerServer/UploadSecret`.
Their request body holds a PFX password or a secret value, so they require
`--body-file <path>`, or `--body-file -` to read it from stdin. Both refuse
`--body`, without repeating its value, because an inline value is kept in shell
history, process listings, and agent transcripts. Neither accepts
`--output table`.

| Command | Request fields |
| --- | --- |
| `certificate upload` | `GameCertificate` (`Name`, `Base64EncodedValue`, and `Password` if the PFX has one), `ForceUpdate`, `CustomTags` |
| `secret upload` | `GameSecret` (`Name`, `Value`, `ExpirationDate`), `ForceUpdate`, `CustomTags` |

`ForceUpdate` replaces a certificate or secret that already has the name. Keep
the body file out of source control, and delete it once it's uploaded.

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
xbox --playfab-title-id ABC123 playfab mps certificate upload --body-file ./certificate.json
```

To build the body without writing the secret to another file, pipe it to
`--body-file -`. Here, `jq` reads the value from `api-key.txt` rather than from
its own arguments, so the value stays out of process listings:

```bash
jq -n --rawfile value ./api-key.txt \
  '{GameSecret: {Name: "nod-arena-api-key", Value: ($value | rtrimstr("\n"))}}' \
  | xbox --playfab-title-id ABC123 playfab mps secret upload --body-file -
```

## `mps asset upload`

`mps asset upload` uploads a local file as a multiplayer server asset. It does
not accept `--body` or `--body-file`; instead, it builds the upload request from
its options.

```bash
xbox --playfab-title-id ABC123 playfab mps asset upload --file gameserver.zip --asset-name gameserver.zip
```

Options:

| Option | Required | Meaning |
| --- | --- | --- |
| `--file` | Yes | Path to the single local file to upload. Replaces the pre-public `--file-path` spelling. |
| `--asset-name` | Yes | Asset name to create in PlayFab. |
| `--buffer-size` | No | Upload block size in bytes. Defaults to `4194304` bytes. |

The upload flow is:

1. Validate that `--buffer-size` is positive and that `--file` exists.
2. Call `/MultiplayerServer/GetAssetUploadUrl` with `{ "FileName": "<asset-name>" }`.
3. Upload the file directly to the returned Azure Storage block blob SAS URL.
4. Stage fixed-size blocks, retry each block up to five times, then commit the
   block list.
5. Print JSON containing the asset name and upload status.

## Destructive MPS commands

Every MPS leaf command's Help classifies its own effect under a `SAFETY`
heading and names the PlayFab API it calls under `LEARN MORE`, so you can check
a command's classification without leaving the terminal:

```console
$ xbox playfab mps build delete --help
...
SAFETY
  Destructive and irreversible. Requires --confirm (--yes, -y), or an
  interactive confirmation at a terminal.

LEARN MORE
  Calls the PlayFab API: /MultiplayerServer/DeleteBuild
```

Any MPS command that maps to a `Delete*` or `Shutdown*` PlayFab operation is
hard-gated by the global confirmation option. In this group those are
`build delete`, `build alias delete`, `build region delete`, `server shutdown`,
`asset delete`, `certificate delete`, `secret delete`,
`container-image repository delete`, and `remote-user delete`. For example:

```bash
xbox --playfab-title-id ABC123 playfab mps build delete --body '{ "BuildId": "11111111-2222-3333-4444-555555555555" }' --confirm
```

The gate classifies by the **PlayFab operation name**, not the CLI verb, so
`container-image untag` (`UntagContainerImage`) and
`container-registry credentials rollover`
(`RolloverContainerRegistryCredentials`) are not hard-gated even though they
change production state.

`Create*`, `Update*`, `Request*`, `Upload*`, `Enable*`, `Rollover*`, and
`Untag*` operations can still mutate production state even when they are not part
of the hard-gated catastrophic subset. Review the request body and target title
carefully before running them. See [Safety and destructive operations](../../safety.md).
