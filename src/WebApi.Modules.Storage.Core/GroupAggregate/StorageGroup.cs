using soft.square.net.WebApi.Modules.Storage.Core.Common;

namespace soft.square.net.WebApi.Modules.Storage.Core.GroupAggregate;

/// <summary>A named set of tenant members, used as a principal in access entries (like a Windows local group).</summary>
public sealed class StorageGroup : AggregateRoot<Guid>, ITenantOwned
{
    public const int NameLength = 100;
    public const int DescriptionLength = 500;
    public const int MaxMembers = 5000;

    private readonly List<GroupMember> _members = [];

    private StorageGroup() { } // EF Core

    public string TenantId { get; set; } = "";
    public string Name { get; private set; } = "";
    public string NameKey { get; private set; } = "";
    public string? Description { get; private set; }
    public IReadOnlyList<GroupMember> Members => _members;
    public int Version { get; private set; }

    public static StorageGroup Create(string name, string? description, IEnumerable<string> members, DateTimeOffset now)
    {
        var group = new StorageGroup { Id = Guid.CreateVersion7(now) };
        group.Update(name, description, members);
        return group;
    }

    public void Update(string name, string? description, IEnumerable<string> members)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0 || name.Length > NameLength) throw new DomainException($"A group needs a name of at most {NameLength} characters.");
        description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (description?.Length > DescriptionLength) throw new DomainException($"A description can have at most {DescriptionLength} characters.");
        var ids = members.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length > MaxMembers) throw new DomainException($"A group can have at most {MaxMembers} members.");
        if (ids.Any(m => m.Length > GroupMember.UserIdLength)) throw new DomainException("A member id is too long.");

        Name = name;
        NameKey = ItemNames.Key(name);
        Description = description;
        _members.RemoveAll(m => !ids.Contains(m.UserId, StringComparer.Ordinal));
        foreach (var id in ids.Where(id => _members.All(m => m.UserId != id))) _members.Add(new GroupMember(id));
        Version++;
    }

    public bool Contains(string userId) => _members.Any(m => m.UserId == userId);
}

public sealed class GroupMember
{
    public const int UserIdLength = 128;

    private GroupMember() { } // EF Core

    public GroupMember(string userId) => UserId = userId;

    public string UserId { get; private set; } = "";
}

/// <summary>The groups a user belongs to.</summary>
public sealed class GroupsOfUserSpec : Specification<StorageGroup>
{
    public GroupsOfUserSpec(string userId) => Query.Where(g => g.Members.Any(m => m.UserId == userId));
}

public sealed class GroupByNameSpec : SingleResultSpecification<StorageGroup>
{
    public GroupByNameSpec(string nameKey) => Query.Where(g => g.NameKey == nameKey);
}
