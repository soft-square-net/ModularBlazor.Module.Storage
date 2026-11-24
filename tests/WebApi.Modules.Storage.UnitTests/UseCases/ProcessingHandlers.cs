using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using soft.square.net.WebApi.Modules.Storage.Core.BlobAggregate;
using soft.square.net.WebApi.Modules.Storage.Core.EventAggregate;
using soft.square.net.WebApi.Modules.Storage.Core.UploadAggregate;
using soft.square.net.WebApi.Modules.Storage.UseCases.Blobs;
using soft.square.net.WebApi.Modules.Storage.UseCases.Events;
using soft.square.net.WebApi.Modules.Storage.UseCases.Processing;
using soft.square.net.WebApi.Modules.Storage.UseCases.Uploads;
using soft.square.net.WebApi.Modules.Storage.UseCases.Items;
using static soft.square.net.WebApi.Modules.Storage.UnitTests.Tree;
using static soft.square.net.WebApi.Modules.Storage.UnitTests.UseCases.HandlerFixture;

namespace soft.square.net.WebApi.Modules.Storage.UnitTests.UseCases;

/// <summary>New uploads wait for the scanner; what is not clean is never handed out; the processor scans and makes thumbnails.</summary>
public class ProcessingHandlers
{
    private readonly HandlerFixture _f = new();
    private readonly IFileScanner _scanner = Substitute.For<IFileScanner>();
    private readonly IThumbnailer _thumbnailer = Substitute.For<IThumbnailer>();
    private readonly IStorageWork _work = Substitute.For<IStorageWork>();
    private readonly StorageOptions _options = new();
    private readonly Item _root;

    public ProcessingHandlers()
    {
        _root = _f.Put(Root(Allow(PrincipalKind.Everyone, null, StorageRights.FullControl)));
        _f.Blobs.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => new BlobContent(new MemoryStream([1, 2, 3]), 3));
    }

    private UploadSession Uploaded(string name, string contentType)
    {
        var session = UploadSession.Start(_root.Id, name, contentType, 10, 10, 1000, NameConflict.Fail, $"t/acme/2026/11/{Guid.NewGuid():N}", "", "ana", Now, TimeSpan.FromHours(1));
        session.ChunkStored(10, null);
        _f.Store.UploadAsync(session.Id, Arg.Any<CancellationToken>()).Returns(session);
        return session;
    }

    private Blob? Complete(UploadSession session)
    {
        Blob? added = null;
        _f.Store.When(s => s.Add(Arg.Any<Blob>())).Do(call => added = call.Arg<Blob>());
        var result = new CompleteUploadHandler(_f.Store, _f.Access, _f.Caller, _f.Stores, _scanner, _work, _f.Tenant, Options.Create(_options))
            .Handle(new CompleteUploadCommand(session.Id), Ct).AsTask().Result;
        result.IsSuccess.ShouldBeTrue(string.Join(" ", result.Errors));
        return added;
    }

    [Fact]
    public void WithAScannerANewFileWaitsForIt()
    {
        _scanner.Enabled.Returns(true);

        var blob = Complete(Uploaded("photo.png", "image/png"));

        blob!.Scan.ShouldBe(ScanStatus.Pending);
        blob.Thumbnail.ShouldBe(ThumbnailState.Pending);
        _f.Items.Single(i => i.Name == "photo.png").Scan.ShouldBe(ScanStatus.Pending);
        _work.Received().Notify("acme");
    }

    [Fact]
    public void WithoutAScannerANewFileIsCleanAtOnce()
    {
        var blob = Complete(Uploaded("notes.txt", "text/plain"));

        blob!.Scan.ShouldBe(ScanStatus.Clean);
        blob.Thumbnail.ShouldBe(ThumbnailState.None); // not an image
        _f.Items.Single(i => i.Name == "notes.txt").Scan.ShouldBe(ScanStatus.Clean);
        _work.DidNotReceive().Notify(Arg.Any<string>());
    }

    private (Item File, Blob Blob) FileWithBlob(ScanStatus scan, string contentType = "image/png")
    {
        var key = $"t/acme/2026/11/{Guid.NewGuid():N}";
        var file = _f.Put(Item.CreateFile(_root, $"{Guid.NewGuid():N}.png", "ana", FileVersion.Create(key, 3, contentType, "ana", Now), Now, scan));
        var blob = Blob.Create(key, "", 3, Now, scan == ScanStatus.Pending, contentType.StartsWith("image/"));
        if (scan == ScanStatus.Infected) blob.Scanned("Eicar", Now);
        _f.Store.BlobsAsync(Arg.Is<IReadOnlyCollection<string>>(k => k.Contains(key)), Arg.Any<CancellationToken>()).Returns([blob]);
        _f.Store.FilesWithBlobAsync(key, Arg.Any<CancellationToken>()).Returns([file]);
        return (file, blob);
    }

    [Theory]
    [InlineData(ScanStatus.Pending, "being checked for viruses")]
    [InlineData(ScanStatus.Infected, "contains a virus (Eicar)")]
    public async Task WhatIsNotCleanIsNotHandedOut(ScanStatus scan, string message)
    {
        var (file, _) = FileWithBlob(scan);

        var link = await new GetDownloadLinkHandler(_f.Access, _f.Store, _f.Stores, _f.Tickets, _f.Tenant, _f.Caller, Options.Create(_options))
            .Handle(new GetDownloadLinkQuery(file.Id, null, false), Ct);
        var content = await new GetFileContentHandler(_f.Access, _f.Store, _f.Stores).Handle(new GetFileContentQuery(file.Id, null), Ct);

        link.Status.ShouldBe(ResultStatus.Conflict);
        link.Errors.Single().ShouldContain(message);
        content.Status.ShouldBe(ResultStatus.Conflict);
        await _f.Blobs.DidNotReceive().OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFileWaitingForItsScanCannotBeSharedByLink()
    {
        var (file, _) = FileWithBlob(ScanStatus.Pending);

        var result = await new soft.square.net.WebApi.Modules.Storage.UseCases.Sharing.CreateShareLinkHandler(_f.Store, _f.Access, _f.Caller)
            .Handle(new soft.square.net.WebApi.Modules.Storage.UseCases.Sharing.CreateShareLinkCommand(file.Id, null, null, null), Ct);

        result.Status.ShouldBe(ResultStatus.Conflict);
    }

    private ProcessHandler Processor => new(_f.Store, _f.Stores, _scanner, _thumbnailer, _f.Clock, Options.Create(_options), NullLoggerFactory.Instance);

    [Fact]
    public async Task TheScannersVerdictReachesTheBlobAndItsFiles()
    {
        _scanner.Enabled.Returns(true);
        var (file, blob) = FileWithBlob(ScanStatus.Pending);
        _f.Store.BlobsToScanAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([blob]);
        _scanner.ScanAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns("Eicar-Test-Signature");

        var result = await Processor.Handle(new ProcessCommand(), Ct);

        result.Value.ShouldBe(new ProcessResult(1, 1, 0));
        blob.Scan.ShouldBe(ScanStatus.Infected);
        blob.Threat.ShouldBe("Eicar-Test-Signature");
        file.Scan.ShouldBe(ScanStatus.Infected);
        await _f.Store.Received().SaveAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenTheScannerIsDownFilesWait()
    {
        _scanner.Enabled.Returns(true);
        var (file, blob) = FileWithBlob(ScanStatus.Pending);
        _f.Store.BlobsToScanAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([blob]);
        _scanner.ScanAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns<string?>(_ => throw new IOException("clamd is down"));

        var result = await Processor.Handle(new ProcessCommand(), Ct);

        result.Value.Scanned.ShouldBe(0);
        blob.Scan.ShouldBe(ScanStatus.Pending);
        file.Scan.ShouldBe(ScanStatus.Pending);
    }

    [Fact]
    public async Task WithoutAScannerAnyMoreWaitingFilesAreLetThrough()
    {
        var (file, blob) = FileWithBlob(ScanStatus.Pending);
        _f.Store.BlobsToScanAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([blob]);

        await Processor.Handle(new ProcessCommand(), Ct);

        blob.Scan.ShouldBe(ScanStatus.Clean);
        file.Scan.ShouldBe(ScanStatus.Clean);
        await _scanner.DidNotReceive().ScanAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CleanImagesGetAThumbnailNextToTheirBlob()
    {
        var (_, blob) = FileWithBlob(ScanStatus.Clean);
        _f.Store.BlobsToThumbnailAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([blob]);
        _thumbnailer.MakeAsync(Arg.Any<Stream>(), 320, Arg.Any<CancellationToken>()).Returns(new byte[] { 9, 9 });

        var result = await Processor.Handle(new ProcessCommand(), Ct);

        result.Value.Thumbnails.ShouldBe(1);
        blob.Thumbnail.ShouldBe(ThumbnailState.Ready);
        await _f.Blobs.Received().CompleteUploadAsync(blob.ThumbnailKey, Arg.Any<string?>(), Arg.Any<IReadOnlyList<string>>(), 2, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnImageThatCannotBeReadIsNotTriedAgain()
    {
        var (_, blob) = FileWithBlob(ScanStatus.Clean);
        _f.Store.BlobsToThumbnailAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([blob]);
        _thumbnailer.MakeAsync(Arg.Any<Stream>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((byte[]?)null);

        await Processor.Handle(new ProcessCommand(), Ct);

        blob.Thumbnail.ShouldBe(ThumbnailState.Failed);
    }

    [Fact]
    public async Task ListingsLinkTheThumbnailsOfCleanImages()
    {
        var (file, blob) = FileWithBlob(ScanStatus.Clean);
        blob.ThumbnailMade(true);
        _f.Tickets.Link(Arg.Any<DownloadTicket>(), Arg.Any<TimeSpan>()).Returns(new Uri("https://gateway/api/storage/links/t1"));

        var view = await new soft.square.net.WebApi.Modules.Storage.UseCases.Items.ListFolderHandler(_f.Store, _f.Access, _f.Thumbnails)
            .Handle(new soft.square.net.WebApi.Modules.Storage.UseCases.Items.ListFolderQuery(_root.Id), Ct);

        var dto = view.Value.Items.Single(i => i.Id == file.Id);
        dto.ThumbnailUrl.ShouldBe(new Uri("https://gateway/api/storage/links/t1"));
        _f.Tickets.Received().Link(Arg.Is<DownloadTicket>(t => t.BlobKey == blob.ThumbnailKey && t.ContentType == "image/webp"), Arg.Any<TimeSpan>());
    }
}

/// <summary>The event feed: pages, the hold-back of the newest seconds, and the outbox writer.</summary>
public class EventFeed
{
    private readonly HandlerFixture _f = new();

    private static StorageEvent Event(string type) =>
        StorageEvent.From(new ItemEvent(type, Guid.NewGuid(), ItemKind.File, "a.txt", null, 1, "ana", Now));

    [Fact]
    public async Task PagesFollowTheLastId()
    {
        var events = Enumerable.Range(0, 3).Select(_ => Event(StorageEventTypes.Created)).ToList();
        for (var i = 0; i < events.Count; i++) typeof(StorageEvent).GetProperty("Id")!.SetValue(events[i], 11L + i);
        _f.Store.EventsAsync(10, Arg.Any<DateTimeOffset>(), 3, Arg.Any<CancellationToken>()).Returns(events);

        var page = await new GetEventsHandler(_f.Store, _f.Clock, Options.Create(new StorageOptions())).Handle(new GetEventsQuery(10, 2), Ct);

        page.Value.Events.Select(e => e.Id).ShouldBe([11L, 12L]);
        page.Value.Last.ShouldBe(12);
        page.Value.More.ShouldBeTrue();
        await _f.Store.Received().EventsAsync(10, Now - TimeSpan.FromSeconds(5), 3, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NothingNewKeepsTheReadersPlace()
    {
        _f.Store.EventsAsync(Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);

        var page = await new GetEventsHandler(_f.Store, _f.Clock, Options.Create(new StorageOptions())).Handle(new GetEventsQuery(42, null), Ct);

        page.Value.Events.ShouldBeEmpty();
        page.Value.Last.ShouldBe(42);
        page.Value.More.ShouldBeFalse();
    }

    [Fact]
    public async Task TheWriterPutsEveryItemEventInTheOutbox()
    {
        await new ItemEventWriter(_f.Store).HandleAsync(new ItemEvent(StorageEventTypes.Deleted, Guid.NewGuid(), ItemKind.Folder, "a", null, null, "ana", Now), Ct);

        _f.Store.Received().Add(Arg.Is<StorageEvent>(e => e.Type == StorageEventTypes.Deleted && e.Kind == ItemKind.Folder));
    }
}
