// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;
using Moq;
using System.Net;
using System.Text.Json;
using XboxDevCLI.PlayFab.Commands;
using XboxDevCLI.Safety;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

public class PlayFabGroupCommandTests
{
    #region Command structure tests

    [Fact]
    public void GroupsCommand_HasExpectedTopLevelSubcommands()
    {
        var command = GroupCommandFactory.Create();

        Assert.Equal("group", command.Name);
        foreach (var name in new[] { "create", "get", "update", "delete", "member", "role", "invite", "application", "block", "membership" })
        {
            Assert.Contains(command.Subcommands, c => c.Name == name);
        }
    }

    [Theory]
    [InlineData("member", new[] { "add", "remove", "change-role", "list", "is-member" })]
    [InlineData("role", new[] { "create", "update", "delete" })]
    [InlineData("invite", new[] { "send", "accept", "remove", "list" })]
    [InlineData("application", new[] { "apply", "accept", "remove", "list" })]
    [InlineData("block", new[] { "add", "remove", "list" })]
    public void GroupsSubgroup_HasExpectedLeafCommands(string subgroup, string[] expected)
    {
        var group = GroupCommandFactory.Create().Subcommands.First(c => c.Name == subgroup);

        Assert.Equal(expected.Length, group.Subcommands.Count);
        foreach (var name in expected)
        {
            Assert.Contains(group.Subcommands, c => c.Name == name);
        }
    }

    [Fact]
    public void MembershipSubgroup_HasListAndOpportunityList()
    {
        var membership = GroupCommandFactory.Create().Subcommands.First(c => c.Name == "membership");

        Assert.Contains(membership.Subcommands, c => c.Name == "list");
        var opportunity = Assert.Single(membership.Subcommands, c => c.Name == "opportunity");
        Assert.Contains(opportunity.Subcommands, c => c.Name == "list");
    }

    #endregion

    #region Build-helper mapping tests

    [Fact]
    public void BuildCreateGroupCall_MapsNameAndOptionalOwner()
    {
        var (apiPath, request) = GroupCommandFactory.BuildCreateGroupCall("Guild", "P1", "title_player_account");

        Assert.Equal("/Group/CreateGroup", apiPath);
        Assert.Equal("Guild", request.GroupName);
        Assert.Equal("P1", request.Entity!.Id);
        Assert.Equal("title_player_account", request.Entity!.Type);
    }

    [Fact]
    public void BuildCreateGroupCall_OmitsOwnerWhenNotProvided()
    {
        var (_, request) = GroupCommandFactory.BuildCreateGroupCall("Guild", null, null);

        Assert.Null(request.Entity);
    }

    [Fact]
    public void BuildGetGroupCall_ById_MapsIdWithoutType()
    {
        var (apiPath, request) = GroupCommandFactory.BuildGetGroupCall("G1", null);

        Assert.Equal("/Group/GetGroup", apiPath);
        Assert.Equal("G1", request.Group!.Id);
        Assert.Null(request.Group!.Type);
        Assert.Null(request.GroupName);
    }

    [Fact]
    public void BuildGetGroupCall_ByName_MapsNameAndOmitsGroup()
    {
        var (_, request) = GroupCommandFactory.BuildGetGroupCall(null, "Guild");

        Assert.Null(request.Group);
        Assert.Equal("Guild", request.GroupName);
    }

    [Fact]
    public void BuildAddMembersCall_ExpandsCommaSeparatedIdsWithSharedType()
    {
        var (apiPath, request) = GroupCommandFactory.BuildAddMembersCall(
            "G1", "a, b ,,c", "title_player_account", "admins");

        Assert.Equal("/Group/AddMembers", apiPath);
        Assert.Equal("admins", request.RoleId);
        Assert.NotNull(request.Members);
        Assert.Equal(new[] { "a", "b", "c" }, request.Members!.Select(m => m.Id));
        Assert.All(request.Members!, m => Assert.Equal("title_player_account", m.Type));
    }

    [Fact]
    public void BuildChangeMemberRoleCall_MapsOriginAndDestination()
    {
        var (apiPath, request) = GroupCommandFactory.BuildChangeMemberRoleCall(
            "G1", "a,b", "character", "members", "admins");

        Assert.Equal("/Group/ChangeMemberRole", apiPath);
        Assert.Equal("members", request.OriginRoleId);
        Assert.Equal("admins", request.DestinationRoleId);
        Assert.Equal(2, request.Members!.Count);
    }

    [Fact]
    public void BuildDeleteGroupCall_UsesDeletePath()
    {
        var (apiPath, request) = GroupCommandFactory.BuildDeleteGroupCall("G1");

        Assert.Equal("/Group/DeleteGroup", apiPath);
        Assert.Equal("G1", request.Group!.Id);
    }

    [Fact]
    public void BuildIsMemberCall_MapsGroupEntityAndRole()
    {
        var (apiPath, request) = GroupCommandFactory.BuildIsMemberCall("G1", "P1", "title_player_account", "admins");

        Assert.Equal("/Group/IsMember", apiPath);
        Assert.Equal("G1", request.Group!.Id);
        Assert.Equal("P1", request.Entity!.Id);
        Assert.Equal("admins", request.RoleId);
    }

    [Fact]
    public void BuildInviteToGroupCall_MapsAutoAccept()
    {
        var (apiPath, request) = GroupCommandFactory.BuildInviteToGroupCall("G1", "P1", "title_player_account", null, false);

        Assert.Equal("/Group/InviteToGroup", apiPath);
        Assert.False(request.AutoAcceptOutstandingApplication);
    }

    [Fact]
    public void BuildListMembershipCall_OmitsEntityWhenNotProvided()
    {
        var (apiPath, request) = GroupCommandFactory.BuildListMembershipCall(null, null);

        Assert.Equal("/Group/ListMembership", apiPath);
        Assert.Null(request.Entity);
    }

    [Fact]
    public void BuildMembers_TrimsAndDropsEmpties()
    {
        var members = GroupCommandFactory.BuildMembers(" a , ,b,", "character");

        Assert.Equal(new[] { "a", "b" }, members.Select(m => m.Id));
    }

    #endregion

    #region Integration tests with PlayFabApiClient

    private static IConfiguration CreateTestConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PlayFab:TitleId"] = "TEST123",
                ["PlayFab:BaseUrlTemplate"] = "https://{0}.playfabapi.com",
                ["AzureAd:ClientId"] = "test-client-id",
                ["AzureAd:TenantId"] = "test-tenant-id",
                ["AzureAd:Scopes"] = "api://test/.default",
            })
            .Build();

    private static AuthenticationResult CreateMockAuthResult(string accessToken) =>
        new(
            accessToken: accessToken,
            isExtendedLifeTimeToken: false,
            uniqueId: "test-unique-id",
            expiresOn: DateTimeOffset.UtcNow.AddHours(1),
            extendedExpiresOn: DateTimeOffset.UtcNow.AddHours(1),
            tenantId: "test-tenant-id",
            account: Mock.Of<IAccount>(),
            idToken: "test-id-token",
            scopes: new[] { "api://test/.default" },
            correlationId: Guid.NewGuid());

    private static PlayFabApiClient CreateClientWithHandler(HttpMessageHandler handler)
    {
        var config = CreateTestConfiguration();
        var mockAuth = new Mock<AuthenticationService>(config) { CallBase = false };
        mockAuth.Setup(a => a.AcquireTokenSilentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(CreateMockAuthResult("token"));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://TEST123.playfabapi.com/") };
        return new PlayFabApiClient(config, mockAuth.Object, httpClient);
    }

    private static readonly JsonSerializerOptions IgnoreNulls = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public async Task ListGroupMembers_Read_SendsGroupAndCorrectPath()
    {
        var handler = new GroupCommandMockHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"code":200,"status":"OK","data":{"Members":[]}}""") });
        using var client = CreateClientWithHandler(handler);

        var (apiPath, request) = GroupCommandFactory.BuildListGroupMembersCall("G1");
        await client.CallApiAsync(apiPath, JsonSerializer.Serialize(request, IgnoreNulls), CancellationToken.None);

        Assert.Contains("/Group/ListGroupMembers", handler.LastRequestUri);
        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        Assert.Equal("G1", doc.RootElement.GetProperty("Group").GetProperty("Id").GetString());
    }

    [Fact]
    public async Task CreateGroup_Write_SendsGroupNameAndOmitsCustomTags()
    {
        var handler = new GroupCommandMockHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"code":200,"status":"OK","data":{"GroupName":"Guild"}}""") });
        using var client = CreateClientWithHandler(handler);

        var (apiPath, request) = GroupCommandFactory.BuildCreateGroupCall("Guild", null, null);
        await client.CallApiAsync(apiPath, JsonSerializer.Serialize(request, IgnoreNulls), CancellationToken.None);

        Assert.Contains("/Group/CreateGroup", handler.LastRequestUri);
        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        Assert.Equal("Guild", doc.RootElement.GetProperty("GroupName").GetString());
        Assert.False(doc.RootElement.TryGetProperty("CustomTags", out _));
        Assert.False(doc.RootElement.TryGetProperty("Entity", out _));
    }

    [Fact]
    public async Task AddMembers_Write_SendsMembersArrayWithSharedType()
    {
        var handler = new GroupCommandMockHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"code":200,"status":"OK","data":{}}""") });
        using var client = CreateClientWithHandler(handler);

        var (apiPath, request) = GroupCommandFactory.BuildAddMembersCall("G1", "a,b", "title_player_account", null);
        await client.CallApiAsync(apiPath, JsonSerializer.Serialize(request, IgnoreNulls), CancellationToken.None);

        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        var members = doc.RootElement.GetProperty("Members").EnumerateArray().ToList();
        Assert.Equal(2, members.Count);
        Assert.Equal("a", members[0].GetProperty("Id").GetString());
        Assert.All(members, m => Assert.Equal("title_player_account", m.GetProperty("Type").GetString()));
        Assert.False(doc.RootElement.TryGetProperty("RoleId", out _));
    }

    [Fact]
    public async Task DeleteGroup_WithoutConfirmation_IsBlockedByDestructiveGuard()
    {
        var handler = new GroupCommandMockHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"code":200,"status":"OK","data":{}}""") });
        using var client = CreateClientWithHandler(handler);

        var (apiPath, request) = GroupCommandFactory.BuildDeleteGroupCall("G1");

        await Assert.ThrowsAsync<DestructiveOperationNotConfirmedException>(
            () => client.CallApiAsync(apiPath, JsonSerializer.Serialize(request, IgnoreNulls), CancellationToken.None));
        Assert.Null(handler.LastRequestUri);
    }

    [Fact]
    public async Task DeleteGroup_WithConfirmation_SendsDeleteRequest()
    {
        var handler = new GroupCommandMockHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"code":200,"status":"OK","data":{}}""") });
        using var client = CreateClientWithHandler(handler);

        var (apiPath, request) = GroupCommandFactory.BuildDeleteGroupCall("G1");
        await client.CallApiAsync(apiPath, JsonSerializer.Serialize(request, IgnoreNulls), CancellationToken.None, confirmedDestructive: true);

        Assert.Contains("/Group/DeleteGroup", handler.LastRequestUri);
        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        Assert.Equal("G1", doc.RootElement.GetProperty("Group").GetProperty("Id").GetString());
    }

    #endregion
}

/// <summary>
/// Mock handler for the group command tests that captures the outgoing request URI and body
/// for assertion, then returns a fixed response. Owned by this test file so it stays decoupled
/// from other command tests' helpers.
/// </summary>
internal sealed class GroupCommandMockHandler : HttpMessageHandler
{
    private readonly HttpResponseMessage _response;

    public GroupCommandMockHandler(HttpResponseMessage response)
    {
        _response = response;
    }

    public string? CapturedBody { get; private set; }

    public string? LastRequestUri { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri?.ToString();
        if (request.Content != null)
        {
            CapturedBody = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        return _response;
    }
}
