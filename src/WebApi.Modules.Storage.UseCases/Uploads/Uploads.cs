using soft.square.net.WebApi.Contracts.Tenancy;
using soft.square.net.WebApi.Modules.Storage.Core.BlobAggregate;
using soft.square.net.WebApi.Modules.Storage.Core.Common;
using soft.square.net.WebApi.Modules.Storage.Core.UploadAggregate;
using soft.square.net.WebApi.Modules.Storage.UseCases.Access;
using soft.square.net.WebApi.Modules.Storage.UseCases.Blobs;
using soft.square.net.WebApi.Modules.Storage.UseCases.Items;

namespace soft.square.net.WebApi.Modules.Storage.UseCases.Uploads;

/// <summary>An upload in progress: send the chunk at <see cref="Received"/>, <see cref="ChunkSize"/> bytes (the last may be shorter).</summary>
public sealed record UploadDto(Guid Id, Guid FolderId, string Name, long Size, int ChunkSize, long Received, NameConflict Conflict, DateTimeOffset ExpiresAt)
{
    public static UploadDto From(UploadSession s) => new(s.Id, s.FolderId, s.Name, s.Size, s.ChunkSize, s.Received, s.Conflict, s.ExpiresAt);
}

/// <summary>
/// Starts an upload into a folder (Create files). A taken name with <see cref="NameConflict.Fail"/> is refused now, so
/// the client can ask "Replace or skip?" before sending any bytes; the choice is checked again at completion.
/// </summary>
public sealed record StartUploadCommand(Guid FolderId, string Name, long Size, string? ContentType, NameConflict Conflict) : ICommand<Result<UploadDto>>;

public sealed class StartUploadHandler(IStorageStore store, ItemAccess access, StorageCaller caller, IBlobStore blobs, ITenantContext tenant,
    IOptions<StorageOptions> options) : ICommandHandler<StartUploadCommand, Result<UploadDto>>
{
    public async ValueTask<Result<UploadDto>> Handle(StartUploadCommand command, CancellationToken ct)
    {
        var folder = await access.ReachAsync(command.FolderId, StorageRights.CreateFiles, ct);
        if (!folder.IsSuccess) return folder.As<UploadDto>();
        if (!folder.Value.Item.IsFolder) return Result<UploadDto>.Invalid(new ValidationError("Files can only be uploaded into a folder."));

        var name = ItemNames.Require(command.Name);
        if (command.Conflict == NameConflict.Fail && await store.ChildByNameAsync(folder.Value.Item.Id, name, ct) is not null)
            return Result<UploadDto>.Conflict(StorageMessages.NameTaken(name));

        var settings = options.Value;
        var key = Blob.NewKey(tenant.Required.Id, caller.Now);
        var session = UploadSession.Start(folder.Value.Item.Id, name, command.ContentType, command.Size, settings.ChunkSize, settings.MaxFileSize,
            command.Conflict, key, caller.UserId, caller.Now, settings.UploadLifetime);
        session.UseProviderUpload(await blobs.StartUploadAsync(key, session.ContentType, ct));
        store.Add(session);
        await store.SaveAsync(ct);
        return UploadDto.From(session);
    }
}

/// <summary>Where an upload stands (resume after a lost connection or a reload).</summary>
public sealed record GetUploadQuery(Guid Id) : IQuery<Result<UploadDto>>;

public sealed class GetUploadHandler(IStorageStore store, StorageCaller caller) : IQueryHandler<GetUploadQuery, Result<UploadDto>>
{
    public async ValueTask<Result<UploadDto>> Handle(GetUploadQuery query, CancellationToken ct)
    {
        var session = await Sessions.MineAsync(store, caller, query.Id, ct);
        return session.IsSuccess ? UploadDto.From(session.Value) : session.As<UploadDto>();
    }
}

/// <summary>One chunk, at the offset the session expects.</summary>
public sealed record WriteChunkCommand(Guid Id, long Offset, long Length, Stream Data) : ICommand<Result<UploadDto>>;

public sealed class WriteChunkHandler(IStorageStore store, StorageCaller caller, IBlobStore blobs) : ICommandHandler<WriteChunkCommand, Result<UploadDto>>
{
    public async ValueTask<Result<UploadDto>> Handle(WriteChunkCommand command, CancellationToken ct)
    {
        var found = await Sessions.MineAsync(store, caller, command.Id, ct);
        if (!found.IsSuccess) return found.As<UploadDto>();
        var session = found.Value;
        int part;
        try { part = session.CheckChunk(command.Offset, command.Length, caller.UserId, caller.Now); }
        catch (DomainException e) { return Result<UploadDto>.Conflict(e.Message); }

        var receipt = await blobs.WritePartAsync(session.BlobKey, session.ProviderUploadId, part, command.Offset, command.Data, command.Length, ct);
        session.ChunkStored(command.Length, receipt);
        await store.SaveAsync(ct);
        return UploadDto.From(session);
    }
}

/// <summary>
/// Completes an upload: the blob is made, and the file appears in the folder (or, with Replace, becomes the existing
/// file's new version, which needs Write data on that file). Rights are checked again, as they may have changed.
/// </summary>
public sealed record CompleteUploadCommand(Guid Id) : ICommand<Result<ItemDto>>;

public sealed class CompleteUploadHandler(IStorageStore store, ItemAccess access, StorageCaller caller, IBlobStore blobs)
    : ICommandHandler<CompleteUploadCommand, Result<ItemDto>>
{
    public async ValueTask<Result<ItemDto>> Handle(CompleteUploadCommand command, CancellationToken ct)
    {
        var found = await Sessions.MineAsync(store, caller, command.Id, ct);
        if (!found.IsSuccess) return found.As<ItemDto>();
        var session = found.Value;
        if (!session.IsComplete) return Result<ItemDto>.Conflict($"The upload has {session.Received:N0} of {session.Size:N0} bytes.");

        var folder = await access.ReachAsync(session.FolderId, StorageRights.CreateFiles, ct);
        if (!folder.IsSuccess) return folder.As<ItemDto>();

        var me = caller.UserId;
        var now = caller.Now;
        var version = FileVersion.Create(session.BlobKey, session.Size, session.ContentType, me, now);
        Item file;
        var existing = await store.ChildByNameAsync(session.FolderId, session.Name, ct);
        if (existing is not null && session.Conflict == NameConflict.Replace)
        {
            if (existing.IsFolder) return Result<ItemDto>.Conflict($"'{existing.Name}' is a folder; it cannot be replaced by a file.");
            if (!(await access.RightsOnChildAsync(folder.Value, existing, ct)).HasFlag(StorageRights.CreateFiles))
                return Result<ItemDto>.Forbidden(StorageMessages.Denied);
            existing.AddVersion(version, me, now);
            file = existing;
        }
        else
        {
            var name = await FreeNames.FindAsync(store, session.FolderId, session.Name, session.Conflict == NameConflict.KeepBoth,
                (n, i) => ItemNames.Numbered(n, i, isFolder: false), ct);
            if (name is null) return Result<ItemDto>.Conflict(StorageMessages.NameTaken(session.Name));
            file = Item.CreateFile(folder.Value.Item, name, me, version, now);
            store.Add(file);
        }

        await blobs.CompleteUploadAsync(session.BlobKey, session.ProviderUploadId, session.PartReceipts, session.Size, ct);
        store.Add(Blob.Create(session.BlobKey, session.Size, now));
        store.Remove(session);
        await store.SaveAsync(ct);
        return ItemDto.From(file, await access.RightsOnChildAsync(folder.Value, file, ct));
    }
}

/// <summary>Cancels an upload and drops its bytes.</summary>
public sealed record CancelUploadCommand(Guid Id) : ICommand<Result>;

public sealed class CancelUploadHandler(IStorageStore store, StorageCaller caller, IBlobStore blobs) : ICommandHandler<CancelUploadCommand, Result>
{
    public async ValueTask<Result> Handle(CancelUploadCommand command, CancellationToken ct)
    {
        var found = await Sessions.MineAsync(store, caller, command.Id, ct);
        if (!found.IsSuccess) return Result.NotFound([.. found.Errors]);
        await blobs.AbortUploadAsync(found.Value.BlobKey, found.Value.ProviderUploadId, ct);
        store.Remove(found.Value);
        await store.SaveAsync(ct);
        return Result.Success();
    }
}

internal static class Sessions
{
    /// <summary>The caller's own upload session (another user's is "not found").</summary>
    public static async Task<Result<UploadSession>> MineAsync(IStorageStore store, StorageCaller caller, Guid id, CancellationToken ct)
    {
        var session = await store.UploadAsync(id, ct);
        return session is null || session.CreatedBy != caller.UserId
            ? Result<UploadSession>.NotFound("The upload does not exist or has expired.")
            : session;
    }
}
