using soft.square.net.WebApi.Modules.Storage.UseCases.Uploads;

namespace soft.square.net.WebApi.Modules.Storage.Web.Uploads;

/// <summary>
/// Resumable uploads: start, send the chunks in order (<c>PUT /uploads/{id}/chunks?offset=</c> with the raw bytes),
/// complete. After a lost connection, <c>GET /uploads/{id}</c> tells where to resume.
/// </summary>
public static class UploadEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/uploads", async (StartUploadCommand command, ISender sender, CancellationToken ct) =>
                (await sender.Send(command, ct)).ToCreatedResult(u => $"uploads/{u.Id}"))
            .WithName("Storage_StartUpload")
            .WithSummary("Start an upload into a folder (Create files). conflict: Fail, Replace or KeepBoth.");

        app.MapGet("/uploads/{id:guid}", async (Guid id, ISender sender, CancellationToken ct) =>
                (await sender.Send(new GetUploadQuery(id), ct)).ToOkResult())
            .WithName("Storage_GetUpload")
            .WithSummary("Where an upload stands: send the chunk at 'received'.");

        app.MapPut("/uploads/{id:guid}/chunks", async (Guid id, long offset, IFormFile chunk, ISender sender, CancellationToken ct) =>
            {
                await using var data = chunk.OpenReadStream();
                return (await sender.Send(new WriteChunkCommand(id, offset, chunk.Length, data), ct)).ToOkResult();
            })
            // A bearer-token API: no cookies, so no antiforgery token (minimal APIs ask for one on form binding).
            .DisableAntiforgery()
            .WithName("Storage_WriteChunk")
            .WithSummary("One chunk of the file (form file \"chunk\"), at the offset the upload expects.");

        app.MapPost("/uploads/{id:guid}/complete", async (Guid id, ISender sender, CancellationToken ct) =>
                (await sender.Send(new CompleteUploadCommand(id), ct)).ToOkResult())
            .WithName("Storage_CompleteUpload")
            .WithSummary("Finish an upload: the file appears in the folder (or becomes a new version with Replace).");

        app.MapDelete("/uploads/{id:guid}", async (Guid id, ISender sender, CancellationToken ct) =>
                (await sender.Send(new CancelUploadCommand(id), ct)).ToNoContentResult())
            .WithName("Storage_CancelUpload")
            .WithSummary("Cancel an upload and drop its bytes.");
    }
}
