using soft.square.net.WebApi.Modules.Storage.UseCases.Access;
using soft.square.net.WebApi.Modules.Storage.UseCases.Groups;

namespace soft.square.net.WebApi.Modules.Storage.Web.Access;

public sealed record SetAclRequest(bool InheritsAccess, IReadOnlyList<AceDto> Entries, bool ConvertInherited, int Version);

public sealed record SetOwnerRequest(string? OwnerId);

/// <summary>The Security dialog (access lists, owner, effective access) and Storage groups.</summary>
public static class AccessEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/items/{id:guid}/acl", async (Guid id, ISender sender, CancellationToken ct) =>
                (await sender.Send(new GetAclQuery(id), ct)).ToOkResult())
            .WithName("Storage_GetAcl")
            .WithSummary("An item's owner and access list: its own entries, then the inherited ones (Read permissions).");

        app.MapPut("/items/{id:guid}/acl", async (Guid id, SetAclRequest request, ISender sender, CancellationToken ct) =>
                (await sender.Send(new SetAclCommand(id, request.InheritsAccess, request.Entries, request.ConvertInherited, request.Version), ct)).ToOkResult())
            .WithName("Storage_SetAcl")
            .WithSummary("Set an item's own entries and inheritance (Change permissions).");

        app.MapPut("/items/{id:guid}/owner", async (Guid id, SetOwnerRequest request, ISender sender, CancellationToken ct) =>
                (await sender.Send(new SetOwnerCommand(id, request.OwnerId), ct)).ToOkResult())
            .WithName("Storage_SetOwner")
            .WithSummary("Take ownership (no owner id; Take ownership), or give it to someone (Storage admins).");

        app.MapGet("/items/{id:guid}/effective-access", async (Guid id, string userId, ISender sender, CancellationToken ct) =>
                (await sender.Send(new GetEffectiveAccessQuery(id, userId), ct)).ToOkResult())
            .WithName("Storage_GetEffectiveAccess")
            .WithSummary("What a member may do with the item, through their entries, groups and grants (Read permissions).");

        app.MapGet("/groups", async (ISender sender, CancellationToken ct) => (await sender.Send(new ListGroupsQuery(), ct)).ToOkResult())
            .WithName("Storage_ListGroups")
            .WithSummary("Every Storage group (the principal picker).");
    }

    /// <summary>Group administration: Storage admins only.</summary>
    public static void MapAdmin(IEndpointRouteBuilder app)
    {
        app.MapPost("/groups", async (GroupInput group, ISender sender, CancellationToken ct) =>
                (await sender.Send(new CreateGroupCommand(group), ct)).ToCreatedResult(g => $"groups/{g.Id}"))
            .WithName("Storage_CreateGroup")
            .WithSummary("Create a Storage group.");

        app.MapPut("/groups/{id:guid}", async (Guid id, int version, GroupInput group, ISender sender, CancellationToken ct) =>
                (await sender.Send(new UpdateGroupCommand(id, group, version), ct)).ToOkResult())
            .WithName("Storage_UpdateGroup")
            .WithSummary("Rename a group or change its members (version: the one you read).");

        app.MapDelete("/groups/{id:guid}", async (Guid id, ISender sender, CancellationToken ct) =>
                (await sender.Send(new DeleteGroupCommand(id), ct)).ToNoContentResult())
            .WithName("Storage_DeleteGroup")
            .WithSummary("Delete a group; entries that name it match nobody.");
    }
}
