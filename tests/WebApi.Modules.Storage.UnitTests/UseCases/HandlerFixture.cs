
using soft.square.net.WebApi.Contracts.Tenancy;
using soft.square.net.WebApi.Modules.Storage.Core.GroupAggregate;
using soft.square.net.WebApi.Modules.Storage.UseCases.Access;
using soft.square.net.WebApi.Modules.Storage.UseCases.Items;

namespace soft.square.net.WebApi.Modules.Storage.UnitTests.UseCases;

/// <summary>
/// The pieces a handler needs: a substituted store (the tree below is served from memory), the platform's access
/// scopes for the caller "ana", and the real StorageCaller / ItemAccess on top.
/// </summary>
public sealed class HandlerFixture
{
    public IStorageStore Store { get; } = Substitute.For<IStorageStore>();
    public IAccessScopes Scopes { get; } = Substitute.For<IAccessScopes>();
    public IReadRepository<StorageGroup> Groups { get; } = Substitute.For<IReadRepository<StorageGroup>>();
    public ITenantContext Tenant { get; } = Substitute.For<ITenantContext>();
    public FixedClock Clock { get; } = new(Tree.Now);
    public StorageCaller Caller { get; }
    public ItemAccess Access { get; }

    private readonly Dictionary<Guid, Item> _items = [];

    public HandlerFixture(string me = "ana", bool admin = false)
    {
        Scopes.UserId.Returns(me);
        Scopes.HasPermissionAsync(StoragePermissions.Admin, Arg.Any<CancellationToken>()).Returns(admin);
        Groups.ListAsync(Arg.Any<ISpecification<StorageGroup>>(), Arg.Any<CancellationToken>()).Returns([]);
        Tenant.Tenant.Returns(new TenantInfo("acme", "Acme", TenantStatus.Active, [], "", ""));
        Caller = new StorageCaller(Scopes, Groups, Clock);
        Access = new ItemAccess(Store, Caller);

        Store.ChainAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(call => Chain(call.Arg<Guid>()));
        Store.ChildrenAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => _items.Values.Where(i => i.ParentId == call.Arg<Guid>() && !i.IsDeleted).ToArray());
        Store.ChildByNameAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
            _items.Values.FirstOrDefault(i => i.ParentId == call.ArgAt<Guid>(0) && !i.IsDeleted
                && i.NameKey == soft.square.net.WebApi.Modules.Storage.Core.Common.ItemNames.Key(call.ArgAt<string>(1))));
        Store.NameKeysAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(call =>
            (IReadOnlySet<string>)_items.Values.Where(i => i.ParentId == call.Arg<Guid>() && !i.IsDeleted).Select(i => i.NameKey).ToHashSet());
        Store.WithSubfoldersAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(new HashSet<Guid>());
        Store.When(s => s.Add(Arg.Any<Item>())).Do(call => Put(call.Arg<Item>()));
    }

    public Item Put(Item item)
    {
        _items[item.Id] = item;
        return item;
    }

    public IReadOnlyList<Item> Items => _items.Values.ToArray();

    private IReadOnlyList<Item>? Chain(Guid id) =>
        _items.TryGetValue(id, out var item) ? [.. item.AncestorIds().Select(a => _items[a]), item] : null;

    public static CancellationToken Ct => TestContext.Current.CancellationToken;
}

public sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
