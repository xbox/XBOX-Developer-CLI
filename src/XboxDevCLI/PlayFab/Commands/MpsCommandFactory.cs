// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using XboxDevCLI.CommandLine;
using XboxDevCLI.PlayFab.Generated.Multiplayer;
using XboxDevCLI.Services;

namespace XboxDevCLI.PlayFab.Commands;

/// <summary>
/// Builds the <c>mps</c> command group for PlayFab Multiplayer Server (MPS).
///
/// Commands follow a noun → verb structure (e.g. <c>mps build alias create</c>,
/// <c>mps server shutdown</c>) consistent with the rest of the CLI, and their verbs track the
/// underlying PlayFab API operations (Get/List/Create/Delete/Update/Upload/…). They cover the
/// same MPS surface as the deprecated
/// <see href="https://github.com/PlayFab/MpsPowershell">MpsPowershell</see> module, but there is
/// no <c>Set-PfTitle</c> step: authentication uses the Entra ID developer identity established
/// with <c>login</c>, and the request body is supplied via <c>--body</c> or <c>--body-file</c>.
/// The commands whose request body carries a credential (see <see cref="SecretBodies"/>) take it
/// only from <c>--body-file</c>.
/// </summary>
internal static class MpsCommandFactory
{
    /// <summary>
    /// Describes a single MPS leaf command: its command path under <c>mps</c>
    /// (e.g. <c>["build", "alias", "create"]</c>), the PlayFab API path it calls, and a
    /// short description.
    /// </summary>
    /// <param name="Path">The command path segments under <c>mps</c> (noun groups then verb).</param>
    /// <param name="ApiPath">The PlayFab API path to call (e.g. <c>/MultiplayerServer/GetBuild</c>).</param>
    /// <param name="Description">A short, human-readable description.</param>
    internal sealed record MpsApiCommand(string[] Path, string ApiPath, string Description);

    /// <summary>
    /// The MPS leaf commands that map one-to-one to a single Multiplayer Server API call,
    /// expressed as a noun → verb tree. The three build-creation variants (collapsed into
    /// <c>build create --type</c>, see <see cref="BuildCreateEndpoints"/>) and the asset upload
    /// (<c>asset upload</c>) are added separately because they don't map one-to-one.
    /// </summary>
    internal static readonly IReadOnlyList<MpsApiCommand> ApiCommands = new[]
    {
        // title
        new MpsApiCommand(new[] { "title", "enable" }, "/MultiplayerServer/EnableMultiplayerServersForTitle", "Enables the multiplayer server feature for a title."),
        new MpsApiCommand(new[] { "title", "status" }, "/MultiplayerServer/GetTitleEnabledForMultiplayerServersStatus", "Gets whether a title is enabled for the multiplayer server feature."),

        // build
        new MpsApiCommand(new[] { "build", "get" }, "/MultiplayerServer/GetBuild", "Gets a multiplayer server build."),
        new MpsApiCommand(new[] { "build", "list" }, "/MultiplayerServer/ListBuildSummariesV2", "Lists one page of summarized details of multiplayer server builds for a title."),
        new MpsApiCommand(new[] { "build", "update-name" }, "/MultiplayerServer/UpdateBuildName", "Updates a multiplayer server build's name."),
        new MpsApiCommand(new[] { "build", "delete" }, "/MultiplayerServer/DeleteBuild", "Deletes a multiplayer server build."),

        // build alias
        new MpsApiCommand(new[] { "build", "alias", "get" }, "/MultiplayerServer/GetBuildAlias", "Gets a multiplayer server build alias."),
        new MpsApiCommand(new[] { "build", "alias", "list" }, "/MultiplayerServer/ListBuildAliases", "Lists one page of build alias details for a title."),
        new MpsApiCommand(new[] { "build", "alias", "create" }, "/MultiplayerServer/CreateBuildAlias", "Creates a multiplayer server build alias."),
        new MpsApiCommand(new[] { "build", "alias", "update" }, "/MultiplayerServer/UpdateBuildAlias", "Updates a multiplayer server build alias."),
        new MpsApiCommand(new[] { "build", "alias", "delete" }, "/MultiplayerServer/DeleteBuildAlias", "Deletes a multiplayer server build alias."),

        // build region
        new MpsApiCommand(new[] { "build", "region", "update" }, "/MultiplayerServer/UpdateBuildRegion", "Updates a multiplayer server build's region."),
        new MpsApiCommand(new[] { "build", "region", "update-all" }, "/MultiplayerServer/UpdateBuildRegions", "Updates all of a multiplayer server build's regions in bulk."),
        new MpsApiCommand(new[] { "build", "region", "delete" }, "/MultiplayerServer/DeleteBuildRegion", "Removes a multiplayer server build's region."),

        // server (running sessions)
        new MpsApiCommand(new[] { "server", "get" }, "/MultiplayerServer/GetMultiplayerServerDetails", "Gets multiplayer server session details for a build."),
        new MpsApiCommand(new[] { "server", "list" }, "/MultiplayerServer/ListMultiplayerServers", "Lists one page of multiplayer server sessions for a build in a region."),
        new MpsApiCommand(new[] { "server", "list-archived" }, "/MultiplayerServer/ListArchivedMultiplayerServers", "Lists one page of archived multiplayer server sessions for a build."),
        new MpsApiCommand(new[] { "server", "request" }, "/MultiplayerServer/RequestMultiplayerServer", "Requests a multiplayer server session."),
        new MpsApiCommand(new[] { "server", "shutdown" }, "/MultiplayerServer/ShutdownMultiplayerServer", "Shuts down a multiplayer server session."),
        new MpsApiCommand(new[] { "server", "get-logs" }, "/MultiplayerServer/GetMultiplayerServerLogs", "Gets multiplayer server logs after a server has terminated."),
        new MpsApiCommand(new[] { "server", "get-session-logs" }, "/MultiplayerServer/GetMultiplayerSessionLogsBySessionId", "Gets multiplayer server logs by session ID after a server has terminated."),

        // asset
        new MpsApiCommand(new[] { "asset", "list" }, "/MultiplayerServer/ListAssetSummaries", "Lists one page of multiplayer server game assets for a title."),
        new MpsApiCommand(new[] { "asset", "delete" }, "/MultiplayerServer/DeleteAsset", "Deletes a multiplayer server game asset for a title."),
        new MpsApiCommand(new[] { "asset", "get-upload-url" }, "/MultiplayerServer/GetAssetUploadUrl", "Gets the URL to upload assets to."),
        new MpsApiCommand(new[] { "asset", "get-download-url" }, "/MultiplayerServer/GetAssetDownloadUrl", "Gets a URL that can be used to download the specified asset."),

        // certificate
        new MpsApiCommand(new[] { "certificate", "list" }, "/MultiplayerServer/ListCertificateSummaries", "Lists one page of multiplayer server game certificates for a title."),
        new MpsApiCommand(new[] { "certificate", "upload" }, "/MultiplayerServer/UploadCertificate", "Uploads a multiplayer server game certificate."),
        new MpsApiCommand(new[] { "certificate", "delete" }, "/MultiplayerServer/DeleteCertificate", "Deletes a multiplayer server game certificate."),

        // secret
        new MpsApiCommand(new[] { "secret", "list" }, "/MultiplayerServer/ListSecretSummaries", "Lists one page of multiplayer server game secrets for a title."),
        new MpsApiCommand(new[] { "secret", "upload" }, "/MultiplayerServer/UploadSecret", "Uploads a multiplayer server game secret."),
        new MpsApiCommand(new[] { "secret", "delete" }, "/MultiplayerServer/DeleteSecret", "Deletes a multiplayer server game secret."),

        // container-image
        new MpsApiCommand(new[] { "container-image", "list" }, "/MultiplayerServer/ListContainerImages", "Lists one page of custom container images for a title."),
        new MpsApiCommand(new[] { "container-image", "list-tags" }, "/MultiplayerServer/ListContainerImageTags", "Lists one page of the tags for a custom container image."),
        new MpsApiCommand(new[] { "container-image", "untag" }, "/MultiplayerServer/UntagContainerImage", "Untags a container image."),
        new MpsApiCommand(new[] { "container-image", "repository", "delete" }, "/MultiplayerServer/DeleteContainerImageRepository", "Deletes a container image repository."),

        // container-registry
        new MpsApiCommand(new[] { "container-registry", "credentials", "get" }, "/MultiplayerServer/GetContainerRegistryCredentials", "Gets the credentials to the container registry."),
        new MpsApiCommand(new[] { "container-registry", "credentials", "rollover" }, "/MultiplayerServer/RolloverContainerRegistryCredentials", "Rolls over the credentials to the container registry."),

        // quota
        new MpsApiCommand(new[] { "quota", "get" }, "/MultiplayerServer/GetTitleMultiplayerServersQuotas", "Gets the quotas for a title in relation to multiplayer servers."),
        new MpsApiCommand(new[] { "quota", "change", "get" }, "/MultiplayerServer/GetTitleMultiplayerServersQuotaChange", "Gets a title's server quota change request."),
        new MpsApiCommand(new[] { "quota", "change", "list" }, "/MultiplayerServer/ListTitleMultiplayerServersQuotaChanges", "Lists all of a title's server quota change requests."),
        new MpsApiCommand(new[] { "quota", "change", "create" }, "/MultiplayerServer/CreateTitleMultiplayerServersQuotaChange", "Creates a request to change a title's multiplayer server quotas."),

        // quality of service
        new MpsApiCommand(new[] { "qos-server", "list" }, "/MultiplayerServer/ListQosServersForTitle", "Lists quality of service servers for the title."),
        new MpsApiCommand(new[] { "party-qos-server", "list" }, "/MultiplayerServer/ListPartyQosServers", "Lists quality of service servers for party."),

        // virtual-machine
        new MpsApiCommand(new[] { "virtual-machine", "list" }, "/MultiplayerServer/ListVirtualMachineSummaries", "Lists one page of virtual machines for a title."),

        // remote-user / remote-login
        new MpsApiCommand(new[] { "remote-user", "create" }, "/MultiplayerServer/CreateRemoteUser", "Creates a remote user to log on to a VM for a multiplayer server build."),
        new MpsApiCommand(new[] { "remote-user", "delete" }, "/MultiplayerServer/DeleteRemoteUser", "Deletes a remote user to log on to a VM for a multiplayer server build."),
        new MpsApiCommand(new[] { "remote-login", "get-endpoint" }, "/MultiplayerServer/GetRemoteLoginEndpoint", "Gets a remote login endpoint to a VM that is hosting a multiplayer server build."),

        // party
        new MpsApiCommand(new[] { "party", "request" }, "/Party/RequestPartyService", "Requests a party session."),
    };

    /// <summary>
    /// The <c>mps build create --type</c> values mapped to their underlying build-creation API
    /// endpoints. A single <c>create</c> command fans out to these by container model.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> BuildCreateEndpoints =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["managed"] = "/MultiplayerServer/CreateBuildWithManagedContainer",
            ["custom"] = "/MultiplayerServer/CreateBuildWithCustomContainer",
            ["process"] = "/MultiplayerServer/CreateBuildWithProcessBasedServer",
        };

    /// <summary>
    /// The one PlayFab operation <c>mps asset upload</c> calls; the bytes then go to the storage
    /// URL it returns.
    /// </summary>
    internal const string GetAssetUploadUrlPath = "/MultiplayerServer/GetAssetUploadUrl";

    /// <summary>
    /// Human-readable descriptions for the intermediate noun groups, keyed by their space-joined
    /// path (e.g. <c>"build alias"</c>). Groups without an entry fall back to a generic description.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> GroupDescriptions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["build"] = "Manage multiplayer server builds.",
            ["build alias"] = "Manage multiplayer server build aliases.",
            ["build region"] = "Manage a multiplayer server build's regions.",
            ["server"] = "Manage running multiplayer server sessions.",
            ["asset"] = "Manage multiplayer server game assets.",
            ["certificate"] = "Manage multiplayer server game certificates.",
            ["secret"] = "Manage multiplayer server game secrets.",
            ["container-image"] = "Manage custom container images.",
            ["container-image repository"] = "Manage container image repositories.",
            ["container-registry"] = "Manage the container registry.",
            ["container-registry credentials"] = "Manage container registry credentials.",
            ["quota"] = "View multiplayer server quotas.",
            ["quota change"] = "Manage multiplayer server quota change requests.",
            ["qos-server"] = "Quality of service (QoS) servers for the title.",
            ["party-qos-server"] = "Quality of service (QoS) servers for Party.",
            ["virtual-machine"] = "Multiplayer server virtual machines.",
            ["remote-user"] = "Manage remote users for build VMs.",
            ["remote-login"] = "Remote login endpoints for build VMs.",
            ["title"] = "Title-level multiplayer server settings.",
            ["party"] = "PlayFab Party.",
        };

    /// <summary>
    /// Builds the top-level <c>mps</c> command and its noun → verb sub-command tree.
    /// </summary>
    public static Command CreateMpsCommand()
    {
        var mpsCommand = new Command(
            "mps",
            "Manage Multiplayer Servers (MPS)");

        var groups = new Dictionary<string, Command>(StringComparer.Ordinal);

        Command GetOrCreateGroup(IReadOnlyList<string> groupPath)
        {
            var parent = mpsCommand;
            for (var i = 0; i < groupPath.Count; i++)
            {
                var key = string.Join(' ', groupPath.Take(i + 1));
                if (!groups.TryGetValue(key, out var group))
                {
                    var description = GroupDescriptions.TryGetValue(key, out var d) ? d : $"{groupPath[i]} commands.";
                    group = new Command(groupPath[i], description);
                    parent.Subcommands.Add(group);
                    groups[key] = group;
                }

                parent = group;
            }

            return parent;
        }

        foreach (var apiCommand in ApiCommands)
        {
            var groupPath = apiCommand.Path[..^1];
            var verb = apiCommand.Path[^1];
            var parent = GetOrCreateGroup(groupPath);
            parent.Subcommands.Add(CreateApiCommand(verb, apiCommand.Description, apiCommand.ApiPath));
        }

        // Commands that don't map one-to-one to a single API path:
        GetOrCreateGroup(new[] { "build" }).Subcommands.Add(CreateBuildCreateCommand());
        GetOrCreateGroup(new[] { "asset" }).Subcommands.Add(CreateAssetUploadCommand());

        return mpsCommand;
    }

    /// <summary>
    /// Table views for the MPS commands whose responses are a flat array of records.
    /// </summary>
    /// <remarks>
    /// Keyed by API path rather than CLI path so a future rename of the command tree cannot
    /// silently detach a view from its command. Deliberately sparse: most MPS operations return a
    /// single object or a nested shape that a flat table would have to discard fields to render.
    /// </remarks>
    private static readonly Dictionary<string, TableView> TableViews = new(StringComparer.Ordinal)
    {
        ["/MultiplayerServer/ListBuildSummariesV2"] = new(
            nameof(ListBuildSummariesResponse.BuildSummaries),
            [
                new TableColumn("BUILD ID", nameof(BuildSummary.BuildId), 38),
                new TableColumn("NAME", nameof(BuildSummary.BuildName), 40),
                new TableColumn("CREATED", nameof(BuildSummary.CreationTime), 24),
            ]),
        ["/MultiplayerServer/ListBuildAliases"] = new(
            nameof(ListBuildAliasesResponse.BuildAliases),
            [
                new TableColumn("ALIAS ID", nameof(BuildAliasDetailsResponse.AliasId), 38),
                new TableColumn("NAME", nameof(BuildAliasDetailsResponse.AliasName), 40),
            ]),
        ["/MultiplayerServer/ListCertificateSummaries"] = new(
            nameof(ListCertificateSummariesResponse.CertificateSummaries),
            [
                new TableColumn("NAME", nameof(CertificateSummary.Name), 40),
                new TableColumn("THUMBPRINT", nameof(CertificateSummary.Thumbprint), 44),
            ]),
        ["/MultiplayerServer/ListQosServersForTitle"] = new(
            nameof(ListQosServersForTitleResponse.QosServers),
            [
                new TableColumn("REGION", nameof(QosServer.Region), 24),
                new TableColumn("SERVER URL", nameof(QosServer.ServerUrl), 60),
            ]),
        ["/MultiplayerServer/ListPartyQosServers"] = new(
            nameof(ListPartyQosServersResponse.QosServers),
            [
                new TableColumn("REGION", nameof(QosServer.Region), 24),
                new TableColumn("SERVER URL", nameof(QosServer.ServerUrl), 60),
            ]),
        ["/MultiplayerServer/ListMultiplayerServers"] = new(
            nameof(ListMultiplayerServersResponse.MultiplayerServerSummaries),
            [
                new TableColumn("SERVER ID", nameof(MultiplayerServerSummary.ServerId), 38),
                new TableColumn("REGION", nameof(MultiplayerServerSummary.Region), 20),
                new TableColumn("STATE", nameof(MultiplayerServerSummary.State), 14),
                // Deliberately not ConnectedPlayers: it is a List<ConnectedPlayer>, and a column
                // pointing at a collection renders the raw JSON truncated to the column width.
                new TableColumn("SESSION ID", nameof(MultiplayerServerSummary.SessionId), 38),
            ]),
        ["/MultiplayerServer/ListArchivedMultiplayerServers"] = new(
            nameof(ListMultiplayerServersResponse.MultiplayerServerSummaries),
            [
                new TableColumn("SERVER ID", nameof(MultiplayerServerSummary.ServerId), 38),
                new TableColumn("REGION", nameof(MultiplayerServerSummary.Region), 20),
                new TableColumn("STATE", nameof(MultiplayerServerSummary.State), 14),
                new TableColumn("LAST TRANSITION", nameof(MultiplayerServerSummary.LastStateTransitionTime), 24),
            ]),
        ["/MultiplayerServer/ListAssetSummaries"] = new(
            nameof(ListAssetSummariesResponse.AssetSummaries),
            [
                new TableColumn("FILE NAME", nameof(AssetSummary.FileName), 60),
            ]),
        ["/MultiplayerServer/ListSecretSummaries"] = new(
            nameof(ListSecretSummariesResponse.SecretSummaries),
            [
                new TableColumn("NAME", nameof(SecretSummary.Name), 40),
                new TableColumn("VERSION", nameof(SecretSummary.Version), 12),
                new TableColumn("EXPIRES", nameof(SecretSummary.ExpirationDate), 24),
            ]),
        ["/MultiplayerServer/ListVirtualMachineSummaries"] = new(
            nameof(ListVirtualMachineSummariesResponse.VirtualMachines),
            [
                new TableColumn("VM ID", nameof(VirtualMachineSummary.VmId), 40),
                new TableColumn("STATE", nameof(VirtualMachineSummary.State), 16),
                new TableColumn("HEALTH", nameof(VirtualMachineSummary.HealthStatus), 16),
            ]),
        ["/MultiplayerServer/ListTitleMultiplayerServersQuotaChanges"] = new(
            nameof(ListTitleMultiplayerServersQuotaChangesResponse.Changes),
            [
                new TableColumn("REQUEST ID", nameof(QuotaChange.RequestId), 38),
                new TableColumn("DESCRIPTION", nameof(QuotaChange.ChangeDescription), 40),
                new TableColumn("PENDING", nameof(QuotaChange.IsPendingReview), 9),
                new TableColumn("APPROVED", nameof(QuotaChange.WasApproved), 9),
            ]),

        // Scalar collections: the array holds bare strings, so the single column reads the
        // element itself rather than a property of it.
        ["/MultiplayerServer/ListContainerImages"] = new(
            nameof(ListContainerImagesResponse.Images),
            [
                new TableColumn("IMAGE", string.Empty, 72),
            ]),
        ["/MultiplayerServer/ListContainerImageTags"] = new(
            nameof(ListContainerImageTagsResponse.Tags),
            [
                new TableColumn("TAG", string.Empty, 72),
            ]),

        // Record views: one object, rendered as a one-row table. Columns are kept to the few
        // fields worth seeing at a glance, because a row has to fit across a terminal — anyone
        // who wants the full response wants JSON.
        ["/MultiplayerServer/GetBuild"] = TableView.Record(
            [
                new TableColumn("BUILD ID", nameof(GetBuildResponse.BuildId), 38),
                new TableColumn("NAME", nameof(GetBuildResponse.BuildName), 40),
                new TableColumn("STATUS", nameof(GetBuildResponse.BuildStatus), 14),
                new TableColumn("OS", nameof(GetBuildResponse.OsPlatform), 10),
                new TableColumn("VM SIZE", nameof(GetBuildResponse.VmSize), 20),
            ]),
        ["/MultiplayerServer/GetBuildAlias"] = TableView.Record(
            [
                new TableColumn("ALIAS ID", nameof(BuildAliasDetailsResponse.AliasId), 38),
                new TableColumn("NAME", nameof(BuildAliasDetailsResponse.AliasName), 60),
            ]),
        ["/MultiplayerServer/GetMultiplayerServerDetails"] = TableView.Record(
            [
                new TableColumn("SERVER ID", nameof(GetMultiplayerServerDetailsResponse.ServerId), 38),
                new TableColumn("STATE", nameof(GetMultiplayerServerDetailsResponse.State), 14),
                new TableColumn("REGION", nameof(GetMultiplayerServerDetailsResponse.Region), 16),
                new TableColumn("SESSION ID", nameof(GetMultiplayerServerDetailsResponse.SessionId), 38),
                new TableColumn("IPV4 ADDRESS", nameof(GetMultiplayerServerDetailsResponse.IPV4Address), 16),
            ]),
        ["/MultiplayerServer/GetTitleEnabledForMultiplayerServersStatus"] = TableView.Record(
            [
                new TableColumn("STATUS", nameof(GetTitleEnabledForMultiplayerServersStatusResponse.Status), 24),
            ]),
        ["/MultiplayerServer/GetRemoteLoginEndpoint"] = TableView.Record(
            [
                new TableColumn("IPV4 ADDRESS", nameof(GetRemoteLoginEndpointResponse.IPV4Address), 16),
                new TableColumn("PORT", nameof(GetRemoteLoginEndpointResponse.Port), 8),
            ]),
        // Both asset URLs carry a pre-authenticated signature, so they are bearer credentials in
        // the same sense as the registry password below, and are masked for the same reason.
        ["/MultiplayerServer/GetAssetUploadUrl"] = TableView.Record(
            [
                new TableColumn("FILE NAME", nameof(GetAssetUploadUrlResponse.FileName), 40),
                new TableColumn(
                    "UPLOAD URL",
                    nameof(GetAssetUploadUrlResponse.AssetUploadUrl),
                    12,
                    Redact: true),
            ]),
        ["/MultiplayerServer/GetAssetDownloadUrl"] = TableView.Record(
            [
                new TableColumn("FILE NAME", nameof(GetAssetDownloadUrlResponse.FileName), 40),
                new TableColumn(
                    "DOWNLOAD URL",
                    nameof(GetAssetDownloadUrlResponse.AssetDownloadUrl),
                    14,
                    Redact: true),
            ]),
        // The password is a reusable registry credential. It is redacted here because a table is
        // the format that ends up in screenshots, pasted chat, and terminal scrollback; the JSON
        // formats — including the default — still return it.
        ["/MultiplayerServer/GetContainerRegistryCredentials"] = TableView.Record(
            [
                new TableColumn("DNS NAME", nameof(GetContainerRegistryCredentialsResponse.DnsName), 48),
                new TableColumn("USERNAME", nameof(GetContainerRegistryCredentialsResponse.Username), 32),
                new TableColumn(
                    "PASSWORD",
                    nameof(GetContainerRegistryCredentialsResponse.Password),
                    12,
                    Redact: true),
            ]),

        // The change itself is nested one level down, so the view names it rather than reading
        // the response root.
        ["/MultiplayerServer/GetTitleMultiplayerServersQuotaChange"] = TableView.Record(
            nameof(GetTitleMultiplayerServersQuotaChangeResponse.Change),
            [
                new TableColumn("REQUEST ID", nameof(QuotaChange.RequestId), 38),
                new TableColumn("DESCRIPTION", nameof(QuotaChange.ChangeDescription), 40),
                new TableColumn("PENDING", nameof(QuotaChange.IsPendingReview), 8),
                new TableColumn("APPROVED", nameof(QuotaChange.WasApproved), 8),
            ]),
    };

    /// <summary>
    /// The MPS list operations that return one page per call, with the request field that sizes a
    /// page and the field that carries the next-page token.
    /// </summary>
    /// <remarks>
    /// Keyed by API path for the same reason as <see cref="TableViews"/>. Each entry names its own
    /// request and response types so a regeneration that renames a field breaks the build, and
    /// <c>CommandPagingTests</c> checks both fields exist on the models the operation uses.
    /// <c>quota change list</c> is absent because its request and response carry no paging fields.
    /// </remarks>
    private static readonly Dictionary<string, CommandPaging> PagedOperations = new(StringComparer.Ordinal)
    {
        ["/MultiplayerServer/ListBuildSummariesV2"] = new(
            nameof(ListBuildSummariesRequest.PageSize), nameof(ListBuildSummariesResponse.SkipToken)),
        ["/MultiplayerServer/ListBuildAliases"] = new(
            nameof(ListBuildAliasesRequest.PageSize), nameof(ListBuildAliasesResponse.SkipToken)),
        ["/MultiplayerServer/ListMultiplayerServers"] = new(
            nameof(ListMultiplayerServersRequest.PageSize), nameof(ListMultiplayerServersResponse.SkipToken)),
        ["/MultiplayerServer/ListArchivedMultiplayerServers"] = new(
            nameof(ListMultiplayerServersRequest.PageSize), nameof(ListMultiplayerServersResponse.SkipToken)),
        ["/MultiplayerServer/ListAssetSummaries"] = new(
            nameof(ListAssetSummariesRequest.PageSize), nameof(ListAssetSummariesResponse.SkipToken)),
        ["/MultiplayerServer/ListCertificateSummaries"] = new(
            nameof(ListCertificateSummariesRequest.PageSize), nameof(ListCertificateSummariesResponse.SkipToken)),
        ["/MultiplayerServer/ListSecretSummaries"] = new(
            nameof(ListSecretSummariesRequest.PageSize), nameof(ListSecretSummariesResponse.SkipToken)),
        ["/MultiplayerServer/ListContainerImages"] = new(
            nameof(ListContainerImagesRequest.PageSize), nameof(ListContainerImagesResponse.SkipToken)),
        ["/MultiplayerServer/ListContainerImageTags"] = new(
            nameof(ListContainerImageTagsRequest.PageSize), nameof(ListContainerImageTagsResponse.SkipToken)),
        ["/MultiplayerServer/ListVirtualMachineSummaries"] = new(
            nameof(ListVirtualMachineSummariesRequest.PageSize), nameof(ListVirtualMachineSummariesResponse.SkipToken)),
    };

    /// <summary>
    /// How Help describes a command whose request body carries a credential.
    /// </summary>
    /// <param name="Fields">The request's fields, appended to the <c>--body-file</c> description.</param>
    /// <param name="Holds">What the body holds, for the line in LEARN MORE that says why.</param>
    /// <param name="Example">A complete invocation that reads the body from a file.</param>
    private sealed record SecretBody(string Fields, string Holds, string Example);

    /// <summary>
    /// The MPS operations whose request body carries a credential, keyed by API path. Their
    /// commands take the body only from <c>--body-file</c>, a path or <c>-</c> for stdin, and
    /// refuse <c>--body</c>: an inline value lands in shell history, process listings, and agent
    /// transcripts.
    /// </summary>
    private static readonly Dictionary<string, SecretBody> SecretBodies = new(StringComparer.Ordinal)
    {
        ["/MultiplayerServer/UploadCertificate"] = new(
            $"Fields: {nameof(UploadCertificateRequest.GameCertificate)} " +
            $"({nameof(Certificate.Name)}, {nameof(Certificate.Base64EncodedValue)}, " +
            $"{nameof(Certificate.Password)}), {nameof(UploadCertificateRequest.ForceUpdate)}, " +
            $"{nameof(UploadCertificateRequest.CustomTags)}.",
            "the certificate and its PFX password",
            "xbox playfab mps certificate upload --body-file ./certificate.json"),
        ["/MultiplayerServer/UploadSecret"] = new(
            $"Fields: {nameof(UploadSecretRequest.GameSecret)} " +
            $"({nameof(Secret.Name)}, {nameof(Secret.Value)}, {nameof(Secret.ExpirationDate)}), " +
            $"{nameof(UploadSecretRequest.ForceUpdate)}, {nameof(UploadSecretRequest.CustomTags)}.",
            "the secret's value",
            "xbox playfab mps secret upload --body-file ./secret.json"),
    };

    /// <summary>
    /// The refusal for an inline <c>--body</c> on a command in <see cref="SecretBodies"/>. It never
    /// repeats the value, since stderr ends up in logs.
    /// </summary>
    internal const string InlineSecretBodyRefusal =
        "--body isn't accepted here, because this command's request body holds a secret, and an " +
        "inline value is kept in shell history and process listings. Put the JSON in a file and " +
        "pass --body-file <path>, or pass --body-file - to read it from stdin.";

    private static Command CreateApiCommand(string name, string description, string apiPath)
    {
        var secretBody = SecretBodies.GetValueOrDefault(apiPath);

        var bodyFileOption = new Option<string?>("--body-file")
        {
            Description = "Path to a file containing the JSON request body, or - to read UTF-8 from stdin until EOF.",
        };

        var command = new Command(name, description)
        {
            bodyFileOption,
        };

        if (secretBody is null)
        {
            var bodyOption = new Option<string?>("--body")
            {
                Description = "JSON request body (inline). Defaults to {} if neither --body nor --body-file is provided.",
            };
            command.Options.Insert(0, bodyOption);
            command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
                await Program.InvokeApiAsync(
                    parseResult,
                    apiPath,
                    parseResult.GetValue(bodyOption),
                    parseResult.GetValue(bodyFileOption),
                    cancellationToken));
        }
        else
        {
            // --body is the other MPS commands' way in, so it gets guidance here rather than a
            // generic parse error. Hidden, and only ever refused. It takes every argument after it,
            // and repeats, so the parser never reports a fragment of an inline body, such as one
            // the shell split on a space, as an unrecognized argument and repeats it on stderr.
            var refusedBodyOption = new Option<string[]>("--body")
            {
                Hidden = true,
                Arity = ArgumentArity.ZeroOrMore,
                AllowMultipleArgumentsPerToken = true,
            };
            refusedBodyOption.Validators.Add(result => result.AddError(InlineSecretBodyRefusal));
            command.Options.Insert(0, refusedBodyOption);

            bodyFileOption.Description =
                "File holding the JSON request body, or - to read it from stdin. " + secretBody.Fields;
            bodyFileOption.HelpName = "path";
            // Without a body there's nothing to upload, so fail before sign-in rather than send {}.
            bodyFileOption.Required = true;
            command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
                await Program.InvokeApiAsync(
                    parseResult,
                    apiPath,
                    body: null,
                    parseResult.GetValue(bodyFileOption),
                    cancellationToken));
        }

        // Recording the API path lets Help describe the command's safety from the operation it
        // actually invokes, rather than inferring it from the friendly CLI verb. The table view,
        // where one exists, is what makes `--output table` available for this command.
        return command.WithMetadata(new CommandMetadata(
            ApiPaths: [apiPath],
            Examples: secretBody is null ? null : [secretBody.Example],
            LearnMore: secretBody is null
                ? null
                :
                [
                    $"The request body holds {secretBody.Holds}, so it's read only from " +
                    "--body-file <path> or --body-file - (stdin), never from an inline --body.",
                ],
            Table: TableViews.GetValueOrDefault(apiPath),
            Paging: PagedOperations.GetValueOrDefault(apiPath)));
    }

    /// <summary>
    /// Builds <c>mps build create</c>, which creates a multiplayer server build. <c>--type</c>
    /// selects the container model and maps to the matching CreateBuild* API (see
    /// <see cref="BuildCreateEndpoints"/>).
    /// </summary>
    private static Command CreateBuildCreateCommand()
    {
        var typeOption = new Option<string>("--type")
        {
            Description = "The build's container model: 'managed', 'custom', or 'process'.",
            Required = true,
        };

        typeOption.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 && !BuildCreateEndpoints.ContainsKey(result.Tokens[0].Value))
            {
                result.AddError($"Invalid value for --type. Allowed values: {string.Join(", ", BuildCreateEndpoints.Keys)}.");
            }
        });

        var bodyOption = new Option<string?>("--body")
        {
            Description = "JSON request body (inline). Defaults to {} if neither --body nor --body-file is provided.",
        };

        var bodyFileOption = new Option<string?>("--body-file")
        {
            Description = "Path to a file containing the JSON request body, or - to read UTF-8 from stdin until EOF.",
        };

        var command = new Command("create", "Creates a multiplayer server build.")
        {
            typeOption,
            bodyOption,
            bodyFileOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var type = parseResult.GetValue(typeOption)!;
            var apiPath = BuildCreateEndpoints[type];
            var body = parseResult.GetValue(bodyOption);
            var bodyFile = parseResult.GetValue(bodyFileOption);
            return await Program.InvokeApiAsync(parseResult, apiPath, body, bodyFile, cancellationToken);
        });

        return command.WithMetadata(new CommandMetadata(
            ApiPaths: [.. BuildCreateEndpoints.Values],
            LearnMore: ["--type selects which of these the command calls."]));
    }

    /// <summary>
    /// Builds <c>mps asset upload</c>, which uploads a local file to PlayFab as a multiplayer
    /// server asset. It first requests an asset upload URL, then uploads the file as a block blob
    /// to Azure Storage.
    /// </summary>
    private static Command CreateAssetUploadCommand()
    {
        var filePathOption = new Option<string>("--file")
        {
            Description = "The path to the local file to upload.",
            Required = true,
        };

        var assetNameOption = new Option<string>("--asset-name")
        {
            Description = "The name of the asset to upload.",
            Required = true,
        };

        var bufferSizeOption = new Option<int>("--buffer-size")
        {
            Description = "The size of the buffer used for uploading (in bytes).",
            DefaultValueFactory = _ => 4 * 1024 * 1024,
        };

        var command = new Command("upload", "Uploads a local file to PlayFab as a multiplayer server asset.")
        {
            filePathOption,
            assetNameOption,
            bufferSizeOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var filePath = parseResult.GetValue(filePathOption)!;
            var assetName = parseResult.GetValue(assetNameOption)!;
            var bufferSize = parseResult.GetValue(bufferSizeOption);
            return await UploadAssetAsync(parseResult, filePath, assetName, bufferSize, cancellationToken);
        });

        // GetAssetUploadUrl is a read operation, but the command does not stop there: it writes
        // the file over the URL it gets back, which adds the asset to the title. Classifying this
        // from the one PlayFab operation it calls would report a write as a read, so the effect is
        // stated explicitly and the operation is left in LEARN MORE where it belongs.
        return command.WithMetadata(new CommandMetadata(
            ApiPaths: [GetAssetUploadUrlPath],
            Safety: CommandSafety.Mutates,
            Examples:
            [
                "xbox playfab mps asset upload --file ./gameserver.zip --asset-name gameserver.zip",
            ],
            SafetyDetails:
                "The asset is written to the title's storage over the URL GetAssetUploadUrl " +
                "returns, not by the PlayFab call itself."));
    }

    private static async Task<int> UploadAssetAsync(
        ParseResult parseResult,
        string filePath,
        string assetName,
        int bufferSize,
        CancellationToken cancellationToken)
    {
        if (bufferSize <= 0)
        {
            Console.Error.WriteLine("Error: --buffer-size must be a positive number of bytes.");
            return 1;
        }

        if (!File.Exists(filePath))
        {
            Console.Error.WriteLine($"Error: File not found: {filePath}");
            return 1;
        }

        var outputFormat = parseResult.GetValue(Program.OutputOption) ?? "json";
        var compact = outputFormat.Equals("json-compact", StringComparison.OrdinalIgnoreCase);

        try
        {
            var config = Program.BuildConfiguration(parseResult);
            var authService = Program.GetAuthentication(parseResult);
            using var client = new PlayFabApiClient(config, authService);

            var uploadUrlRequest = JsonSerializer.Serialize(new Dictionary<string, string> { ["FileName"] = assetName });
            var uploadUrlResponse = await client.CallApiAsync(GetAssetUploadUrlPath, uploadUrlRequest, cancellationToken: cancellationToken);

            var assetUploadUrl = ExtractAssetUploadUrl(uploadUrlResponse);
            if (assetUploadUrl == null)
            {
                Console.Error.WriteLine("Error: The GetAssetUploadUrl response did not contain an AssetUploadUrl.");
                return 1;
            }

            using var httpClient = new HttpClient();
            await UploadBlockBlobAsync(httpClient, assetUploadUrl, filePath, bufferSize, cancellationToken);

            var result = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["AssetName"] = assetName,
                ["Status"] = "Uploaded",
            });
            Console.WriteLine(PlayFabApiClient.FormatJson(result, compact));
            return 0;
        }
        catch (PlayFabApiException ex)
        {
            Console.Error.WriteLine(Program.FormatErrorOutput(ex, compact));
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Asset upload failed: {ex.Message}");
            return 1;
        }
    }

    private static string? ExtractAssetUploadUrl(string getAssetUploadUrlResponse)
    {
        using var doc = JsonDocument.Parse(getAssetUploadUrlResponse);
        if (doc.RootElement.TryGetProperty("data", out var data) &&
            data.TryGetProperty("AssetUploadUrl", out var url) &&
            url.ValueKind == JsonValueKind.String)
        {
            return url.GetString();
        }

        return null;
    }

    /// <summary>
    /// Uploads <paramref name="filePath"/> to the supplied Azure Storage block blob SAS URL,
    /// staging the file in fixed-size blocks and committing the block list, including per-block
    /// retries.
    /// </summary>
    private static async Task UploadBlockBlobAsync(
        HttpClient httpClient,
        string assetUploadUrl,
        string filePath,
        int bufferSize,
        CancellationToken cancellationToken)
    {
        const int maxRetries = 5;

        await using var fileStream = File.OpenRead(filePath);
        var buffer = new byte[bufferSize];
        var blockNum = 0;
        var base64BlockIds = new List<string>();

        var numBytesRead = await fileStream.ReadAsync(buffer.AsMemory(0, bufferSize), cancellationToken);
        while (numBytesRead > 0)
        {
            string? base64BlockId = null;
            Exception? lastError = null;

            for (var numFailures = 0; numFailures < maxRetries; numFailures++)
            {
                try
                {
                    // Include numFailures in the blockId so we never attempt to reupload an existing blockId.
                    var blockId = $"{blockNum}_{numFailures}".PadLeft(15, '0');
                    base64BlockId = Convert.ToBase64String(Encoding.UTF8.GetBytes(blockId));

                    var uri = $"{assetUploadUrl}&comp=block&blockId={Uri.EscapeDataString(base64BlockId)}";
                    using var content = new ByteArrayContent(buffer, 0, numBytesRead);
                    using var response = await httpClient.PutAsync(uri, content, cancellationToken);
                    response.EnsureSuccessStatusCode();

                    lastError = null;
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastError = ex;
                    base64BlockId = null;
                }
            }

            if (lastError != null || base64BlockId == null)
            {
                throw new InvalidOperationException(
                    $"Failed to upload block {blockNum} after {maxRetries} retries. " +
                    $"Last error: {lastError?.Message}",
                    lastError);
            }

            base64BlockIds.Add(base64BlockId);
            blockNum++;
            numBytesRead = await fileStream.ReadAsync(buffer.AsMemory(0, bufferSize), cancellationToken);
        }

        var blockList = new StringBuilder();
        blockList.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        blockList.AppendLine("<BlockList>");
        foreach (var id in base64BlockIds)
        {
            blockList.AppendLine($"<Uncommitted>{id}</Uncommitted>");
        }

        blockList.AppendLine("</BlockList>");

        var blockListUri = $"{assetUploadUrl}&comp=blocklist";
        using var blockListContent = new StringContent(blockList.ToString(), Encoding.UTF8);
        blockListContent.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
        using var blockListResponse = await httpClient.PutAsync(blockListUri, blockListContent, cancellationToken);
        blockListResponse.EnsureSuccessStatusCode();
    }
}
