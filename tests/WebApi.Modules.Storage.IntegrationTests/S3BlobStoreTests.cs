using Microsoft.Extensions.Options;
using soft.square.net.WebApi.Modules.Storage.Infrastructure.Blobs;
using soft.square.net.WebApi.Modules.Storage.UseCases.Blobs;

namespace soft.square.net.WebApi.Modules.Storage.IntegrationTests;

/// <summary>
/// The S3 store against a real server, when one is given (skipped otherwise): STORAGE_TEST_S3_URL (MinIO:
/// http://localhost:9000), STORAGE_TEST_S3_ACCESS_KEY and STORAGE_TEST_S3_SECRET_KEY. Uses the bucket "storage-tests".
/// </summary>
public sealed class S3BlobStoreTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly string? Url = Environment.GetEnvironmentVariable("STORAGE_TEST_S3_URL");
    private readonly BlobStores? _stores;
    private readonly IBlobStore? _store;

    public S3BlobStoreTests()
    {
        if (string.IsNullOrWhiteSpace(Url)) return;
        var connections = new BlobConnectionsOptions();
        connections.Connections["test"] = new BlobConnectionOptions
        {
            ServiceUrl = Url,
            AccessKey = Environment.GetEnvironmentVariable("STORAGE_TEST_S3_ACCESS_KEY"),
            SecretKey = Environment.GetEnvironmentVariable("STORAGE_TEST_S3_SECRET_KEY"),
            ForcePathStyle = true,
            CreateBucket = true
        };
        _stores = new BlobStores(Options.Create(new LocalBlobOptions()), Options.Create(connections));
        _store = _stores.Get("test|storage-tests|run/");
    }

    public void Dispose() => _stores?.Dispose();

    private IBlobStore Store => _store ?? throw new InvalidOperationException();

    private static string NewKey() => $"t/acme/2026/11/{Guid.NewGuid():N}";

    private static void RequireServer()
    {
        if (string.IsNullOrWhiteSpace(Url)) Assert.Skip("No S3 server: set STORAGE_TEST_S3_URL (and the keys) to run.");
    }

    [Fact]
    public async Task MultipartUploadsReadBackAndDelete()
    {
        RequireServer();
        var key = NewKey();
        var first = new byte[S3BlobStore.S3MinPartSize];
        Random.Shared.NextBytes(first);
        var last = "the end"u8.ToArray();

        var upload = await Store.StartUploadAsync(key, "application/octet-stream", Ct);
        var etag1 = await Store.WritePartAsync(key, upload, 1, 0, new MemoryStream(first), first.Length, Ct);
        var etag2 = await Store.WritePartAsync(key, upload, 2, first.Length, new MemoryStream(last), last.Length, Ct);
        await Store.CompleteUploadAsync(key, upload, [etag1!, etag2!], first.Length + last.Length, Ct);

        var content = await Store.OpenAsync(key, Ct);
        content.ShouldNotBeNull();
        content.Length.ShouldBe(first.Length + last.Length);
        var read = new MemoryStream();
        await using (content.Stream) await content.Stream.CopyToAsync(read, Ct);
        read.ToArray().ShouldBe([.. first, .. last]);

        using var http = new HttpClient();
        var url = await Store.GetDownloadUrlAsync(key, "data.bin", "application/x-test", inline: false, TimeSpan.FromMinutes(1), Ct);
        using var response = await http.GetAsync(url, Ct);
        response.IsSuccessStatusCode.ShouldBeTrue();
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/x-test");
        response.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        (await response.Content.ReadAsByteArrayAsync(Ct)).Length.ShouldBe(first.Length + last.Length);

        await Store.DeleteAsync(key, Ct);
        (await Store.OpenAsync(key, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task EmptyFilesAndCancelledUploads()
    {
        RequireServer();
        var empty = NewKey();
        var upload = await Store.StartUploadAsync(empty, "text/plain", Ct);
        await Store.CompleteUploadAsync(empty, upload, [], 0, Ct);
        (await Store.OpenAsync(empty, Ct))!.Length.ShouldBe(0);
        await Store.DeleteAsync(empty, Ct);

        var cancelled = NewKey();
        upload = await Store.StartUploadAsync(cancelled, "text/plain", Ct);
        await Store.WritePartAsync(cancelled, upload, 1, 0, new MemoryStream([1, 2, 3]), 3, Ct);
        await Store.AbortUploadAsync(cancelled, upload, Ct);
        await Store.AbortUploadAsync(cancelled, upload, Ct); // twice is fine
        (await Store.OpenAsync(cancelled, Ct)).ShouldBeNull();
    }
}
