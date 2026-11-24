using soft.square.net.WebApi.Modules.Storage.UseCases.Events;

namespace soft.square.net.WebApi.Modules.Storage.Web.Events;

/// <summary>
/// The event feed for workflow services (Elsa): a client-credentials token with <c>storage.events</c> in the tenant,
/// and <c>X-Tenant</c>. Poll with the last id handled; names and ids only, never content.
/// </summary>
public static class EventEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/events", async (long? after, int? take, ISender sender, CancellationToken ct) =>
                (await sender.Send(new GetEventsQuery(after ?? 0, take), ct)).ToOkResult())
            .WithName("Storage_GetEvents")
            .WithSummary("What happened to folders and files after an event id, oldest first (item.created, file.versioned, item.renamed, item.moved, item.deleted, item.restored, item.purged, file.scanned).");
    }
}
