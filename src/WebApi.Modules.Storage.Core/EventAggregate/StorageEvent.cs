using soft.square.net.WebApi.Modules.Storage.Core.ItemAggregate;

namespace soft.square.net.WebApi.Modules.Storage.Core.EventAggregate;

/// <summary>The kinds of <see cref="StorageEvent"/>: what happened to a folder or a file.</summary>
public static class StorageEventTypes
{
    /// <summary>A folder or file was created (new folder, upload of a new file, copy).</summary>
    public const string Created = "item.created";
    /// <summary>A file got a new current version (an upload over it, an old version restored).</summary>
    public const string Versioned = "file.versioned";
    /// <summary>Renamed; <see cref="StorageEvent.Detail"/> is the old name.</summary>
    public const string Renamed = "item.renamed";
    /// <summary>Moved to another folder; <see cref="StorageEvent.Detail"/> is the old folder's id.</summary>
    public const string Moved = "item.moved";
    /// <summary>Moved to the recycle bin (a folder takes what is in it along).</summary>
    public const string Deleted = "item.deleted";
    /// <summary>Taken out of the recycle bin.</summary>
    public const string Restored = "item.restored";
    /// <summary>Removed for good, with everything below it; <see cref="StorageEvent.Detail"/> is how many items went.</summary>
    public const string Purged = "item.purged";
    /// <summary>The virus scan of the current version finished; <see cref="StorageEvent.Detail"/> is "clean" or "infected: {threat}".</summary>
    public const string Scanned = "file.scanned";
}

/// <summary>
/// Raised by <see cref="Item"/> when something happens to it; a handler writes it to the outbox
/// (<see cref="StorageEvent"/>) in the same save, so the feed never misses a change and never shows one that was rolled back.
/// </summary>
public sealed record ItemEvent(string Type, Guid ItemId, ItemKind Kind, string Name, Guid? ParentId, int? FileVersion, string By,
    DateTimeOffset At, string? Detail = null) : IDomainEvent;

/// <summary>
/// One row of the module's outbox: the feed <c>GET /events?after=</c> that workflows (Elsa) read to react to files,
/// in order. Ids grow; rows are kept for <c>Storage:EventRetentionDays</c>.
/// </summary>
public sealed class StorageEvent : EntityBase<long>, ITenantOwned
{
    public const int TypeLength = 40;
    public const int DetailLength = 500;

    private StorageEvent() { } // EF Core

    public string TenantId { get; set; } = "";
    public string Type { get; private set; } = "";
    public Guid ItemId { get; private set; }
    public ItemKind Kind { get; private set; }
    public string Name { get; private set; } = "";
    public Guid? ParentId { get; private set; }
    public int? FileVersion { get; private set; }
    public string By { get; private set; } = "";
    public DateTimeOffset At { get; private set; }
    public string? Detail { get; private set; }

    public static StorageEvent From(ItemEvent e) => new()
    {
        Type = e.Type,
        ItemId = e.ItemId,
        Kind = e.Kind,
        Name = e.Name,
        ParentId = e.ParentId,
        FileVersion = e.FileVersion,
        By = e.By,
        At = e.At,
        Detail = e.Detail is { Length: > DetailLength } text ? text[..DetailLength] : e.Detail
    };
}
