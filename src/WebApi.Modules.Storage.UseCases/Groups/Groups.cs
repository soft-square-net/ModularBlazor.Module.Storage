using soft.square.net.WebApi.Modules.Storage.Core.Common;
using soft.square.net.WebApi.Modules.Storage.Core.GroupAggregate;
using soft.square.net.WebApi.Modules.Storage.UseCases.Access;

namespace soft.square.net.WebApi.Modules.Storage.UseCases.Groups;

public sealed record GroupDto(Guid Id, string Name, string? Description, IReadOnlyList<string> Members, int Version)
{
    public static GroupDto From(StorageGroup g) => new(g.Id, g.Name, g.Description, g.Members.Select(m => m.UserId).ToArray(), g.Version);
}

/// <summary>The input of create and update.</summary>
public sealed record GroupInput(string Name, string? Description, IReadOnlyList<string>? Members);

/// <summary>Every Storage group, for the Security dialog's principal picker (any Storage user).</summary>
public sealed record ListGroupsQuery : IQuery<Result<IReadOnlyList<GroupDto>>>;

public sealed class ListGroupsHandler(IReadRepository<StorageGroup> groups) : IQueryHandler<ListGroupsQuery, Result<IReadOnlyList<GroupDto>>>
{
    public async ValueTask<Result<IReadOnlyList<GroupDto>>> Handle(ListGroupsQuery query, CancellationToken ct) =>
        (await groups.ListAsync(ct)).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).Select(GroupDto.From).ToArray();
}

public sealed record CreateGroupCommand(GroupInput Group) : ICommand<Result<GroupDto>>;

public sealed class CreateGroupHandler(IRepository<StorageGroup> groups, StorageCaller caller) : ICommandHandler<CreateGroupCommand, Result<GroupDto>>
{
    public async ValueTask<Result<GroupDto>> Handle(CreateGroupCommand command, CancellationToken ct)
    {
        if (!await caller.IsAdminAsync(ct)) return Result<GroupDto>.Forbidden("Only Storage administrators manage groups.");
        var group = StorageGroup.Create(command.Group.Name, command.Group.Description, command.Group.Members ?? [], caller.Now);
        if (await groups.FirstOrDefaultAsync(new GroupByNameSpec(group.NameKey), ct) is not null)
            return Result<GroupDto>.Conflict($"There is already a group named '{group.Name}'.");
        return GroupDto.From(await groups.AddAsync(group, ct));
    }
}

public sealed record UpdateGroupCommand(Guid Id, GroupInput Group, int Version) : ICommand<Result<GroupDto>>;

public sealed class UpdateGroupHandler(IRepository<StorageGroup> groups, StorageCaller caller) : ICommandHandler<UpdateGroupCommand, Result<GroupDto>>
{
    public async ValueTask<Result<GroupDto>> Handle(UpdateGroupCommand command, CancellationToken ct)
    {
        if (!await caller.IsAdminAsync(ct)) return Result<GroupDto>.Forbidden("Only Storage administrators manage groups.");
        if (await groups.GetByIdAsync(command.Id, ct) is not { } group) return Result<GroupDto>.NotFound("The group does not exist.");
        if (group.Version != command.Version) return Result<GroupDto>.Conflict("Someone else changed this group first; refresh and try again.");
        var key = ItemNames.Key((command.Group.Name ?? "").Trim());
        if (await groups.FirstOrDefaultAsync(new GroupByNameSpec(key), ct) is { } other && other.Id != group.Id)
            return Result<GroupDto>.Conflict($"There is already a group named '{other.Name}'.");
        group.Update(command.Group.Name!, command.Group.Description, command.Group.Members ?? []);
        await groups.UpdateAsync(group, ct);
        return GroupDto.From(group);
    }
}

/// <summary>Deletes a group. Entries that name it stay, and match nobody (Windows shows them as an unknown account).</summary>
public sealed record DeleteGroupCommand(Guid Id) : ICommand<Result>;

public sealed class DeleteGroupHandler(IRepository<StorageGroup> groups, StorageCaller caller) : ICommandHandler<DeleteGroupCommand, Result>
{
    public async ValueTask<Result> Handle(DeleteGroupCommand command, CancellationToken ct)
    {
        if (!await caller.IsAdminAsync(ct)) return Result.Forbidden("Only Storage administrators manage groups.");
        if (await groups.GetByIdAsync(command.Id, ct) is not { } group) return Result.NotFound("The group does not exist.");
        await groups.DeleteAsync(group, ct);
        return Result.Success();
    }
}
