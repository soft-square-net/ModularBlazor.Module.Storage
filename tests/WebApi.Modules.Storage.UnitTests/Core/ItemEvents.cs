using soft.square.net.WebApi.Modules.Storage.Core.BlobAggregate;
using soft.square.net.WebApi.Modules.Storage.Core.EventAggregate;
using static soft.square.net.WebApi.Modules.Storage.UnitTests.Tree;

namespace soft.square.net.WebApi.Modules.Storage.UnitTests.Core;

/// <summary>What an item raises for the outbox, and the blob's scan and thumbnail state.</summary>
public class ItemEvents
{
    private static IReadOnlyList<ItemEvent> Taken(Item item) =>
        [.. ((IHasDomainEvents)item).TakeDomainEvents().Cast<ItemEvent>()];

    [Fact]
    public void SystemFoldersRaiseNothing()
    {
        var root = Root();
        root.SetAccess(false, [], "admin", Now);
        Taken(root).ShouldBeEmpty();
    }

    [Fact]
    public void EachChangeRaisesItsEvent()
    {
        var root = Root();
        var a = Folder(root, "a");
        var b = Folder(root, "b");
        var file = File(a, "doc.txt");
        Taken(file).Select(e => e.Type).ShouldBe([StorageEventTypes.Created]);

        file.Rename("notes.txt", "ana", Now);
        file.MoveTo(b, 0, "ana", Now);
        file.AddVersion(FileVersion.Create("t/acme/2026/11/v2", 3, "text/plain", "ana", Now), "ana", Now);
        file.Delete("ana", Now);
        file.Restore("notes.txt", "ana", Now);

        var events = Taken(file);
        events.Select(e => e.Type).ShouldBe([StorageEventTypes.Renamed, StorageEventTypes.Moved, StorageEventTypes.Versioned,
            StorageEventTypes.Deleted, StorageEventTypes.Restored]);
        events[0].Detail.ShouldBe("doc.txt");
        events[1].Detail.ShouldBe(a.Id.ToString("D"));
        events[1].ParentId.ShouldBe(b.Id);
        events[2].FileVersion.ShouldBe(2);
    }

    [Fact]
    public void RenamingToTheSameNameRaisesNothing()
    {
        var file = File(Root(), "doc.txt");
        Taken(file);
        file.Rename("doc.txt", "ana", Now);
        Taken(file).ShouldBeEmpty();
    }

    [Fact]
    public void TheScanAppliesToTheCurrentVersionOnly()
    {
        var file = Item.CreateFile(Root(), "doc.txt", "ana", FileVersion.Create("t/acme/2026/11/v1", 3, "text/plain", "ana", Now), Now, ScanStatus.Pending);
        file.Scan.ShouldBe(ScanStatus.Pending);
        Taken(file);

        file.Scanned("t/acme/2026/11/other", "Eicar", Now).ShouldBeFalse();
        file.Scanned("t/acme/2026/11/v1", "Eicar-Test-Signature", Now).ShouldBeTrue();

        file.Scan.ShouldBe(ScanStatus.Infected);
        Taken(file).Single().Detail.ShouldBe("infected: Eicar-Test-Signature");
    }

    [Fact]
    public void AnInfectedBlobGetsNoThumbnail()
    {
        var blob = Blob.Create("t/acme/2026/11/x", "", 10, Now, scan: true, thumbnail: true);
        blob.Scan.ShouldBe(ScanStatus.Pending);
        blob.Thumbnail.ShouldBe(ThumbnailState.Pending);

        blob.Scanned("Eicar", Now);

        blob.Scan.ShouldBe(ScanStatus.Infected);
        blob.Thumbnail.ShouldBe(ThumbnailState.None);
        blob.ThumbnailKey.ShouldBe("t/acme/2026/11/x.thumb.webp");
    }

    [Fact]
    public void LongDetailsAreCut()
    {
        var e = StorageEvent.From(new ItemEvent(StorageEventTypes.Renamed, Guid.NewGuid(), ItemKind.File, "a", null, 1, "ana", Now, new string('x', 900)));
        e.Detail!.Length.ShouldBe(StorageEvent.DetailLength);
    }
}
