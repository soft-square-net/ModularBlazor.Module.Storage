using soft.square.net.WebApi.Modules.Storage.Core.EventAggregate;
using soft.square.net.WebApi.Modules.Storage.UseCases.Items;

namespace soft.square.net.WebApi.Modules.Storage.UseCases.Events;

/// <summary>One event of the feed. <see cref="Type"/> is one of <see cref="StorageEventTypes"/>.</summary>
public sealed record StorageEventDto(long Id, string Type, Guid ItemId, ItemKind Kind, string Name, Guid? ParentId, int? FileVersion,
    string By, DateTimeOffset At, string? Detail)
{
    public static StorageEventDto From(StorageEvent e) => new(e.Id, e.Type, e.ItemId, e.Kind, e.Name, e.ParentId, e.FileVersion, e.By, e.At, e.Detail);
}

/// <summary>A page of the feed: read the next one with <c>after = Last</c> (the same value again when there was nothing new).</summary>
public sealed record EventPage(IReadOnlyList<StorageEventDto> Events, long Last, bool More);

/// <summary>
/// The tenant's events after <see cref="After"/>, oldest first: what workflows (Elsa) poll to react to files. A reader
/// keeps the last id it handled. Events are kept for <c>Storage:EventRetentionDays</c>; the newest seconds
/// (<c>Storage:EventDelay</c>) are held back, so a slower save cannot land behind an id a reader already passed.
/// </summary>
public sealed record GetEventsQuery(long After, int? Take) : IQuery<Result<EventPage>>;

public sealed class GetEventsHandler(IStorageStore store, TimeProvider clock, IOptions<StorageOptions> options) : IQueryHandler<GetEventsQuery, Result<EventPage>>
{
    public const int MaxTake = 500;

    public async ValueTask<Result<EventPage>> Handle(GetEventsQuery query, CancellationToken ct)
    {
        var take = Math.Clamp(query.Take ?? 100, 1, MaxTake);
        var after = Math.Max(0, query.After);
        var events = await store.EventsAsync(after, clock.GetUtcNow() - options.Value.EventDelay, take + 1, ct);
        var page = events.Take(take).Select(StorageEventDto.From).ToList();
        return new EventPage(page, page.Count > 0 ? page[^1].Id : after, events.Count > take);
    }
}

/// <summary>Writes every <see cref="ItemEvent"/> to the outbox, in the save that raised it.</summary>
public sealed class ItemEventWriter(IStorageStore store) : IDomainEventHandler<ItemEvent>
{
    public Task HandleAsync(ItemEvent domainEvent, CancellationToken cancellationToken)
    {
        store.Add(StorageEvent.From(domainEvent));
        return Task.CompletedTask;
    }
}
