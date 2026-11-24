using Microsoft.EntityFrameworkCore;
using soft.square.net.WebApi.Modules.Storage.Core.AccessControl;
using soft.square.net.WebApi.Modules.Storage.Core.ItemAggregate;
using soft.square.net.WebApi.Modules.Storage.Infrastructure.Data;

namespace soft.square.net.WebApi.Modules.Storage.IntegrationTests;

/// <summary>The mappings (owned entries and versions, the unique name index) and the store's tree queries, on a real database.</summary>
public sealed class StorageStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);
    private readonly StorageDatabase _database = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _database.Dispose();

    private static FileVersion Version() => FileVersion.Create($"t/acme/2026/11/{Guid.NewGuid():N}", 5, "text/plain", "ana", Now);

    /// <summary>Files / a / b / doc.txt, saved.</summary>
    private async Task<(Item Root, Item A, Item B, Item Doc)> SeedAsync()
    {
        await using var db = _database.For("acme");
        var store = new StorageStore(db);
        var root = await store.EnsureAsync(c => store.TopFolderAsync(FolderRole.Root, c), () => Item.CreateTop("acme", FolderRole.Root, "Files",
            [AccessEntry.Create(PrincipalKind.Everyone, null, null, AceType.Allow, StorageRights.Read, AceInheritance.Subfolders | AceInheritance.Files)], Now), Ct);
        var a = Item.CreateFolder(root, "a", "ana", Now);
        var b = Item.CreateFolder(a, "b", "ana", Now);
        var doc = Item.CreateFile(b, "doc.txt", "ana", Version(), Now);
        store.Add(a);
        store.Add(b);
        store.Add(doc);
        await store.SaveAsync(Ct);
        return (root, a, b, doc);
    }

    [Fact]
    public async Task LoadsTheChainWithEntriesAndVersions()
    {
        var (root, a, b, doc) = await SeedAsync();

        await using var db = _database.For("acme");
        var chain = await new StorageStore(db).ChainAsync(doc.Id, Ct);

        chain.ShouldNotBeNull();
        chain.Select(i => i.Id).ShouldBe([root.Id, a.Id, b.Id, doc.Id]);
        chain[0].Entries.Single().Rights.ShouldBe(StorageRights.Read);
        chain[^1].Versions.Single().Number.ShouldBe(1);
        chain[0].Role.ShouldBe(FolderRole.Root);
    }

    [Fact]
    public async Task AnotherTenantSeesNothing()
    {
        var (_, _, _, doc) = await SeedAsync();
        await using var db = _database.For("globex");
        (await new StorageStore(db).ChainAsync(doc.Id, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task TheTopFolderIsCreatedOnce()
    {
        var (root, _, _, _) = await SeedAsync();
        await using var db = _database.For("acme");
        var store = new StorageStore(db);
        // Another replica's create raced with this one: the first lookup missed, the insert collides on the key, and
        // the folder that won comes back.
        var lookups = 0;
        var again = await store.EnsureAsync(
            c => lookups++ == 0 ? Task.FromResult<Item?>(null) : store.TopFolderAsync(FolderRole.Root, c),
            () => Item.CreateTop("acme", FolderRole.Root, "Files", [], Now), Ct);

        again.Id.ShouldBe(root.Id);
        (await db.Items.CountAsync(i => i.Role == FolderRole.Root, Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task NamesAreUniquePerFolderButFreedByDelete()
    {
        var (_, a, _, _) = await SeedAsync();

        await using (var db = _database.For("acme"))
        {
            db.Items.Add(Item.CreateFolder((await db.Items.FirstAsync(i => i.Id == a.Id, Ct)), "B", "ana", Now));
            await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        }

        await using (var db = _database.For("acme"))
        {
            var store = new StorageStore(db);
            var b = (await store.ChildByNameAsync(a.Id, "b", Ct))!;
            b.Delete("ana", Now);
            await store.SaveAsync(Ct);
            store.Add(Item.CreateFolder((await store.ChainAsync(a.Id, Ct))![^1], "b", "ana", Now));
            await store.SaveAsync(Ct);
            (await store.ChildrenAsync(a.Id, Ct)).Count.ShouldBe(1);
        }
    }

    [Fact]
    public async Task MovingAFolderRewritesThePathsBelowIt()
    {
        var (root, a, b, doc) = await SeedAsync();

        await using (var db = _database.For("acme"))
        {
            var store = new StorageStore(db);
            var target = Item.CreateFolder(root, "target", "ana", Now);
            store.Add(target);
            await store.SaveAsync(Ct);
            var moving = (await store.ChainAsync(a.Id, Ct))![^1];
            var height = await store.SubtreeHeightAsync(moving, Ct);
            height.ShouldBe(2);
            var (oldPath, oldDepth) = moving.MoveTo(target, height, "ana", Now);
            await store.SaveMoveAsync(moving, oldPath, oldDepth, Ct);
        }

        await using (var db = _database.For("acme"))
        {
            var chain = await new StorageStore(db).ChainAsync(doc.Id, Ct);
            chain!.Select(i => i.Name).ShouldBe(["Files", "target", "a", "b", "doc.txt"]);
            chain[^1].Depth.ShouldBe(4);
        }
    }

    [Fact]
    public async Task LiveDescendantsSkipDeletedSubtrees()
    {
        var (root, a, b, doc) = await SeedAsync();
        await using var db = _database.For("acme");
        var store = new StorageStore(db);
        var other = Item.CreateFile(a, "keep.txt", "ana", Version(), Now);
        store.Add(other);
        (await store.ChainAsync(b.Id, Ct))![^1].Delete("ana", Now);
        await store.SaveAsync(Ct);

        var live = await store.LiveDescendantsAsync((await store.ChainAsync(a.Id, Ct))![^1], Ct);

        live.Select(i => i.Name).ShouldBe(["keep.txt"]);
        (await store.WithSubfoldersAsync([root.Id, a.Id], Ct)).ShouldBe([root.Id]);
    }

    [Fact]
    public async Task UsageCountsBlobsOnceAndUploadsUnderWay()
    {
        var (_, a, _, _) = await SeedAsync();
        await using (var db = _database.For("acme"))
        {
            var store = new StorageStore(db);
            store.Add(Core.BlobAggregate.Blob.Create($"t/acme/2026/11/{Guid.NewGuid():N}", "minio|files|", 100, Now));
            store.Add(Core.BlobAggregate.Blob.Create($"t/acme/2026/11/{Guid.NewGuid():N}", "", 20, Now));
            store.Add(Core.UploadAggregate.UploadSession.Start(a.Id, "big.bin", null, 7, 1024, 1000, Core.UploadAggregate.NameConflict.Fail,
                $"t/acme/2026/11/{Guid.NewGuid():N}", "", "ana", Now, TimeSpan.FromHours(1)));
            await store.SaveAsync(Ct);
        }

        await using (var db = _database.For("acme")) (await new StorageStore(db).UsageAsync(Ct)).ShouldBe((120L, 7L));
        await using (var db = _database.For("globex")) (await new StorageStore(db).UsageAsync(Ct)).ShouldBe((0L, 0L));
    }

    [Fact]
    public async Task TheRecycleBinQueries()
    {
        var (root, a, b, doc) = await SeedAsync();
        await using (var db = _database.For("acme"))
        {
            var store = new StorageStore(db);
            var loaded = (await store.ChainAsync(doc.Id, Ct))!;
            loaded[^1].Delete("ana", Now.AddDays(-40));
            loaded[1].Delete("bob", Now.AddDays(-1)); // a
            var used = Core.BlobAggregate.Blob.Create("t/acme/used", "", 5, Now);
            var unused = Core.BlobAggregate.Blob.Create("t/acme/unused", "", 5, Now);
            unused.Release();
            store.Add(used);
            store.Add(unused);
            store.Add(Core.UploadAggregate.UploadSession.Start(a.Id, "old.bin", null, 7, 1024, 1000, Core.UploadAggregate.NameConflict.Fail,
                "t/acme/old", "", "ana", Now.AddDays(-3), TimeSpan.FromDays(1)));
            store.Add(Core.UploadAggregate.UploadSession.Start(a.Id, "new.bin", null, 7, 1024, 1000, Core.UploadAggregate.NameConflict.Fail,
                "t/acme/new", "", "ana", Now, TimeSpan.FromDays(1)));
            await store.SaveAsync(Ct);
        }

        await using (var db = _database.For("acme"))
        {
            var store = new StorageStore(db);
            (await store.RecycleBinAsync(null, Ct)).Select(i => i.Id).ShouldBe([a.Id, doc.Id]); // newest first
            (await store.RecycleBinAsync("ana", Ct)).Select(i => i.Id).ShouldBe([doc.Id]);
            (await store.DeletedBeforeAsync(Now.AddDays(-30), 10, Ct)).Select(i => i.Id).ShouldBe([doc.Id]);
            (await store.SubtreeAsync((await store.ChainAsync(a.Id, Ct))![^1], Ct)).Select(i => i.Id).ShouldBe([b.Id, doc.Id]);
            (await store.UnusedBlobsAsync(10, Ct)).Select(x => x.Id).ShouldBe(["t/acme/unused"]);
            (await store.ExpiredUploadsAsync(Now, 10, Ct)).Select(u => u.Name).ShouldBe(["old.bin"]);
        }
        await using (var db = _database.For("globex")) (await new StorageStore(db).RecycleBinAsync(null, Ct)).ShouldBeEmpty();
    }
}
