// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.CommandLine;
using System.Text.Json;
using XboxDevCLI.PlayFab.Generated.Groups;
using XboxDevCLI.Services;

namespace XboxDevCLI.PlayFab.Commands;

/// <summary>
/// Creates the "playfab group" command group for the PlayFab Groups (Entity Group) API:
/// managing groups, their roles, members, invitations, applications, and blocks.
///
/// Commands follow a noun → verb structure (e.g. <c>group member add</c>,
/// <c>group role delete</c>) and map one-to-one to a single <c>/Group/*</c> API call.
/// Each command builds a strongly-typed request model (from
/// <see cref="XboxDevCLI.PlayFab.Generated.Groups"/>) from explicit options, then routes
/// through <see cref="ExecuteAsync{TRequest}"/> which serializes the request, calls the
/// shared <see cref="PlayFabApiClient"/>, honors the global output options, and surfaces
/// PlayFab error JSON.
/// </summary>
internal static class GroupCommandFactory
{
    // Named once and referenced from both the request builder and the command's metadata, so the
    // path Help advertises is necessarily the path the command calls. They were separate literals
    // on the handful of commands that already declared metadata.
    internal const string CreateGroupPath = "/Group/CreateGroup";
    internal const string GetGroupPath = "/Group/GetGroup";
    internal const string UpdateGroupPath = "/Group/UpdateGroup";
    internal const string DeleteGroupPath = "/Group/DeleteGroup";
    internal const string AddMembersPath = "/Group/AddMembers";
    internal const string RemoveMembersPath = "/Group/RemoveMembers";
    internal const string ChangeMemberRolePath = "/Group/ChangeMemberRole";
    internal const string ListGroupMembersPath = "/Group/ListGroupMembers";
    internal const string IsMemberPath = "/Group/IsMember";
    internal const string CreateRolePath = "/Group/CreateRole";
    internal const string UpdateRolePath = "/Group/UpdateRole";
    internal const string DeleteRolePath = "/Group/DeleteRole";
    internal const string InviteToGroupPath = "/Group/InviteToGroup";
    internal const string AcceptGroupInvitationPath = "/Group/AcceptGroupInvitation";
    internal const string RemoveGroupInvitationPath = "/Group/RemoveGroupInvitation";
    internal const string ListGroupInvitationsPath = "/Group/ListGroupInvitations";
    internal const string ApplyToGroupPath = "/Group/ApplyToGroup";
    internal const string AcceptGroupApplicationPath = "/Group/AcceptGroupApplication";
    internal const string RemoveGroupApplicationPath = "/Group/RemoveGroupApplication";
    internal const string ListGroupApplicationsPath = "/Group/ListGroupApplications";
    internal const string BlockEntityPath = "/Group/BlockEntity";
    internal const string UnblockEntityPath = "/Group/UnblockEntity";
    internal const string ListGroupBlocksPath = "/Group/ListGroupBlocks";
    internal const string ListMembershipPath = "/Group/ListMembership";
    internal const string ListMembershipOpportunitiesPath = "/Group/ListMembershipOpportunities";

    /// <summary>
    /// Serialization options for request bodies: optional (null) fields are omitted so a
    /// minimal, valid PlayFab request is produced from only the options the user supplied.
    /// </summary>
    private static readonly JsonSerializerOptions RequestOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static Command Create()
    {
        var groups = new Command(
            "group",
            "Manage groups and membership");

        // Top-level group lifecycle.
        groups.Subcommands.Add(AddCreateGroupCommand());
        groups.Subcommands.Add(CreateGetGroupCommand());
        groups.Subcommands.Add(CreateUpdateGroupCommand());
        groups.Subcommands.Add(CreateDeleteGroupCommand());

        // member
        var member = new Command("member", "Manage group members.");
        member.Subcommands.Add(CreateMemberAddCommand());
        member.Subcommands.Add(CreateMemberRemoveCommand());
        member.Subcommands.Add(CreateMemberChangeRoleCommand());
        member.Subcommands.Add(CreateMemberListCommand());
        member.Subcommands.Add(CreateMemberIsMemberCommand());
        groups.Subcommands.Add(member);

        // role
        var role = new Command("role", "Manage group roles.");
        role.Subcommands.Add(CreateRoleCreateCommand());
        role.Subcommands.Add(CreateRoleUpdateCommand());
        role.Subcommands.Add(CreateRoleDeleteCommand());
        groups.Subcommands.Add(role);

        // invite
        var invite = new Command("invite", "Manage group invitations.");
        invite.Subcommands.Add(CreateInviteSendCommand());
        invite.Subcommands.Add(CreateInviteAcceptCommand());
        invite.Subcommands.Add(CreateInviteRemoveCommand());
        invite.Subcommands.Add(CreateInviteListCommand());
        groups.Subcommands.Add(invite);

        // application (requests to join)
        var application = new Command("application", "Manage group applications (requests to join).");
        application.Subcommands.Add(CreateApplicationApplyCommand());
        application.Subcommands.Add(CreateApplicationAcceptCommand());
        application.Subcommands.Add(CreateApplicationRemoveCommand());
        application.Subcommands.Add(CreateApplicationListCommand());
        groups.Subcommands.Add(application);

        // block
        var block = new Command("block", "Manage entities blocked from joining a group.");
        block.Subcommands.Add(CreateBlockAddCommand());
        block.Subcommands.Add(CreateBlockRemoveCommand());
        block.Subcommands.Add(CreateBlockListCommand());
        groups.Subcommands.Add(block);

        // membership (for an entity, not a group)
        var membership = new Command("membership", "View the groups and roles an entity belongs to.");
        membership.Subcommands.Add(CreateMembershipListCommand());

        // opportunity: invitations and applications are the entity's outstanding chances to join a
        // group, so they hang off their own noun rather than hiding inside a list-opportunities verb.
        var opportunity = new Command(
            "opportunity",
            "View an entity's outstanding invitations and applications to join groups.");
        opportunity.Subcommands.Add(CreateMembershipListOpportunitiesCommand());
        membership.Subcommands.Add(opportunity);

        groups.Subcommands.Add(membership);

        return groups;
    }

    #region Top-level commands

    private static Command AddCreateGroupCommand()
    {
        var groupNameOption = RequiredOption("--group-name", "The name of the group (unique per title by default).");
        var entityIdOption = EntityIdOptional("Optional: the entity ID that will own the group (defaults to the calling entity).");
        var entityTypeOption = EntityTypeOptional("Optional: the entity type of the owner (required if --entity-id is set).");

        var command = new Command("create", "Create a new group.")
        {
            groupNameOption,
            entityIdOption,
            entityTypeOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var groupName = parseResult.GetValue(groupNameOption)!;
            var entityId = parseResult.GetValue(entityIdOption);
            var entityType = parseResult.GetValue(entityTypeOption);

            if (!ValidateEntityPair(entityId, entityType))
            {
                return 1;
            }

            var (apiPath, request) = BuildCreateGroupCall(groupName, entityId, entityType);
            return await ExecuteAsync(parseResult, apiPath, request, cancellationToken: cancellationToken);
        });

        return command.WithMetadata(new CommandMetadata(ApiPaths: [CreateGroupPath]));
    }

    private static Command CreateGetGroupCommand()
    {
        var groupIdOption = OptionalOption("--group-id", "The group's entity ID (or use --group-name).");
        var groupNameOption = OptionalOption("--group-name", "The full name of the group (alternative to --group-id).");

        var command = new Command("get", "Get information about a group and its roles.")
        {
            groupIdOption,
            groupNameOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var groupId = parseResult.GetValue(groupIdOption);
            var groupName = parseResult.GetValue(groupNameOption);

            if (groupId is null && groupName is null)
            {
                Console.Error.WriteLine("Error: provide either --group-id or --group-name.");
                return 1;
            }

            if (groupId is not null && groupName is not null)
            {
                Console.Error.WriteLine("Error: --group-id and --group-name are mutually exclusive; provide only one.");
                return 1;
            }
            var (apiPath, request) = BuildGetGroupCall(groupId, groupName);
            return await ExecuteAsync(parseResult, apiPath, request, cancellationToken: cancellationToken);
        });

        return command.WithMetadata(new CommandMetadata(ApiPaths: [GetGroupPath]));
    }

    private static Command CreateUpdateGroupCommand()
    {
        var groupIdOption = GroupIdOption();
        var groupNameOption = OptionalOption("--group-name", "Optional: the new name of the group.");
        var adminRoleIdOption = OptionalOption("--admin-role-id", "Optional: the ID of an existing role to set as the group's administrator role.");
        var memberRoleIdOption = OptionalOption("--member-role-id", "Optional: the ID of an existing role to set as the group's default member role.");
        var expectedProfileVersionOption = new Option<double?>("--expected-profile-version")
        {
            Description = "Optional: concurrency control; only update if the group's profile version matches.",
        };

        var command = new Command("update", "Update non-membership data about a group (name, roles).")
        {
            groupIdOption,
            groupNameOption,
            adminRoleIdOption,
            memberRoleIdOption,
            expectedProfileVersionOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var (apiPath, request) = BuildUpdateGroupCall(
                parseResult.GetValue(groupIdOption)!,
                parseResult.GetValue(groupNameOption),
                parseResult.GetValue(adminRoleIdOption),
                parseResult.GetValue(memberRoleIdOption),
                parseResult.GetValue(expectedProfileVersionOption));
            return await ExecuteAsync(parseResult, apiPath, request, cancellationToken: cancellationToken);
        });

        return command.WithMetadata(new CommandMetadata(ApiPaths: [UpdateGroupPath]));
    }

    private static Command CreateDeleteGroupCommand()
    {
        var groupIdOption = GroupIdOption();
        var command = new Command(
            "delete",
            "Delete a group and all its roles, invitations, applications, and blocks.")
        {
            groupIdOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var (apiPath, request) = BuildDeleteGroupCall(
                parseResult.GetValue(groupIdOption)!);

            if (!ResolveDestructiveConfirmation(parseResult, apiPath))
            {
                return 1;
            }

            return await ExecuteAsync(parseResult, apiPath, request, confirmedDestructive: true, cancellationToken: cancellationToken);
        });

        return command.WithMetadata(new CommandMetadata(ApiPaths: [DeleteGroupPath]));
    }

    #endregion

    #region member commands

    private static Command CreateMemberAddCommand()
    {
        var groupIdOption = GroupIdOption();
        var memberIdsOption = RequiredOption("--member-ids", "Comma-separated list of member entity IDs to add.");
        var memberTypeOption = RequiredOption("--member-type", "Entity type shared by all members (e.g. title_player_account, character).");
        var roleIdOption = OptionalOption("--role-id", "Optional: the role to add the members to (defaults to the group's member role).");

        var command = new Command("add", "Add members to a group or role.")
        {
            groupIdOption,
            memberIdsOption,
            memberTypeOption,
            roleIdOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var (apiPath, request) = BuildAddMembersCall(
                parseResult.GetValue(groupIdOption)!,
                parseResult.GetValue(memberIdsOption)!,
                parseResult.GetValue(memberTypeOption)!,
                parseResult.GetValue(roleIdOption));
            return await ExecuteAsync(parseResult, apiPath, request, cancellationToken: cancellationToken);
        });

        return command.WithMetadata(new CommandMetadata(ApiPaths: [AddMembersPath]));
    }

    private static Command CreateMemberRemoveCommand()
    {
        var groupIdOption = GroupIdOption();
        var memberIdsOption = RequiredOption("--member-ids", "Comma-separated list of member entity IDs to remove.");
        var memberTypeOption = RequiredOption("--member-type", "Entity type shared by all members (e.g. title_player_account, character).");
        var roleIdOption = OptionalOption("--role-id", "Optional: the role to remove the members from.");

        var command = new Command("remove", "Remove members from a group.")
        {
            groupIdOption,
            memberIdsOption,
            memberTypeOption,
            roleIdOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var (apiPath, request) = BuildRemoveMembersCall(
                parseResult.GetValue(groupIdOption)!,
                parseResult.GetValue(memberIdsOption)!,
                parseResult.GetValue(memberTypeOption)!,
                parseResult.GetValue(roleIdOption));
            return await ExecuteAsync(parseResult, apiPath, request, cancellationToken: cancellationToken);
        });

        return command.WithMetadata(new CommandMetadata(ApiPaths: [RemoveMembersPath]));
    }

    private static Command CreateMemberChangeRoleCommand()
    {
        var groupIdOption = GroupIdOption();
        var memberIdsOption = RequiredOption("--member-ids", "Comma-separated list of member entity IDs to move between roles.");
        var memberTypeOption = RequiredOption("--member-type", "Entity type shared by all members (e.g. title_player_account, character).");
        var originRoleIdOption = RequiredOption("--origin-role-id", "The role the members currently belong to.");
        var destinationRoleIdOption = RequiredOption("--destination-role-id", "The existing role the members will be moved to.");

        var command = new Command("change-role", "Move members from one role to another.")
        {
            groupIdOption,
            memberIdsOption,
            memberTypeOption,
            originRoleIdOption,
            destinationRoleIdOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var (apiPath, request) = BuildChangeMemberRoleCall(
                parseResult.GetValue(groupIdOption)!,
                parseResult.GetValue(memberIdsOption)!,
                parseResult.GetValue(memberTypeOption)!,
                parseResult.GetValue(originRoleIdOption)!,
                parseResult.GetValue(destinationRoleIdOption)!);
            return await ExecuteAsync(parseResult, apiPath, request, cancellationToken: cancellationToken);
        });

        return command.WithMetadata(new CommandMetadata(ApiPaths: [ChangeMemberRolePath]));
    }

    private static Command CreateMemberListCommand()
    {
        var groupIdOption = GroupIdOption();
        var command = new Command("list", "List all members of a group and their roles.")
        {
            groupIdOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var (apiPath, request) = BuildListGroupMembersCall(
                parseResult.GetValue(groupIdOption)!);
            return await ExecuteAsync(parseResult, apiPath, request, cancellationToken: cancellationToken);
        });

        return command.WithMetadata(new CommandMetadata(ApiPaths: [ListGroupMembersPath]));
    }

    private static Command CreateMemberIsMemberCommand()
    {
        var groupIdOption = GroupIdOption();
        var entityIdOption = EntityIdRequired("The entity ID to check membership for.");
        var entityTypeOption = EntityTypeRequired("The entity type to check membership for (e.g. title_player_account).");
        var roleIdOption = OptionalOption("--role-id", "Optional: check membership of a specific role (defaults to any role).");

        var command = new Command("is-member", "Check whether an entity is a member of a group or role.")
        {
            groupIdOption,
            entityIdOption,
            entityTypeOption,
            roleIdOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var (apiPath, request) = BuildIsMemberCall(
                parseResult.GetValue(groupIdOption)!,
                parseResult.GetValue(entityIdOption)!,
                parseResult.GetValue(entityTypeOption)!,
                parseResult.GetValue(roleIdOption));
            return await ExecuteAsync(parseResult, apiPath, request, cancellationToken: cancellationToken);
        });

        // IsMember is the one operation this command family calls that reads without being named
        // Get, List, or Search, so the operation-name rule would classify a membership check as a
        // production mutation. PlayFab documents it as "Checks to see if an entity is a member of
        // a group or role within the group"; the classification is stated rather than derived,
        // the same mechanism `mps asset upload` uses in the opposite direction.
        return command.WithMetadata(new CommandMetadata(
            ApiPaths: [IsMemberPath],
            Safety: CommandSafety.ReadOnly));
    }

    #endregion

    #region role commands

    private static Command CreateRoleCreateCommand()
    {
        var groupIdOption = GroupIdOption();
        var roleIdOption = RequiredOption("--role-id", "The ID of the new role (unique within the group; cannot be changed).");
        var roleNameOption = RequiredOption("--role-name", "The display name of the new role.");
        var command = new Command("create", "Create a new role within a group.")
        {
            groupIdOption,
            roleIdOption,
            roleNameOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var (apiPath, request) = BuildCreateRoleCall(
                parseResult.GetValue(groupIdOption)!,
                parseResult.GetValue(roleIdOption)!,
                parseResult.GetValue(roleNameOption)!);
            return await ExecuteAsync(parseResult, apiPath, request, cancellationToken: cancellationToken);
        });

        return command.WithMetadata(new CommandMetadata(ApiPaths: [CreateRolePath]));
    }

    private static Command CreateRoleUpdateCommand()
    {
        var groupIdOption = GroupIdOption();
        var roleIdOption = RequiredOption("--role-id", "The ID of the role to update.");
        var roleNameOption = RequiredOption("--role-name", "The new display name of the role.");
        var expectedProfileVersionOption = new Option<double?>("--expected-profile-version")
        {
            Description = "Optional: concurrency control; only update if the group's profile version matches.",
        };

        var command = new Command("update", "Update a role's name.")
        {
            groupIdOption,
            roleIdOption,
            roleNameOption,
            expectedProfileVersionOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var (apiPath, request) = BuildUpdateRoleCall(
                parseResult.GetValue(groupIdOption)!,
                parseResult.GetValue(roleIdOption)!,
                parseResult.GetValue(roleNameOption)!,
                parseResult.GetValue(expectedProfileVersionOption));
            return await ExecuteAsync(parseResult, apiPath, request, cancellationToken: cancellationToken);
        });

        return command.WithMetadata(new CommandMetadata(ApiPaths: [UpdateRolePath]));
    }

    private static Command CreateRoleDeleteCommand()
    {
        var groupIdOption = GroupIdOption();
        var roleIdOption = RequiredOption("--role-id", "The ID of the role to delete.");
        var command = new Command("delete", "Delete an existing role from a group.")
        {
            groupIdOption,
            roleIdOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var (apiPath, request) = BuildDeleteRoleCall(
                parseResult.GetValue(groupIdOption)!,
                parseResult.GetValue(roleIdOption)!);

            if (!ResolveDestructiveConfirmation(parseResult, apiPath))
            {
                return 1;
            }

            return await ExecuteAsync(parseResult, apiPath, request, confirmedDestructive: true, cancellationToken: cancellationToken);
        });

        return command.WithMetadata(new CommandMetadata(ApiPaths: [DeleteRolePath]));
    }

    #endregion

    #region invite commands

    private static Command CreateInviteSendCommand()
    {
        var groupIdOption = GroupIdOption();
        var entityIdOption = EntityIdRequired("The entity ID to invite.");
        var entityTypeOption = EntityTypeRequired("The entity type to invite (e.g. title_player_account).");
        var roleIdOption = OptionalOption("--role-id", "Optional: the role to assign the invited entity to (defaults to the member role).");
        var autoAcceptOption = new Option<bool?>("--auto-accept")
        {
            Description = "Optional: automatically accept an outstanding application from the entity if one exists (default true).",
        };

        var command = new Command("send", "Invite an entity to join a group.")
        {
            groupIdOption,
            entityIdOption,
            entityTypeOption,
            roleIdOption,
            autoAcceptOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var (apiPath, request) = BuildInviteToGroupCall(
                parseResult.GetValue(groupIdOption)!,
                parseResult.GetValue(entityIdOption)!,
                parseResult.GetValue(entityTypeOption)!,
                parseResult.GetValue(roleIdOption),
                parseResult.GetValue(autoAcceptOption));
            return await ExecuteAsync(parseResult, apiPath, request, cancellationToken: cancellationToken);
        });

        return command.WithMetadata(new CommandMetadata(ApiPaths: [InviteToGroupPath]));
    }

    private static Command CreateInviteAcceptCommand()
    {
        var groupIdOption = GroupIdOption();
        var entityIdOption = EntityIdOptional("Optional: the entity accepting the invitation (defaults to the calling entity).");
        var entityTypeOption = EntityTypeOptional("Optional: the entity type accepting the invitation (required if --entity-id is set).");
        var command = new Command("accept", "Accept an invitation to join a group.")
        {
            groupIdOption,
            entityIdOption,
            entityTypeOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var entityId = parseResult.GetValue(entityIdOption);
            var entityType = parseResult.GetValue(entityTypeOption);

            if (!ValidateEntityPair(entityId, entityType))
            {
                return 1;
            }

            var (apiPath, request) = BuildAcceptGroupInvitationCall(
                parseResult.GetValue(groupIdOption)!,
                entityId,
                entityType);
            return await ExecuteAsync(parseResult, apiPath, request, cancellationToken: cancellationToken);
        });

        return command.WithMetadata(new CommandMetadata(ApiPaths: [AcceptGroupInvitationPath]));
    }

    private static Command CreateInviteRemoveCommand()
    {
        var groupIdOption = GroupIdOption();
        var entityIdOption = EntityIdRequired("The entity ID whose invitation to remove.");
        var entityTypeOption = EntityTypeRequired("The entity type whose invitation to remove.");
        var command = new Command("remove", "Remove (reject or rescind) an invitation to join a group.")
        {
            groupIdOption,
            entityIdOption,
            entityTypeOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var (apiPath, request) = BuildRemoveGroupInvitationCall(
                parseResult.GetValue(groupIdOption)!,
                parseResult.GetValue(entityIdOption)!,
                parseResult.GetValue(entityTypeOption)!);
            return await ExecuteAsync(parseResult, apiPath, request, cancellationToken: cancellationToken);
        });

        return command.WithMetadata(new CommandMetadata(ApiPaths: [RemoveGroupInvitationPath]));
    }

    private static Command CreateInviteListCommand()
    {
        var groupIdOption = GroupIdOption();
        var command = new Command("list", "List all outstanding invitations for a group.")
        {
            groupIdOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var (apiPath, request) = BuildListGroupInvitationsCall(
                parseResult.GetValue(groupIdOption)!);
            return await ExecuteAsync(parseResult, apiPath, request, cancellationToken: cancellationToken);
        });

        return command.WithMetadata(new CommandMetadata(
            ApiPaths: [ListGroupInvitationsPath],
            Table: new TableView(
                nameof(ListGroupInvitationsResponse.Invitations),
                [
                    new TableColumn(
                        "INVITED ENTITY",
                        $"{nameof(GroupInvitation.InvitedEntity)}.{nameof(EntityWithLineage.Key)}.{nameof(EntityKey.Id)}",
                        38),
                    new TableColumn("ROLE ID", nameof(GroupInvitation.RoleId), 24),
                    new TableColumn("EXPIRES", nameof(GroupInvitation.Expires), 24),
                ])));
    }

    #endregion

    #region application commands

    private static Command CreateApplicationApplyCommand()
    {
        var groupIdOption = GroupIdOption();
        var entityIdOption = EntityIdOptional("Optional: the entity applying to the group (defaults to the calling entity).");
        var entityTypeOption = EntityTypeOptional("Optional: the entity type applying (required if --entity-id is set).");
        var autoAcceptOption = new Option<bool?>("--auto-accept")
        {
            Description = "Optional: automatically accept an outstanding invitation if one exists instead of applying (default true).",
        };

        var command = new Command("apply", "Apply to join a group.")
        {
            groupIdOption,
            entityIdOption,
            entityTypeOption,
            autoAcceptOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var entityId = parseResult.GetValue(entityIdOption);
            var entityType = parseResult.GetValue(entityTypeOption);

            if (!ValidateEntityPair(entityId, entityType))
            {
                return 1;
            }

            var (apiPath, request) = BuildApplyToGroupCall(
                parseResult.GetValue(groupIdOption)!,
                entityId,
                entityType,
                parseResult.GetValue(autoAcceptOption));
            return await ExecuteAsync(parseResult, apiPath, request, cancellationToken: cancellationToken);
        });

        return command.WithMetadata(new CommandMetadata(ApiPaths: [ApplyToGroupPath]));
    }

    private static Command CreateApplicationAcceptCommand()
    {
        var groupIdOption = GroupIdOption();
        var entityIdOption = EntityIdOptional("Optional: the applicant entity whose application to accept.");
        var entityTypeOption = EntityTypeOptional("Optional: the applicant entity type (required if --entity-id is set).");
        var command = new Command("accept", "Accept an entity's application to join a group.")
        {
            groupIdOption,
            entityIdOption,
            entityTypeOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var entityId = parseResult.GetValue(entityIdOption);
            var entityType = parseResult.GetValue(entityTypeOption);

            if (!ValidateEntityPair(entityId, entityType))
            {
                return 1;
            }

            var (apiPath, request) = BuildAcceptGroupApplicationCall(
                parseResult.GetValue(groupIdOption)!,
                entityId,
                entityType);
            return await ExecuteAsync(parseResult, apiPath, request, cancellationToken: cancellationToken);
        });

        return command.WithMetadata(new CommandMetadata(ApiPaths: [AcceptGroupApplicationPath]));
    }

    private static Command CreateApplicationRemoveCommand()
    {
        var groupIdOption = GroupIdOption();
        var entityIdOption = EntityIdRequired("The entity ID whose application to remove.");
        var entityTypeOption = EntityTypeRequired("The entity type whose application to remove.");

        var command = new Command("remove", "Remove (reject or withdraw) an application to join a group.")
        {
            groupIdOption,
            entityIdOption,
            entityTypeOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var (apiPath, request) = BuildRemoveGroupApplicationCall(
                parseResult.GetValue(groupIdOption)!,
                parseResult.GetValue(entityIdOption)!,
                parseResult.GetValue(entityTypeOption)!);
            return await ExecuteAsync(parseResult, apiPath, request, cancellationToken: cancellationToken);
        });

        return command.WithMetadata(new CommandMetadata(ApiPaths: [RemoveGroupApplicationPath]));
    }

    private static Command CreateApplicationListCommand()
    {
        var groupIdOption = GroupIdOption();

        var command = new Command("list", "List all outstanding applications to join a group.")
        {
            groupIdOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var (apiPath, request) = BuildListGroupApplicationsCall(
                parseResult.GetValue(groupIdOption)!);
            return await ExecuteAsync(parseResult, apiPath, request, cancellationToken: cancellationToken);
        });

        return command.WithMetadata(new CommandMetadata(
            ApiPaths: [ListGroupApplicationsPath],
            Table: new TableView(
                nameof(ListGroupApplicationsResponse.Applications),
                [
                    new TableColumn(
                        "ENTITY",
                        $"{nameof(GroupApplication.Entity)}.{nameof(EntityWithLineage.Key)}.{nameof(EntityKey.Id)}",
                        38),
                    new TableColumn("EXPIRES", nameof(GroupApplication.Expires), 24),
                ])));
    }

    #endregion

    #region block commands

    private static Command CreateBlockAddCommand()
    {
        var groupIdOption = GroupIdOption();
        var entityIdOption = EntityIdRequired("The entity ID to block from joining the group.");
        var entityTypeOption = EntityTypeRequired("The entity type to block (e.g. title_player_account).");

        var command = new Command("add", "Block an entity from joining a group.")
        {
            groupIdOption,
            entityIdOption,
            entityTypeOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var (apiPath, request) = BuildBlockEntityCall(
                parseResult.GetValue(groupIdOption)!,
                parseResult.GetValue(entityIdOption)!,
                parseResult.GetValue(entityTypeOption)!);
            return await ExecuteAsync(parseResult, apiPath, request, cancellationToken: cancellationToken);
        });

        return command.WithMetadata(new CommandMetadata(ApiPaths: [BlockEntityPath]));
    }

    private static Command CreateBlockRemoveCommand()
    {
        var groupIdOption = GroupIdOption();
        var entityIdOption = EntityIdRequired("The entity ID to unblock.");
        var entityTypeOption = EntityTypeRequired("The entity type to unblock.");

        var command = new Command("remove", "Unblock an entity, allowing it to join a group again.")
        {
            groupIdOption,
            entityIdOption,
            entityTypeOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var (apiPath, request) = BuildUnblockEntityCall(
                parseResult.GetValue(groupIdOption)!,
                parseResult.GetValue(entityIdOption)!,
                parseResult.GetValue(entityTypeOption)!);
            return await ExecuteAsync(parseResult, apiPath, request, cancellationToken: cancellationToken);
        });

        return command.WithMetadata(new CommandMetadata(ApiPaths: [UnblockEntityPath]));
    }

    private static Command CreateBlockListCommand()
    {
        var groupIdOption = GroupIdOption();

        var command = new Command("list", "List all entities blocked from joining a group.")
        {
            groupIdOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var (apiPath, request) = BuildListGroupBlocksCall(
                parseResult.GetValue(groupIdOption)!);
            return await ExecuteAsync(parseResult, apiPath, request, cancellationToken: cancellationToken);
        });

        return command.WithMetadata(new CommandMetadata(
            ApiPaths: [ListGroupBlocksPath],
            Table: new TableView(
                nameof(ListGroupBlocksResponse.BlockedEntities),
                [
                    new TableColumn(
                        "BLOCKED ENTITY",
                        $"{nameof(GroupBlock.Entity)}.{nameof(EntityWithLineage.Key)}.{nameof(EntityKey.Id)}",
                        38),
                    new TableColumn(
                        "ENTITY TYPE",
                        $"{nameof(GroupBlock.Entity)}.{nameof(EntityWithLineage.Key)}.{nameof(EntityKey.Type)}",
                        24),
                ])));
    }

    #endregion

    #region membership commands

    private static Command CreateMembershipListCommand()
    {
        var entityIdOption = EntityIdOptional("Optional: the entity whose memberships to list (defaults to the calling entity).");
        var entityTypeOption = EntityTypeOptional("Optional: the entity type (required if --entity-id is set).");

        var command = new Command("list", "List all groups and roles an entity belongs to.")
        {
            entityIdOption,
            entityTypeOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var entityId = parseResult.GetValue(entityIdOption);
            var entityType = parseResult.GetValue(entityTypeOption);

            if (!ValidateEntityPair(entityId, entityType))
            {
                return 1;
            }

            var (apiPath, request) = BuildListMembershipCall(entityId, entityType);
            return await ExecuteAsync(parseResult, apiPath, request, cancellationToken: cancellationToken);
        });

        return command.WithMetadata(new CommandMetadata(
            ApiPaths: [ListMembershipPath],
            Table: new TableView(
                nameof(ListMembershipResponse.Groups),
                [
                    new TableColumn(
                        "GROUP ID",
                        $"{nameof(GroupWithRoles.Group)}.{nameof(EntityKey.Id)}",
                        38),
                    new TableColumn("GROUP NAME", nameof(GroupWithRoles.GroupName), 36),
                ])));
    }

    private static Command CreateMembershipListOpportunitiesCommand()
    {
        var entityIdOption = EntityIdOptional("Optional: the entity whose opportunities to list (defaults to the calling entity).");
        var entityTypeOption = EntityTypeOptional("Optional: the entity type (required if --entity-id is set).");

        var command = new Command("list", "List an entity's outstanding invitations and applications.")
        {
            entityIdOption,
            entityTypeOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var entityId = parseResult.GetValue(entityIdOption);
            var entityType = parseResult.GetValue(entityTypeOption);

            if (!ValidateEntityPair(entityId, entityType))
            {
                return 1;
            }

            var (apiPath, request) = BuildListMembershipOpportunitiesCall(entityId, entityType);
            return await ExecuteAsync(parseResult, apiPath, request, cancellationToken: cancellationToken);
        });

        return command.WithMetadata(new CommandMetadata(ApiPaths: [ListMembershipOpportunitiesPath]));
    }

    #endregion

    #region Shared option factories

    private static Option<string> RequiredOption(string name, string description) =>
        new(name) { Description = description, Required = true };

    private static Option<string?> OptionalOption(string name, string description) =>
        new(name) { Description = description };

    private static Option<string> GroupIdOption() =>
        RequiredOption("--group-id", "The group's entity ID.");


    private static Option<string> EntityIdRequired(string description) =>
        RequiredOption("--entity-id", description);

    private static Option<string> EntityTypeRequired(string description) =>
        RequiredOption("--entity-type", description);

    private static Option<string?> EntityIdOptional(string description) =>
        OptionalOption("--entity-id", description);

    private static Option<string?> EntityTypeOptional(string description) =>
        OptionalOption("--entity-type", description);

    #endregion

    #region EntityKey / member helpers

    private static EntityKey Group(string id) => new() { Id = id };

    /// <summary>
    /// Builds an <see cref="EntityKey"/> from an id/type pair, returning <c>null</c> when both
    /// are absent (so an optional entity field is omitted from the request).
    /// </summary>
    private static EntityKey? Entity(string? id, string? type) =>
        id is null && type is null ? null : new EntityKey { Id = id, Type = type };

    /// <summary>
    /// Expands a comma-separated list of member ids into <see cref="EntityKey"/> values that all
    /// share <paramref name="memberType"/>. Whitespace is trimmed and empty entries are dropped.
    /// </summary>
    internal static List<EntityKey> BuildMembers(string memberIds, string memberType)
    {
        var ids = memberIds.Split(
            ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return ids.Select(id => new EntityKey { Id = id, Type = memberType }).ToList();
    }

    #endregion

    #region Shared executor

    /// <summary>
    /// Serializes <paramref name="request"/> and routes it through the shared
    /// <see cref="PlayFabCommandHelper.InvokeAsync"/> (call → format → error) using the
    /// process-global <see cref="Console"/> streams and the global <c>--output</c> /
    /// <c>--data-only</c> options resolved from <paramref name="parseResult"/>.
    /// </summary>
    /// <remarks>
    /// This is a thin wrapper over the shared helper: option resolution and client
    /// construction live here, while the testable call/format/error branches live in
    /// <see cref="PlayFabCommandHelper.InvokeAsync"/>, matching the other PlayFab command
    /// factories. Returns 0 on success; 1 on failure.
    /// </remarks>
    private static async Task<int> ExecuteAsync<TRequest>(
        ParseResult parseResult,
        string apiPath,
        TRequest request,
        CancellationToken cancellationToken,
        bool confirmedDestructive = false)
        where TRequest : class
    {
        var output = OutputOptions.From(parseResult);

        try
        {
            using var client = PlayFabCommandHelper.CreateClient(parseResult);
            var jsonBody = JsonSerializer.Serialize(request, RequestOptions);
            return await PlayFabCommandHelper.InvokeAsync(
                client, apiPath, jsonBody, output,
                Console.Out, Console.Error, cancellationToken, confirmedDestructive);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Request failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Resolves confirmation for a destructive operation (<c>Delete*</c>) via the global
    /// <c>--confirm</c> flag or an interactive prompt. Returns <c>true</c> when the caller may
    /// proceed and must pass <c>confirmedDestructive: true</c> to the client.
    /// </summary>
    private static bool ResolveDestructiveConfirmation(ParseResult parseResult, string apiPath) =>
        parseResult.GetValue(Program.ConfirmOption) || Program.ConfirmDestructiveOperation(apiPath);

    /// <summary>
    /// Validates that an optional entity id/type pair was supplied together (or not at all).
    /// Writes an error to stderr and returns <c>false</c> when only one half is present.
    /// </summary>
    private static bool ValidateEntityPair(string? entityId, string? entityType)
    {
        if (entityId is null == (entityType is null))
        {
            return true;
        }

        Console.Error.WriteLine("Error: --entity-id and --entity-type must be provided together.");
        return false;
    }

    #endregion

    #region Build helpers

    internal static (string apiPath, CreateGroupRequest request) BuildCreateGroupCall(
        string groupName, string? entityId, string? entityType) =>
        (CreateGroupPath, new CreateGroupRequest
        {
            GroupName = groupName,
            Entity = Entity(entityId, entityType),
        });

    internal static (string apiPath, GetGroupRequest request) BuildGetGroupCall(
        string? groupId, string? groupName) =>
        (GetGroupPath, new GetGroupRequest
        {
            // GetGroup already operates on a group, so only the id is sent — no entity type.
            Group = groupId is null ? null : Group(groupId),
            GroupName = groupName,
        });

    internal static (string apiPath, UpdateGroupRequest request) BuildUpdateGroupCall(
        string groupId, string? groupName, string? adminRoleId,
        string? memberRoleId, double? expectedProfileVersion) =>
        (UpdateGroupPath, new UpdateGroupRequest
        {
            // UpdateGroup already operates on a group, so only the id is sent — no entity type.
            Group = Group(groupId),
            GroupName = groupName,
            AdminRoleId = adminRoleId,
            MemberRoleId = memberRoleId,
            ExpectedProfileVersion = expectedProfileVersion,
        });

    internal static (string apiPath, DeleteGroupRequest request) BuildDeleteGroupCall(
        string groupId) =>
        (DeleteGroupPath, new DeleteGroupRequest { Group = Group(groupId) });

    internal static (string apiPath, AddMembersRequest request) BuildAddMembersCall(
        string groupId, string memberIds, string memberType, string? roleId) =>
        (AddMembersPath, new AddMembersRequest
        {
            Group = Group(groupId),
            Members = BuildMembers(memberIds, memberType),
            RoleId = roleId,
        });

    internal static (string apiPath, RemoveMembersRequest request) BuildRemoveMembersCall(
        string groupId, string memberIds, string memberType, string? roleId) =>
        (RemoveMembersPath, new RemoveMembersRequest
        {
            Group = Group(groupId),
            Members = BuildMembers(memberIds, memberType),
            RoleId = roleId,
        });

    internal static (string apiPath, ChangeMemberRoleRequest request) BuildChangeMemberRoleCall(
        string groupId, string memberIds, string memberType,
        string originRoleId, string destinationRoleId) =>
        (ChangeMemberRolePath, new ChangeMemberRoleRequest
        {
            Group = Group(groupId),
            Members = BuildMembers(memberIds, memberType),
            OriginRoleId = originRoleId,
            DestinationRoleId = destinationRoleId,
        });

    internal static (string apiPath, ListGroupMembersRequest request) BuildListGroupMembersCall(
        string groupId) =>
        (ListGroupMembersPath, new ListGroupMembersRequest { Group = Group(groupId) });

    internal static (string apiPath, IsMemberRequest request) BuildIsMemberCall(
        string groupId, string entityId, string entityType, string? roleId) =>
        (IsMemberPath, new IsMemberRequest
        {
            Group = Group(groupId),
            Entity = new EntityKey { Id = entityId, Type = entityType },
            RoleId = roleId,
        });

    internal static (string apiPath, CreateGroupRoleRequest request) BuildCreateRoleCall(
        string groupId, string roleId, string roleName) =>
        (CreateRolePath, new CreateGroupRoleRequest
        {
            Group = Group(groupId),
            RoleId = roleId,
            RoleName = roleName,
        });

    internal static (string apiPath, UpdateGroupRoleRequest request) BuildUpdateRoleCall(
        string groupId, string roleId, string roleName,
        double? expectedProfileVersion) =>
        (UpdateRolePath, new UpdateGroupRoleRequest
        {
            Group = Group(groupId),
            RoleId = roleId,
            RoleName = roleName,
            ExpectedProfileVersion = expectedProfileVersion,
        });

    internal static (string apiPath, DeleteRoleRequest request) BuildDeleteRoleCall(
        string groupId, string roleId) =>
        (DeleteRolePath, new DeleteRoleRequest
        {
            Group = Group(groupId),
            RoleId = roleId,
        });

    internal static (string apiPath, InviteToGroupRequest request) BuildInviteToGroupCall(
        string groupId, string entityId, string entityType,
        string? roleId, bool? autoAccept) =>
        (InviteToGroupPath, new InviteToGroupRequest
        {
            Group = Group(groupId),
            Entity = new EntityKey { Id = entityId, Type = entityType },
            RoleId = roleId,
            AutoAcceptOutstandingApplication = autoAccept,
        });

    internal static (string apiPath, AcceptGroupInvitationRequest request) BuildAcceptGroupInvitationCall(
        string groupId, string? entityId, string? entityType) =>
        (AcceptGroupInvitationPath, new AcceptGroupInvitationRequest
        {
            Group = Group(groupId),
            Entity = Entity(entityId, entityType),
        });

    internal static (string apiPath, RemoveGroupInvitationRequest request) BuildRemoveGroupInvitationCall(
        string groupId, string entityId, string entityType) =>
        (RemoveGroupInvitationPath, new RemoveGroupInvitationRequest
        {
            Group = Group(groupId),
            Entity = new EntityKey { Id = entityId, Type = entityType },
        });

    internal static (string apiPath, ListGroupInvitationsRequest request) BuildListGroupInvitationsCall(
        string groupId) =>
        (ListGroupInvitationsPath, new ListGroupInvitationsRequest { Group = Group(groupId) });

    internal static (string apiPath, ApplyToGroupRequest request) BuildApplyToGroupCall(
        string groupId, string? entityId, string? entityType, bool? autoAccept) =>
        (ApplyToGroupPath, new ApplyToGroupRequest
        {
            Group = Group(groupId),
            Entity = Entity(entityId, entityType),
            AutoAcceptOutstandingInvite = autoAccept,
        });

    internal static (string apiPath, AcceptGroupApplicationRequest request) BuildAcceptGroupApplicationCall(
        string groupId, string? entityId, string? entityType) =>
        (AcceptGroupApplicationPath, new AcceptGroupApplicationRequest
        {
            Group = Group(groupId),
            Entity = Entity(entityId, entityType),
        });

    internal static (string apiPath, RemoveGroupApplicationRequest request) BuildRemoveGroupApplicationCall(
        string groupId, string entityId, string entityType) =>
        (RemoveGroupApplicationPath, new RemoveGroupApplicationRequest
        {
            Group = Group(groupId),
            Entity = new EntityKey { Id = entityId, Type = entityType },
        });

    internal static (string apiPath, ListGroupApplicationsRequest request) BuildListGroupApplicationsCall(
        string groupId) =>
        (ListGroupApplicationsPath, new ListGroupApplicationsRequest { Group = Group(groupId) });

    internal static (string apiPath, BlockEntityRequest request) BuildBlockEntityCall(
        string groupId, string entityId, string entityType) =>
        (BlockEntityPath, new BlockEntityRequest
        {
            Group = Group(groupId),
            Entity = new EntityKey { Id = entityId, Type = entityType },
        });

    internal static (string apiPath, UnblockEntityRequest request) BuildUnblockEntityCall(
        string groupId, string entityId, string entityType) =>
        (UnblockEntityPath, new UnblockEntityRequest
        {
            Group = Group(groupId),
            Entity = new EntityKey { Id = entityId, Type = entityType },
        });

    internal static (string apiPath, ListGroupBlocksRequest request) BuildListGroupBlocksCall(
        string groupId) =>
        (ListGroupBlocksPath, new ListGroupBlocksRequest { Group = Group(groupId) });

    internal static (string apiPath, ListMembershipRequest request) BuildListMembershipCall(
        string? entityId, string? entityType) =>
        (ListMembershipPath, new ListMembershipRequest { Entity = Entity(entityId, entityType) });

    internal static (string apiPath, ListMembershipOpportunitiesRequest request) BuildListMembershipOpportunitiesCall(
        string? entityId, string? entityType) =>
        (ListMembershipOpportunitiesPath, new ListMembershipOpportunitiesRequest
        {
            Entity = Entity(entityId, entityType),
        });

    #endregion
}
