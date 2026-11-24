using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using soft.square.net.WebApi.Modules.Storage.Core.BlobAggregate;
using soft.square.net.WebApi.Modules.Storage.Core.UploadAggregate;
using soft.square.net.WebApi.Modules.Storage.UseCases.Items;
using static soft.square.net.WebApi.Modules.Storage.UnitTests.Tree;
using static soft.square.net.WebApi.Modules.Storage.UnitTests.UseCases.HandlerFixture;

namespace soft.square.net.WebApi.Modules.Storage.UnitTests.UseCases;

/// <summary>The recycle bin: what it lists, restoring, purging (and the bytes that go with it).</summary>
public class RecycleBinHandlers
{
    private readonly HandlerFixture _f = new();
    private readonly Item _root;
    private readonly Dictionary<string, Blob> _blobs = [];

    public RecycleBinHandlers()
    {
        _root = _f.Put(Root(Allow(PrincipalKind.Everyone, null, StorageRights.Modify)));
        _f.Store.RecycleBinAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(call =>
            _f.Items.Where(i => i.IsDeleted && (call.Arg<string?>() is not { } by || i.DeletedBy == by)).ToArray());
        _f.Store.SubtreeAsync(Arg.Any<Item>(), Arg.Any<CancellationToken>()).Returns(call =>
            _f.Items.Where(i => i.Path.StartsWith(call.Arg<Item>().Path) && i.Id != call.Arg<Item>().Id).ToArray());
        _f.Store.BlobsAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns(call =>
            call.Arg<IReadOnlyCollection<string>>().Where(_blobs.ContainsKey).Select(k => _blobs[k]).ToArray());
        _f.Store.UnusedBlobsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(_ => _blobs.Values.Where(b => b.References <= 0).ToArray());
        _f.Store.When(s => s.Remove(Arg.Any<Blob>())).Do(call => _blobs.Remove(call.Arg<Blob>().Id));
    }

    private Item FileWithBlob(Item folder, string name, string by = "ana")
    {
        var file = _f.Put(File(folder, name, by));
        _blobs[file.Current!.BlobKey] = Blob.Create(file.Current.BlobKey, "", file.Size, Now);
        return file;
    }

    [Fact]
    public async Task ABinShowsWhatYouDeletedAndAdminsSeeEverything()
    {
        var mine = FileWithBlob(_root, "mine.txt");
        mine.Delete("ana", Now);
        var theirs = FileWithBlob(_root, "theirs.txt", "bob");
        theirs.Delete("bob", Now);

        var bin = await new GetRecycleBinHandler(_f.Store, _f.Caller).Handle(new GetRecycleBinQuery(), Ct);
        bin.Value.Select(d => d.Item.Id).ShouldBe([mine.Id]);
        bin.Value[0].OriginalFolderId.ShouldBe(_root.Id);
        bin.Value[0].OriginalPath.Select(c => c.Id).ShouldBe([_root.Id]);

        var admin = new HandlerFixture(admin: true);
        admin.Store.RecycleBinAsync(null, Arg.Any<CancellationToken>()).Returns([mine, theirs]);
        admin.Store.ChainAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(call => _f.Store.ChainAsync(call.Arg<Guid>(), Ct));
        (await new GetRecycleBinHandler(admin.Store, admin.Caller).Handle(new GetRecycleBinQuery(), Ct)).Value.Count.ShouldBe(2);
    }

    [Fact]
    public async Task RestoreBringsItBackAndNumbersATakenName()
    {
        var old = FileWithBlob(_root, "report.txt");
        old.Delete("ana", Now);
        FileWithBlob(_root, "report.txt"); // a new file took the name meanwhile

        var result = await new RestoreItemsHandler(_f.Store, _f.Caller, _f.Access).Handle(new RestoreItemsCommand([old.Id]), Ct);

        result.Value.Done.Select(d => d.Id).ShouldBe([old.Id]);
        old.IsDeleted.ShouldBeFalse();
        old.Name.ShouldBe("report (2).txt");
    }

    [Fact]
    public async Task AnItemInADeletedFolderWaitsForItsFolder()
    {
        var folder = _f.Put(Folder(_root, "Reports", "ana"));
        var file = FileWithBlob(folder, "a.txt");
        file.Delete("ana", Now);
        folder.Delete("ana", Now);

        var result = await new RestoreItemsHandler(_f.Store, _f.Caller, _f.Access).Handle(new RestoreItemsCommand([file.Id]), Ct);

        result.Value.Failed.Single().Reason.ShouldContain("Reports");
        file.IsDeleted.ShouldBeTrue();
    }

    [Fact]
    public async Task PurgeRemovesTheSubtreeAndDeletesBytesNobodyUses()
    {
        var folder = _f.Put(Folder(_root, "Old", "ana"));
        var inside = FileWithBlob(folder, "a.txt");
        var copy = _f.Put(Item.CreateFile(_root, "copy.txt", "ana",
            FileVersion.Create(inside.Current!.BlobKey, inside.Size, "text/plain", "ana", Now), Now)); // shares the blob
        _blobs[inside.Current.BlobKey].AddReference();
        var lonely = FileWithBlob(folder, "b.txt");
        folder.Delete("ana", Now);

        var result = await new PurgeItemsHandler(_f.Store, _f.Caller, _f.Purger).Handle(new PurgeItemsCommand([folder.Id]), Ct);

        result.Value.Done.Select(d => d.Id).ShouldBe([folder.Id]);
        _f.Store.Received(1).Remove(folder);
        _f.Store.Received(1).Remove(inside);
        _f.Store.Received(1).Remove(lonely);
        await _f.Blobs.Received(1).DeleteAsync(lonely.Current!.BlobKey, Arg.Any<CancellationToken>());
        await _f.Blobs.DidNotReceive().DeleteAsync(copy.Current!.BlobKey, Arg.Any<CancellationToken>());
        _blobs[copy.Current.BlobKey].References.ShouldBe(1);
    }

    [Fact]
    public async Task OthersCannotPurgeWhatYouDeleted()
    {
        var theirs = FileWithBlob(_root, "theirs.txt", "bob");
        theirs.Delete("bob", Now);

        var result = await new PurgeItemsHandler(_f.Store, _f.Caller, _f.Purger).Handle(new PurgeItemsCommand([theirs.Id]), Ct);

        result.Value.Failed.Single().Id.ShouldBe(theirs.Id);
        _f.Store.DidNotReceive().Remove(theirs);
    }

    [Fact]
    public async Task ShiftDeleteSkipsTheBin()
    {
        var file = FileWithBlob(_root, "gone.txt");

        var result = await new DeleteItemsHandler(_f.Store, _f.Access, _f.Caller, _f.Purger).Handle(new DeleteItemsCommand([file.Id], Permanent: true), Ct);

        result.Value.Done.Single().Id.ShouldBe(file.Id);
        _f.Store.Received(1).Remove(file);
        await _f.Blobs.Received(1).DeleteAsync(file.Current!.BlobKey, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BytesThatCannotBeDeletedWaitForTheNextSweep()
    {
        var file = FileWithBlob(_root, "a.txt");
        file.Delete("ana", Now);
        _f.Blobs.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new IOException("store down")));

        await new PurgeItemsHandler(_f.Store, _f.Caller, _f.Purger).Handle(new PurgeItemsCommand([]), Ct);

        _blobs.ShouldContainKey(file.Current!.BlobKey);
        _blobs[file.Current.BlobKey].References.ShouldBe(0);
    }
}

public class VersionHandlers
{
    private readonly HandlerFixture _f = new();
    private readonly Blob _first;
    private readonly Item _file;

    public VersionHandlers()
    {
        var root = _f.Put(Root(Allow(PrincipalKind.Everyone, null, StorageRights.Modify)));
        _file = _f.Put(File(root, "plan.txt", "ana"));
        _first = Blob.Create(_file.Current!.BlobKey, "", 10, Now);
        _file.AddVersion(FileVersion.Create("t/acme/2026/11/second", 20, "text/plain", "ana", Now), "ana", Now);
        var blobs = new Dictionary<string, Blob> { [_first.Id] = _first, ["t/acme/2026/11/second"] = Blob.Create("t/acme/2026/11/second", "", 20, Now) };
        _f.Store.BlobsAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns(call =>
            call.Arg<IReadOnlyCollection<string>>().Where(blobs.ContainsKey).Select(k => blobs[k]).ToArray());
        _f.Store.UnusedBlobsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(_ => blobs.Values.Where(b => b.References <= 0).ToArray());
    }

    [Fact]
    public async Task RestoringAVersionAddsItAsTheNewest()
    {
        var result = await new RestoreVersionHandler(_f.Store, _f.Access, _f.Caller).Handle(new RestoreVersionCommand(_file.Id, 1), Ct);

        result.IsSuccess.ShouldBeTrue();
        _file.CurrentVersion.ShouldBe(3);
        _file.Current!.BlobKey.ShouldBe(_first.Id);
        _file.Size.ShouldBe(10);
        _first.References.ShouldBe(2);
        result.Value.Versions.Select(v => v.Number).ShouldBe([3, 2, 1]);
    }

    [Fact]
    public async Task RemovingAnOldVersionFreesItsBytes()
    {
        var result = await new DeleteVersionHandler(_f.Store, _f.Access, _f.Caller, _f.Purger).Handle(new DeleteVersionCommand(_file.Id, 1), Ct);

        result.Value.Versions.Select(v => v.Number).ShouldBe([2]);
        await _f.Blobs.Received(1).DeleteAsync(_first.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheCurrentVersionStays()
    {
        await Should.ThrowAsync<DomainException>(async () =>
            await new DeleteVersionHandler(_f.Store, _f.Access, _f.Caller, _f.Purger).Handle(new DeleteVersionCommand(_file.Id, 2), Ct));
        Should.Throw<DomainException>(() => _file.RestoreVersion(2, "ana", Now));
    }

    [Fact]
    public async Task AMissingVersionIsNotFound()
    {
        var result = await new RestoreVersionHandler(_f.Store, _f.Access, _f.Caller).Handle(new RestoreVersionCommand(_file.Id, 9), Ct);
        result.Status.ShouldBe(ResultStatus.NotFound);
    }
}

public class SweepHandlerHandle
{
    private readonly HandlerFixture _f = new();
    private readonly StorageOptions _options = new() { RecycleBinDays = 30 };

    private SweepHandler Handler => new(_f.Store, _f.Stores, _f.Purger, _f.Clock, Options.Create(_options), NullLoggerFactory.Instance);

    [Fact]
    public async Task PurgesWhatStayedTooLongAndExpiresUploads()
    {
        var root = _f.Put(Root(Allow(PrincipalKind.Everyone, null, StorageRights.Modify)));
        var old = _f.Put(File(root, "old.txt", "ana"));
        old.Delete("ana", Now.AddDays(-31));
        _f.Store.DeletedBeforeAsync(Now.AddDays(-30), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([old]);
        _f.Store.BlobsAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns([]);
        _f.Store.UnusedBlobsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        var upload = UploadSession.Start(root.Id, "big.bin", null, 100, 10, 1000, NameConflict.Fail, "t/acme/x", "", "ana", Now.AddDays(-2), TimeSpan.FromDays(1));
        _f.Store.ExpiredUploadsAsync(Now, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([upload]);

        var result = await Handler.Handle(new SweepCommand(), Ct);

        result.Value.ItemsPurged.ShouldBe(1);
        result.Value.UploadsExpired.ShouldBe(1);
        _f.Store.Received(1).Remove(old);
        _f.Store.Received(1).Remove(upload);
        await _f.Blobs.Received(1).AbortUploadAsync("t/acme/x", Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ZeroDaysKeepsTheBin()
    {
        _options.RecycleBinDays = 0;
        _f.Store.ExpiredUploadsAsync(Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        _f.Store.UnusedBlobsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);

        await Handler.Handle(new SweepCommand(), Ct);

        await _f.Store.DidNotReceive().DeletedBeforeAsync(Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
