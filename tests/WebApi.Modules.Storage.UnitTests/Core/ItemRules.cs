using soft.square.net.WebApi.Modules.Storage.Core.Common;
using soft.square.net.WebApi.Modules.Storage.Core.UploadAggregate;
using static soft.square.net.WebApi.Modules.Storage.UnitTests.Tree;

namespace soft.square.net.WebApi.Modules.Storage.UnitTests.Core;

public class ItemNamesRequire
{
    [Theory]
    [InlineData("a/b")]
    [InlineData("a:b")]
    [InlineData("what?")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("  ")]
    [InlineData("...")]
    public void RefusesWhatWindowsRefuses(string name) => Should.Throw<DomainException>(() => ItemNames.Require(name));

    [Fact]
    public void DropsTrailingDotsAndSpacesAndKeepsArabic()
    {
        ItemNames.Require("تقرير. ").ShouldBe("تقرير");
        ItemNames.Key("Report.PDF").ShouldBe(ItemNames.Key("report.pdf"));
    }

    [Fact]
    public void NumbersAndCopiesLikeExplorer()
    {
        ItemNames.Numbered("Report.pdf", 2, isFolder: false).ShouldBe("Report (2).pdf");
        ItemNames.Numbered("New folder", 3, isFolder: true).ShouldBe("New folder (3)");
        ItemNames.CopyName("Report.pdf", 1, isFolder: false).ShouldBe("Report - Copy.pdf");
        ItemNames.CopyName("Report.pdf", 2, isFolder: false).ShouldBe("Report - Copy (2).pdf");
    }
}

public class ItemMoveAndDelete
{
    [Fact]
    public void PathsAndDepthFollowTheTree()
    {
        var root = Root();
        var a = Folder(root, "a");
        var file = File(a, "x.txt");
        file.Path.ShouldBe($"/{root.Id:N}/{a.Id:N}/{file.Id:N}/");
        file.Depth.ShouldBe(2);
        file.AncestorIds().ShouldBe([root.Id, a.Id]);
    }

    [Fact]
    public void RefusesToMoveAFolderIntoItself()
    {
        var root = Root();
        var a = Folder(root, "a");
        var b = Folder(a, "b");
        Should.Throw<DomainException>(() => a.MoveTo(b, 1, "ana", Now));
        Should.Throw<DomainException>(() => a.MoveTo(a, 1, "ana", Now));
    }

    [Fact]
    public void MovesAndRebasesWhatIsBelow()
    {
        var root = Root();
        var a = Folder(root, "a");
        var b = Folder(a, "b");
        var c = Folder(root, "c");

        var (oldPath, oldDepth) = a.MoveTo(c, 1, "ana", Now);
        b.Rebase(oldPath, a.Path, a.Depth - oldDepth);

        a.ParentId.ShouldBe(c.Id);
        b.Path.ShouldBe($"/{root.Id:N}/{c.Id:N}/{a.Id:N}/{b.Id:N}/");
        b.Depth.ShouldBe(3);
    }

    [Fact]
    public void SystemFoldersCannotBeRenamedOrDeleted()
    {
        var root = Root();
        Should.Throw<DomainException>(() => root.Rename("x", "ana", Now));
        Should.Throw<DomainException>(() => root.Delete("ana", Now));
    }

    [Fact]
    public void DeletingFreesTheName()
    {
        var root = Root();
        var file = File(root, "x.txt");
        file.Delete("ana", Now);
        file.IsDeleted.ShouldBeTrue();
        file.NameKey.ShouldNotBe(ItemNames.Key("x.txt"));
    }

    [Fact]
    public void TopFolderIdsComeFromTheTenant()
    {
        Item.TopId("acme", FolderRole.Root).ShouldBe(Item.TopId("acme", FolderRole.Root));
        Item.TopId("acme", FolderRole.Root).ShouldNotBe(Item.TopId("globex", FolderRole.Root));
        Item.TopId("acme", FolderRole.Root).ShouldNotBe(Item.TopId("acme", FolderRole.Homes));
    }

    [Fact]
    public void NewVersionsBecomeCurrent()
    {
        var file = File(Root(), "x.txt");
        file.AddVersion(FileVersion.Create("t/acme/2026/11/" + Guid.NewGuid().ToString("N"), 99, "text/plain", "ana", Now), "ana", Now);
        file.CurrentVersion.ShouldBe(2);
        file.Size.ShouldBe(99);
        file.Versions.Count.ShouldBe(2);
    }
}

public class UploadSessionChunks
{
    private static UploadSession Start(long size) =>
        UploadSession.Start(Guid.NewGuid(), "big.bin", null, size, chunkSize: 10, maxFileSize: 1000, NameConflict.Fail, "t/acme/2026/11/x", "", "ana", Now, TimeSpan.FromHours(1));

    [Fact]
    public void TakesChunksInOrderAndOfTheRightSize()
    {
        var upload = Start(25);
        upload.CheckChunk(0, 10, "ana", Now).ShouldBe(1);
        upload.ChunkStored(10, null);
        Should.Throw<DomainException>(() => upload.CheckChunk(0, 10, "ana", Now));  // already have it
        Should.Throw<DomainException>(() => upload.CheckChunk(10, 5, "ana", Now));  // not the last chunk
        upload.CheckChunk(10, 10, "ana", Now).ShouldBe(2);
        upload.ChunkStored(10, "etag-2");
        upload.CheckChunk(20, 5, "ana", Now).ShouldBe(3);
        upload.ChunkStored(5, "etag-3");
        upload.IsComplete.ShouldBeTrue();
        upload.PartReceipts.ShouldBe(["etag-2", "etag-3"]);
    }

    [Fact]
    public void RefusesOtherUsersExpiredSessionsAndHugeFiles()
    {
        var upload = Start(25);
        Should.Throw<DomainException>(() => upload.CheckChunk(0, 10, "bob", Now));
        Should.Throw<DomainException>(() => upload.CheckChunk(0, 10, "ana", Now.AddHours(2)));
        Should.Throw<DomainException>(() => Start(5000));
    }
}
