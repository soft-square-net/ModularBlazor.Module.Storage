using soft.square.net.WebApi.Modules.Storage.Infrastructure.Blobs;

namespace soft.square.net.WebApi.Modules.Storage.IntegrationTests;

/// <summary>The local disk provider in a temporary folder: chunked uploads, retries, reads, and keys it refuses.</summary>
public sealed class FileSystemBlobStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "storage-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FileSystemBlobStore _store;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly string Key = $"t/acme/2026/11/{Guid.NewGuid():N}";

    public FileSystemBlobStoreTests() => _store = new FileSystemBlobStore(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task WritesChunksAtTheirOffsetsAndCompletes()
    {
        var upload = await _store.StartUploadAsync(Key, "text/plain", Ct);
        await _store.WritePartAsync(Key, upload, 1, 0, new MemoryStream("hello "u8.ToArray()), 6, Ct);
        // A retried first chunk overwrites in place.
        await _store.WritePartAsync(Key, upload, 1, 0, new MemoryStream("HELLO "u8.ToArray()), 6, Ct);
        await _store.WritePartAsync(Key, upload, 2, 6, new MemoryStream("world"u8.ToArray()), 5, Ct);
        await _store.CompleteUploadAsync(Key, upload, [], 11, Ct);

        var content = await _store.OpenAsync(Key, Ct);
        content.ShouldNotBeNull();
        await using (content.Stream)
        {
            content.Length.ShouldBe(11);
            content.Stream.CanSeek.ShouldBeTrue();
            (await new StreamReader(content.Stream).ReadToEndAsync(Ct)).ShouldBe("HELLO world");
        }
        Directory.GetFiles(Path.Combine(_root, ".uploads")).ShouldBeEmpty();
    }

    [Fact]
    public async Task AShortChunkFails()
    {
        var upload = await _store.StartUploadAsync(Key, "text/plain", Ct);
        await Should.ThrowAsync<IOException>(() => _store.WritePartAsync(Key, upload, 1, 0, new MemoryStream([1, 2]), 5, Ct));
    }

    [Fact]
    public async Task EmptyFilesWork()
    {
        var upload = await _store.StartUploadAsync(Key, "text/plain", Ct);
        await _store.CompleteUploadAsync(Key, upload, [], 0, Ct);
        var content = await _store.OpenAsync(Key, Ct);
        await using (content!.Stream) content.Length.ShouldBe(0);
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("t/acme/2026/11/../../../../x")]
    [InlineData("t/ACME/2026/11/0123")]
    public async Task RefusesKeysItDidNotMake(string key) =>
        await Should.ThrowAsync<ArgumentException>(() => _store.OpenAsync(key, Ct));

    [Fact]
    public async Task AMissingBlobIsNull() => (await _store.OpenAsync(Key, Ct)).ShouldBeNull();
}
