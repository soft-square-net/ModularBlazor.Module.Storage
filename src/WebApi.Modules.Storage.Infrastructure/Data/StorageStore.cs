using Microsoft.EntityFrameworkCore;
using soft.square.net.WebApi.Modules.Storage.Core.BlobAggregate;
using soft.square.net.WebApi.Modules.Storage.Core.EventAggregate;
using soft.square.net.WebApi.Modules.Storage.Core.Common;
using soft.square.net.WebApi.Modules.Storage.Core.ItemAggregate;
using soft.square.net.WebApi.Modules.Storage.Core.PersonAggregate;
using soft.square.net.WebApi.Modules.Storage.Core.ShareAggregate;
using soft.square.net.WebApi.Modules.Storage.Core.AccessControl;
using soft.square.net.WebApi.Modules.Storage.Core.UploadAggregate;
using soft.square.net.WebApi.Modules.Storage.UseCases.Items;

namespace soft.square.net.WebApi.Modules.Storage.Infrastructure.Data;

/// <summary><see cref="IStorageStore"/> on the request's <see cref="StorageDbContext"/> (the current tenant's database).</summary>
internal sealed class StorageStore(StorageDbContext db) : IStorageStore
{
    public async Task<IReadOnlyList<Item>?> ChainAsync(Guid id, CancellationToken ct)
    {
        var item = await db.Items.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (item is null) return null;
        var ancestorIds = item.AncestorIds();
        if (ancestorIds.Count == 0) return [item];
        var ancestors = await db.Items.Where(i => ancestorIds.Contains(i.Id)).ToListAsync(ct);
        // A missing ancestor means a broken tree (or another tenant's path): treat the item as unreachable.
        if (ancestors.Count != ancestorIds.Count) return null;
        return [.. ancestors.OrderBy(i => i.Depth), item];
    }

    public async Task<IReadOnlyList<Item>> ChildrenAsync(Guid folderId, CancellationToken ct) =>
        await db.Items.Where(i => i.ParentId == folderId && i.DeletedAt == null).ToListAsync(ct);

    public Task<Item?> ChildByNameAsync(Guid folderId, string name, CancellationToken ct)
    {
        var key = ItemNames.Key(name);
        return db.Items.FirstOrDefaultAsync(i => i.ParentId == folderId && i.NameKey == key && i.DeletedAt == null, ct);
    }

    public async Task<IReadOnlySet<string>> NameKeysAsync(Guid folderId, CancellationToken ct) =>
        (await db.Items.Where(i => i.ParentId == folderId && i.DeletedAt == null).Select(i => i.NameKey).ToListAsync(ct)).ToHashSet();

    public async Task<IReadOnlySet<Guid>> WithSubfoldersAsync(IReadOnlyCollection<Guid> folderIds, CancellationToken ct)
    {
        if (folderIds.Count == 0) return new HashSet<Guid>();
        var ids = folderIds.Select(id => (Guid?)id).ToArray();
        var parents = await db.Items
            .Where(i => ids.Contains(i.ParentId) && i.Kind == ItemKind.Folder && i.DeletedAt == null)
            .Select(i => i.ParentId!.Value).Distinct().ToListAsync(ct);
        return parents.ToHashSet();
    }

    public async Task<IReadOnlyList<Item>> LiveDescendantsAsync(Item folder, CancellationToken ct)
    {
        var all = await Below(folder).OrderBy(i => i.Depth).ToListAsync(ct);
        var excluded = new HashSet<Guid>();
        var live = new List<Item>();
        foreach (var item in all)
        {
            if (item.IsDeleted || item.ParentId is { } parent && excluded.Contains(parent)) excluded.Add(item.Id);
            else live.Add(item);
        }
        return live;
    }

    public async Task<IReadOnlyList<Item>> SubtreeAsync(Item folder, CancellationToken ct) =>
        await Below(folder).OrderBy(i => i.Depth).ToListAsync(ct);

    public async Task<IReadOnlyList<Item>> RecycleBinAsync(string? deletedBy, CancellationToken ct)
    {
        var query = db.Items.Where(i => i.DeletedAt != null);
        if (deletedBy is not null) query = query.Where(i => i.DeletedBy == deletedBy);
        return await query.OrderByDescending(i => i.DeletedAt).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Item>> DeletedBeforeAsync(DateTimeOffset before, int take, CancellationToken ct) =>
        await db.Items.Where(i => i.DeletedAt != null && i.DeletedAt < before).OrderBy(i => i.DeletedAt).Take(take).ToListAsync(ct);

    public async Task<IReadOnlyList<Blob>> UnusedBlobsAsync(int take, CancellationToken ct) =>
        await db.Blobs.Where(b => b.References <= 0).OrderBy(b => b.CreatedAt).Take(take).ToListAsync(ct);

    public async Task<IReadOnlyList<UploadSession>> ExpiredUploadsAsync(DateTimeOffset now, int take, CancellationToken ct) =>
        await db.Uploads.Where(u => u.ExpiresAt < now).OrderBy(u => u.ExpiresAt).Take(take).ToListAsync(ct);

    public async Task<int> SubtreeHeightAsync(Item folder, CancellationToken ct)
    {
        var deepest = await Below(folder).Select(i => (int?)i.Depth).MaxAsync(ct);
        return deepest is { } depth ? depth - folder.Depth : 0;
    }

    public Task<Item?> TopFolderAsync(FolderRole role, CancellationToken ct) =>
        db.Items.FirstOrDefaultAsync(i => i.Role == role && i.ParentId == null, ct);

    public Task<Item?> HomeAsync(Guid homesId, string userId, CancellationToken ct)
    {
        var key = ItemNames.Key(userId);
        return db.Items.FirstOrDefaultAsync(i => i.ParentId == homesId && i.Role == FolderRole.Home && i.NameKey == key, ct);
    }

    public async Task<IReadOnlyList<Blob>> BlobsAsync(IReadOnlyCollection<string> keys, CancellationToken ct) =>
        keys.Count == 0 ? [] : await db.Blobs.Where(b => keys.Contains(b.Id)).ToListAsync(ct);

    public async Task<(long Used, long Pending)> UsageAsync(CancellationToken ct)
    {
        var used = await db.Blobs.SumAsync(b => (long?)b.Size, ct) ?? 0;
        var pending = await db.Uploads.SumAsync(u => (long?)u.Size, ct) ?? 0;
        return (used, pending);
    }

    public Task<UploadSession?> UploadAsync(Guid id, CancellationToken ct) =>
        db.Uploads.FirstOrDefaultAsync(u => u.Id == id, ct);

    public async Task<Item> EnsureAsync(Func<CancellationToken, Task<Item?>> find, Func<Item> create, CancellationToken ct)
    {
        if (await find(ct) is { } found) return found;
        var item = create();
        db.Items.Add(item);
        try
        {
            await db.SaveChangesAsync(ct);
            return item;
        }
        catch (DbUpdateException)
        {
            // Another request created it first (same key, or same name in the folder).
            db.Entry(item).State = EntityState.Detached;
            return await find(ct) ?? throw new InvalidOperationException($"Could not create the system folder '{item.Name}'.");
        }
    }

    public void Add<T>(T entity) where T : class => db.Add(entity);

    public void Remove<T>(T entity) where T : class => db.Remove(entity);

    public async Task<IReadOnlyList<Item>> SharedWithAsync(string userId, int take, CancellationToken ct) =>
        await db.Items.Where(i => i.DeletedAt == null && i.Entries.Any(e =>
                e.PrincipalKind == PrincipalKind.User && e.PrincipalId == userId && e.Type == AceType.Allow))
            .OrderByDescending(i => i.ModifiedAt).Take(take).ToListAsync(ct);

    public Task<StoragePerson?> PersonAsync(string userId, CancellationToken ct) =>
        db.People.FirstOrDefaultAsync(p => p.UserId == userId, ct);

    public async Task<IReadOnlyList<StoragePerson>> SearchPeopleAsync(string search, int take, CancellationToken ct) =>
        await db.People.Where(p => p.SearchKey.Contains(search)).OrderBy(p => p.Name).Take(take).ToListAsync(ct);

    public async Task<IReadOnlyList<StoragePerson>> PeopleAsync(IReadOnlyCollection<string> userIds, CancellationToken ct) =>
        userIds.Count == 0 ? [] : await db.People.Where(p => userIds.Contains(p.UserId)).ToListAsync(ct);

    public async Task<IReadOnlyList<ShareLink>> LinksOfAsync(Guid itemId, CancellationToken ct) =>
        await db.ShareLinks.Where(l => l.ItemId == itemId).OrderBy(l => l.CreatedAt).ToListAsync(ct);

    public Task<ShareLink?> LinkAsync(Guid id, CancellationToken ct) => db.ShareLinks.FirstOrDefaultAsync(l => l.Id == id, ct);

    public Task<ShareLink?> LinkByTokenAsync(string token, CancellationToken ct) => db.ShareLinks.FirstOrDefaultAsync(l => l.Token == token, ct);

    public async Task<IReadOnlyList<StorageEvent>> EventsAsync(long after, DateTimeOffset upTo, int take, CancellationToken ct) =>
        await db.Events.Where(e => e.Id > after && e.At <= upTo).OrderBy(e => e.Id).Take(take).ToListAsync(ct);

    public Task<int> DeleteEventsBeforeAsync(DateTimeOffset before, CancellationToken ct) =>
        db.Events.Where(e => e.At < before).ExecuteDeleteAsync(ct);

    public async Task<IReadOnlyList<Blob>> BlobsToScanAsync(int take, CancellationToken ct) =>
        await db.Blobs.Where(b => b.Scan == ScanStatus.Pending && b.References > 0).OrderBy(b => b.CreatedAt).Take(take).ToListAsync(ct);

    public async Task<IReadOnlyList<Blob>> BlobsToThumbnailAsync(int take, CancellationToken ct) =>
        await db.Blobs.Where(b => b.Thumbnail == ThumbnailState.Pending && b.Scan == ScanStatus.Clean && b.References > 0)
            .OrderBy(b => b.CreatedAt).Take(take).ToListAsync(ct);

    public async Task<IReadOnlyList<Item>> FilesWithBlobAsync(string blobKey, CancellationToken ct) =>
        await db.Items.Where(i => i.Kind == ItemKind.File && i.Versions.Any(v => v.BlobKey == blobKey)).ToListAsync(ct);

    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);

    public async Task SaveMoveAsync(Item item, string oldPath, int oldDepth, CancellationToken ct)
    {
        // Everything below still has the old prefix in the database; rewrite it in memory and save it all at once.
        var below = await db.Items.Where(i => i.Path.StartsWith(oldPath) && i.Id != item.Id).ToListAsync(ct);
        foreach (var descendant in below) descendant.Rebase(oldPath, item.Path, item.Depth - oldDepth);
        await db.SaveChangesAsync(ct);
    }

    private IQueryable<Item> Below(Item folder)
    {
        var path = folder.Path;
        var id = folder.Id;
        return db.Items.Where(i => i.Path.StartsWith(path) && i.Id != id);
    }
}
