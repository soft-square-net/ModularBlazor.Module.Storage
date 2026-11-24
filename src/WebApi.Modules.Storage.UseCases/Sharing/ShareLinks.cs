using soft.square.net.WebApi.Contracts.Tenancy;
using soft.square.net.WebApi.Modules.Storage.Core.ShareAggregate;
using soft.square.net.WebApi.Modules.Storage.UseCases.Access;
using soft.square.net.WebApi.Modules.Storage.UseCases.Blobs;
using soft.square.net.WebApi.Modules.Storage.UseCases.Items;

namespace soft.square.net.WebApi.Modules.Storage.UseCases.Sharing;

/// <summary>A new "anyone with the link" link (Share on the item); a password is kept only as a hash.</summary>
public sealed record CreateShareLinkCommand(Guid Id, DateTimeOffset? ExpiresAt, string? Password, int? MaxDownloads) : ICommand<Result<ShareLinkDto>>;

public sealed class CreateShareLinkHandler(IStorageStore store, ItemAccess access, StorageCaller caller)
    : ICommandHandler<CreateShareLinkCommand, Result<ShareLinkDto>>
{
    public const int MaxLinksPerItem = 20;

    public async ValueTask<Result<ShareLinkDto>> Handle(CreateShareLinkCommand command, CancellationToken ct)
    {
        var reached = await access.ReachAsync(command.Id, StorageRights.Share, ct);
        if (!reached.IsSuccess) return reached.As<ShareLinkDto>();
        if (!reached.Value.Item.IsFolder && reached.Value.Item.Scan != ScanStatus.Clean)
            return Result<ShareLinkDto>.Conflict(FileVersions.NotSafe(reached.Value.Item.Name, reached.Value.Item.Scan, null));
        if ((await store.LinksOfAsync(command.Id, ct)).Count >= MaxLinksPerItem)
            return Result<ShareLinkDto>.Conflict($"An item can have at most {MaxLinksPerItem} links; remove one first.");
        var link = ShareLink.Create(command.Id, caller.UserId, command.ExpiresAt, command.Password, command.MaxDownloads, caller.Now);
        store.Add(link);
        await store.SaveAsync(ct);
        return ShareLinkDto.From(link, caller.Now);
    }
}

/// <summary>Remove a link: its creator, anyone with Share on the item, or a Storage admin. The URL stops working at once.</summary>
public sealed record DeleteShareLinkCommand(Guid LinkId) : ICommand<Result>;

public sealed class DeleteShareLinkHandler(IStorageStore store, ItemAccess access, StorageCaller caller) : ICommandHandler<DeleteShareLinkCommand, Result>
{
    public async ValueTask<Result> Handle(DeleteShareLinkCommand command, CancellationToken ct)
    {
        var link = await store.LinkAsync(command.LinkId, ct);
        if (link is null) return Result.NotFound("The link does not exist.");
        if (link.CreatedBy != caller.UserId && !await caller.IsAdminAsync(ct))
        {
            var reached = await access.ReachAsync(link.ItemId, StorageRights.Share, ct);
            if (!reached.IsSuccess) return reached.Status == ResultStatus.NotFound ? Result.Forbidden(StorageMessages.Denied) : Result.Forbidden([.. reached.Errors]);
        }
        store.Remove(link);
        await store.SaveAsync(ct);
        return Result.Success();
    }
}

// ───────── the anonymous side (run in the link's tenant: the URL names it) ─────────

/// <summary>An item as a share link shows it: no rights, owners or ids of anything outside the shared item.</summary>
public sealed record SharedEntryDto(Guid Id, string Name, ItemKind Kind, long Size, string? ContentType, DateTimeOffset ModifiedAt)
{
    public static SharedEntryDto From(Item i) => new(i.Id, i.Name, i.Kind, i.IsFolder ? 0 : i.Size, i.ContentType, i.ModifiedAt);
}

/// <summary>
/// What a link opens: the shared item, and for a folder the folder being browsed (<see cref="Folder"/>, the shared one or
/// one below it), the path from the shared folder down to it, and its contents.
/// </summary>
public sealed record SharedView(SharedEntryDto Shared, DateTimeOffset? ExpiresAt, SharedEntryDto Folder, IReadOnlyList<SharedEntryDto> Path,
    IReadOnlyList<SharedEntryDto> Items);

/// <summary>Open a share link (anonymous): the shared file, or a folder of the shared tree (<see cref="FolderId"/>, default its top).</summary>
public sealed record OpenShareQuery(string Token, string? Password, Guid? FolderId) : IQuery<Result<SharedView>>;

public sealed class OpenShareHandler(IStorageStore store, TimeProvider clock) : IQueryHandler<OpenShareQuery, Result<SharedView>>
{
    public async ValueTask<Result<SharedView>> Handle(OpenShareQuery query, CancellationToken ct)
    {
        var opened = await SharedTree.OpenAsync(store, clock, query.Token, query.Password, query.FolderId, ct);
        if (!opened.IsSuccess) return opened.As<SharedView>();
        var (link, shared, chain) = opened.Value;
        if (!shared.IsFolder)
        {
            var self = SharedEntryDto.From(shared);
            return new SharedView(self, link.ExpiresAt, self, [self], []);
        }
        if (!chain[^1].IsFolder) return Result<SharedView>.Invalid(new ValidationError("That is a file: download it instead."));
        var path = chain.SkipWhile(i => i.Id != shared.Id).Select(SharedEntryDto.From).ToArray();
        var items = (await store.ChildrenAsync(chain[^1].Id, ct))
            .OrderBy(i => i.IsFolder ? 0 : 1).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(SharedEntryDto.From).ToArray();
        return new SharedView(SharedEntryDto.From(shared), link.ExpiresAt, path[^1], path, items);
    }
}

/// <summary>
/// A short-lived URL for a file the link shares (anonymous): the shared file, or a file in the shared folder. Each URL
/// counts as a download against the link's limit.
/// </summary>
public sealed record OpenSharedFileQuery(string Token, string? Password, Guid FileId, bool Inline) : IQuery<Result<DownloadLink>>;

public sealed class OpenSharedFileHandler(IStorageStore store, IBlobStores stores, IDownloadTickets tickets, ITenantContext tenant, TimeProvider clock,
    IOptions<StorageOptions> options) : IQueryHandler<OpenSharedFileQuery, Result<DownloadLink>>
{
    public async ValueTask<Result<DownloadLink>> Handle(OpenSharedFileQuery query, CancellationToken ct)
    {
        var opened = await SharedTree.OpenAsync(store, clock, query.Token, query.Password, query.FileId, ct);
        if (!opened.IsSuccess) return opened.As<DownloadLink>();
        var (link, _, chain) = opened.Value;
        var file = chain[^1];
        if (file.IsFolder || file.Current is not { } version) return Result<DownloadLink>.Invalid(new ValidationError("A folder has no content to download."));
        var safe = await FileVersions.SafeAsync(store, file, version, ct);
        if (!safe.IsSuccess) return safe.As<DownloadLink>(); // not counted
        link.CountDownload();
        await store.SaveAsync(ct);
        var lifetime = options.Value.DownloadLinkLifetime;
        var url = await FileVersions.LinkAsync(store, stores, tickets, tenant.Required.Id, file, version, query.Inline, lifetime, ct);
        return new DownloadLink(url, clock.GetUtcNow() + lifetime);
    }
}

/// <summary>A file in a public folder (anonymous, for &lt;img&gt; and links in published pages): a short-lived URL to it.</summary>
public sealed record OpenPublicFileQuery(Guid FileId) : IQuery<Result<DownloadLink>>;

public sealed class OpenPublicFileHandler(IStorageStore store, IBlobStores stores, IDownloadTickets tickets, ITenantContext tenant, TimeProvider clock,
    IOptions<StorageOptions> options) : IQueryHandler<OpenPublicFileQuery, Result<DownloadLink>>
{
    public async ValueTask<Result<DownloadLink>> Handle(OpenPublicFileQuery query, CancellationToken ct)
    {
        var chain = await store.ChainAsync(query.FileId, ct);
        // Not found whether it is missing, private or deleted: a public URL tells nothing about private files.
        if (chain is null || chain.Any(i => i.IsDeleted) || chain[^1].IsFolder || !chain.Any(i => i.IsPublic) || chain[^1].Current is not { } version)
            return Result<DownloadLink>.NotFound("Not found.");
        if (!(await FileVersions.SafeAsync(store, chain[^1], version, ct)).IsSuccess) return Result<DownloadLink>.NotFound("Not found.");
        var lifetime = options.Value.DownloadLinkLifetime;
        var url = await FileVersions.LinkAsync(store, stores, tickets, tenant.Required.Id, chain[^1], version, true, lifetime, ct);
        return new DownloadLink(url, clock.GetUtcNow() + lifetime);
    }
}

/// <summary>A working link, the item it shares, and the chain of the item asked for (the shared item or one below it).</summary>
internal sealed record OpenedShare(ShareLink Link, Item Shared, IReadOnlyList<Item> Chain);

internal static class SharedTree
{
    public const string Gone = "This link does not work any more: it was removed, it expired, or it reached its download limit.";
    public const string NeedsPassword = "This link needs a password.";
    public const string WrongPassword = "The password is wrong.";

    /// <summary>
    /// The link (working, password accepted), the item it shares, and the chain of <paramref name="targetId"/>, which must
    /// be the shared item or below it (default: the shared item). Nothing on the way may be in the recycle bin.
    /// </summary>
    public static async Task<Result<OpenedShare>> OpenAsync(
        IStorageStore store, TimeProvider clock, string token, string? password, Guid? targetId, CancellationToken ct)
    {
        var link = string.IsNullOrWhiteSpace(token) || token.Length > ShareLink.TokenLength ? null : await store.LinkByTokenAsync(token, ct);
        if (link is null || !link.IsActive(clock.GetUtcNow())) return Result<OpenedShare>.NotFound(Gone);
        if (!link.Accepts(password))
            return Result<OpenedShare>.Unauthorized(string.IsNullOrEmpty(password) ? NeedsPassword : WrongPassword);
        var chain = await store.ChainAsync(targetId ?? link.ItemId, ct);
        var top = chain?.ToList().FindIndex(i => i.Id == link.ItemId) ?? -1;
        if (chain is null || top < 0 || chain.Any(i => i.IsDeleted))
            return Result<OpenedShare>.NotFound(top < 0 && chain is not null ? "Not found." : Gone);
        return new OpenedShare(link, chain[top], chain);
    }
}
