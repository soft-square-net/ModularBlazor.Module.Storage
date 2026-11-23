using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using soft.square.net.WebApi.Contracts.Tenancy;
using soft.square.net.WebApi.Modules.Storage.UseCases.Items;
using soft.square.net.WebApi.Modules.Storage.Web.Extensions;

namespace soft.square.net.WebApi.Modules.Storage.Web.Items;

public sealed record RenameRequest(string Name, int? Version);

/// <summary>The Explorer's folder and file endpoints. Each sends one command or query; the use case checks the item's access list.</summary>
public static class ItemEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/roots", async (ISender sender, CancellationToken ct) => (await sender.Send(new GetRootsQuery(), ct)).ToOkResult())
            .WithName("Storage_GetRoots")
            .WithSummary("The navigation pane's starting points: Files and your home folder (created on first use).");

        app.MapGet("/folders/{id:guid}/items", async (Guid id, ISender sender, CancellationToken ct) =>
                (await sender.Send(new ListFolderQuery(id), ct)).ToOkResult())
            .WithName("Storage_ListFolder")
            .WithSummary("Open a folder: its path and the items you have any right on, folders first (List folder).");

        app.MapGet("/items/{id:guid}", async (Guid id, ISender sender, CancellationToken ct) =>
                (await sender.Send(new GetItemQuery(id), ct)).ToOkResult())
            .WithName("Storage_GetItem")
            .WithSummary("An item's properties, path and versions (Read attributes).");

        app.MapPost("/folders", async (CreateFolderCommand command, ISender sender, CancellationToken ct) =>
                (await sender.Send(command, ct)).ToCreatedResult(f => $"items/{f.Id}"))
            .WithName("Storage_CreateFolder")
            .WithSummary("New folder (Create folders on the parent); numberIfTaken gives \"New folder (2)\".");

        app.MapPatch("/items/{id:guid}", async (Guid id, RenameRequest request, ISender sender, CancellationToken ct) =>
                (await sender.Send(new RenameItemCommand(id, request.Name, request.Version), ct)).ToOkResult())
            .WithName("Storage_RenameItem")
            .WithSummary("Rename (Delete on the item, or Delete children on its folder).");

        app.MapPost("/items/move", async (MoveItemsCommand command, ISender sender, CancellationToken ct) =>
                (await sender.Send(command, ct)).ToOkResult())
            .WithName("Storage_MoveItems")
            .WithSummary("Cut and paste, or drag: move items into a folder; each item moves on its own.");

        app.MapPost("/items/copy", async (CopyItemsCommand command, ISender sender, CancellationToken ct) =>
                (await sender.Send(command, ct)).ToOkResult())
            .WithName("Storage_CopyItems")
            .WithSummary("Copy and paste: copies inherit from the destination; file copies share their blob.");

        app.MapPost("/items/delete", async (DeleteItemsCommand command, ISender sender, CancellationToken ct) =>
                (await sender.Send(command, ct)).ToOkResult())
            .WithName("Storage_DeleteItems")
            .WithSummary("Delete to the recycle bin.");

        app.MapGet("/items/{id:guid}/content", async Task<Results<FileStreamHttpResult, ProblemHttpResult>> (Guid id, int? version, bool? inline,
                ISender sender, HttpResponse response, CancellationToken ct) =>
            {
                var result = await sender.Send(new GetFileContentQuery(id, version), ct);
                if (!result.IsSuccess) return result.ToProblem();
                var file = result.Value;
                return Files.Send(response, file.Stream, file.ContentType, file.FileName, inline == true, file.LastModified, file.ETag);
            })
            .Produces<Stream>(StatusCodes.Status200OK, "application/octet-stream")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("Storage_DownloadFile")
            .WithSummary("A file's bytes (Read data), the current version or another one; supports ranges.");

        app.MapGet("/items/{id:guid}/link", async (Guid id, int? version, bool? inline, ISender sender, CancellationToken ct) =>
                (await sender.Send(new GetDownloadLinkQuery(id, version, inline == true), ct)).ToOkResult())
            .WithName("Storage_GetDownloadLink")
            .WithSummary("A short-lived URL to download or show a file without the access token (img, video, PDF viewer).");
    }

    /// <summary>
    /// The anonymous end of a signed link: the token is the authorization. It works without a tenant in the request
    /// (an &lt;img&gt; sends no X-Tenant header); a tenant named by the request must be the link's.
    /// </summary>
    public static void MapLinks(IEndpointRouteBuilder app)
    {
        app.MapGet("/links/{token}", async Task<Results<FileStreamHttpResult, ProblemHttpResult>> (string token, ISender sender,
                HttpResponse response, CancellationToken ct) =>
            {
                var result = await sender.Send(new OpenTicketQuery(token), ct);
                if (!result.IsSuccess) return result.ToProblem();
                var (ticket, content) = result.Value;
                response.Headers.CacheControl = "private, max-age=300";
                return Files.Send(response, content.Stream, ticket.ContentType, ticket.FileName, ticket.Inline, null, null);
            })
            .AllowAnonymous()
            .TenantOptional()
            .ExcludeFromDescription()
            .WithName(DownloadTickets.RouteName);
    }
}

internal static class Files
{
    /// <summary>A file response with ranges; attachment (download) or inline (show), the name encoded for any language.</summary>
    public static FileStreamHttpResult Send(HttpResponse response, Stream stream, string contentType, string fileName, bool inline,
        DateTimeOffset? lastModified, string? etag)
    {
        var disposition = new ContentDispositionHeaderValue(inline ? "inline" : "attachment");
        disposition.SetHttpFileName(fileName);
        response.Headers.ContentDisposition = disposition.ToString();
        response.Headers.XContentTypeOptions = "nosniff";
        return TypedResults.Stream(stream, contentType, fileDownloadName: null, lastModified,
            etag is null ? null : new EntityTagHeaderValue(etag), enableRangeProcessing: stream.CanSeek);
    }
}
