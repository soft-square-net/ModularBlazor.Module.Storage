using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using soft.square.net.WebApi.Modules.Storage.Core.BlobAggregate;
using soft.square.net.WebApi.Modules.Storage.Core.EventAggregate;
using soft.square.net.WebApi.Modules.Storage.Core.ItemAggregate;
using soft.square.net.WebApi.Modules.Storage.Infrastructure.Data;
using soft.square.net.WebApi.Modules.Storage.Infrastructure.Processing;

namespace soft.square.net.WebApi.Modules.Storage.IntegrationTests;

/// <summary>The thumbnailer on real images, clamd's replies, and the store's outbox and processing queries.</summary>
public sealed class ProcessingTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);
    private readonly StorageDatabase _database = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task AnImageBecomesASmallWebp()
    {
        using var image = new Image<Rgba32>(1200, 600, new Rgba32(200, 30, 30));
        using var png = new MemoryStream();
        await image.SaveAsync(png, new PngEncoder(), Ct);
        png.Position = 0;

        var bytes = await new ImageSharpThumbnailer().MakeAsync(png, 320, Ct);

        bytes.ShouldNotBeNull();
        using var thumb = Image.Load(bytes);
        Image.DetectFormat(bytes).DefaultMimeType.ShouldBe("image/webp");
        Math.Max(thumb.Width, thumb.Height).ShouldBe(320);
        thumb.Height.ShouldBe(160); // the aspect ratio is kept
    }

    [Fact]
    public async Task WhatIsNotAnImageGivesNoThumbnail()
    {
        using var garbage = new MemoryStream("this is not an image"u8.ToArray());
        (await new ImageSharpThumbnailer().MakeAsync(garbage, 320, Ct)).ShouldBeNull();
    }

    [Theory]
    [InlineData("stream: OK", null)]
    [InlineData("stream: Eicar-Test-Signature FOUND", "Eicar-Test-Signature")]
    public void ClamdRepliesAreRead(string reply, string? threat) => ClamAvScanner.Parse(reply).ShouldBe(threat);

    [Fact]
    public void AClamdErrorIsNotAVerdict() =>
        Should.Throw<IOException>(() => ClamAvScanner.Parse("INSTREAM size limit exceeded. ERROR"));

    private static StorageEvent Event(string type, DateTimeOffset at) =>
        StorageEvent.From(new ItemEvent(type, Guid.NewGuid(), ItemKind.File, "a.txt", null, 1, "ana", at));

    [Fact]
    public async Task TheOutboxIsReadInOrderAndTrimmed()
    {
        await using (var db = _database.For("acme"))
        {
            var store = new StorageStore(db);
            store.Add(Event(StorageEventTypes.Created, Now.AddDays(-10)));
            store.Add(Event(StorageEventTypes.Renamed, Now.AddMinutes(-2)));
            store.Add(Event(StorageEventTypes.Deleted, Now.AddMinutes(-1)));
            store.Add(Event(StorageEventTypes.Purged, Now)); // newer than the read's upper bound
            await store.SaveAsync(Ct);
        }
        await using (var db = _database.For("globex"))
        {
            var store = new StorageStore(db);
            store.Add(Event(StorageEventTypes.Created, Now.AddMinutes(-1)));
            await store.SaveAsync(Ct);
        }

        await using (var db = _database.For("acme"))
        {
            var store = new StorageStore(db);
            var all = await store.EventsAsync(0, Now.AddSeconds(-5), 10, Ct);
            all.Select(e => e.Type).ShouldBe([StorageEventTypes.Created, StorageEventTypes.Renamed, StorageEventTypes.Deleted]);
            all.Select(e => e.Id).ShouldBeInOrder();

            var after = await store.EventsAsync(all[0].Id, Now.AddSeconds(-5), 1, Ct);
            after.Single().Type.ShouldBe(StorageEventTypes.Renamed);

            (await store.DeleteEventsBeforeAsync(Now.AddDays(-7), Ct)).ShouldBe(1);
        }
        await using (var db = _database.For("globex"))
            (await new StorageStore(db).EventsAsync(0, Now, 10, Ct)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task TheProcessorsQueues()
    {
        var root = Item.CreateTop("acme", FolderRole.Root, "Files", [], Now);
        var pending = Blob.Create("t/acme/2026/11/pending", "", 5, Now, scan: true, thumbnail: true);
        var image = Blob.Create("t/acme/2026/11/image", "", 5, Now, thumbnail: true);
        var unused = Blob.Create("t/acme/2026/11/unused", "", 5, Now, scan: true, thumbnail: true);
        unused.Release();
        var file = Item.CreateFile(root, "a.png", "ana", FileVersion.Create("t/acme/2026/11/image", 5, "image/png", "ana", Now), Now);
        file.AddVersion(FileVersion.Create("t/acme/2026/11/pending", 5, "image/png", "ana", Now), "ana", Now, ScanStatus.Pending);
        await using (var db = _database.For("acme"))
        {
            var store = new StorageStore(db);
            store.Add(root);
            store.Add(file);
            store.Add(pending);
            store.Add(image);
            store.Add(unused);
            await store.SaveAsync(Ct);
        }

        await using (var db = _database.For("acme"))
        {
            var store = new StorageStore(db);
            (await store.BlobsToScanAsync(10, Ct)).Select(b => b.Id).ShouldBe(["t/acme/2026/11/pending"]);
            (await store.BlobsToThumbnailAsync(10, Ct)).Select(b => b.Id).ShouldBe(["t/acme/2026/11/image"]); // the pending one waits for its scan
            (await store.FilesWithBlobAsync("t/acme/2026/11/image", Ct)).Single().Id.ShouldBe(file.Id); // an older version counts too
            (await store.FilesWithBlobAsync("t/acme/2026/11/unused", Ct)).ShouldBeEmpty();
        }
        await using (var db = _database.For("globex"))
            (await new StorageStore(db).BlobsToScanAsync(10, Ct)).ShouldBeEmpty();
    }
}
