using soft.square.net.WebApi.Modules.Storage.UseCases.Items;
using static soft.square.net.WebApi.Modules.Storage.UnitTests.Tree;
using static soft.square.net.WebApi.Modules.Storage.UnitTests.UseCases.HandlerFixture;

namespace soft.square.net.WebApi.Modules.Storage.UnitTests.UseCases;

public class CreateFolderHandlerHandle
{
    private readonly HandlerFixture _f = new();

    private CreateFolderHandler Handler => new(_f.Store, _f.Access, _f.Caller);

    [Fact]
    public async Task NeedsCreateFoldersOnTheParent()
    {
        var root = _f.Put(Root(Allow(PrincipalKind.Everyone, null, StorageRights.ReadAndExecute)));

        var result = await Handler.Handle(new CreateFolderCommand(root.Id, "Reports", false), Ct);

        result.Status.ShouldBe(ResultStatus.Forbidden);
        _f.Items.Count.ShouldBe(1);
    }

    [Fact]
    public async Task CreatesOwnedByTheCaller()
    {
        var root = _f.Put(Root(Allow(PrincipalKind.Everyone, null, StorageRights.Modify)));

        var result = await Handler.Handle(new CreateFolderCommand(root.Id, "Reports", false), Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Name.ShouldBe("Reports");
        result.Value.OwnerId.ShouldBe("ana");
        ((StorageRights)result.Value.Rights).HasFlag(StorageRights.Modify).ShouldBeTrue();
        await _f.Store.Received(1).SaveAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NumbersATakenNameWhenAsked()
    {
        var root = _f.Put(Root(Allow(PrincipalKind.Everyone, null, StorageRights.Modify)));
        _f.Put(Folder(root, "New folder"));

        (await Handler.Handle(new CreateFolderCommand(root.Id, "new folder", false), Ct)).Status.ShouldBe(ResultStatus.Conflict);
        (await Handler.Handle(new CreateFolderCommand(root.Id, "New folder", true), Ct)).Value.Name.ShouldBe("New folder (2)");
    }
}

public class ListFolderHandlerHandle
{
    [Fact]
    public async Task ShowsOnlyItemsTheCallerHasRightsOn()
    {
        var f = new HandlerFixture();
        var root = f.Put(Root(Allow(PrincipalKind.Everyone, null, StorageRights.ReadAndExecute, AceInheritance.None)));
        var open = f.Put(Folder(root, "Open"));
        open.SetAccess(true, [Allow(PrincipalKind.Everyone, null, StorageRights.Read)], "admin", Now);
        f.Put(Folder(root, "Secret"));
        var file = f.Put(File(root, "mine.txt", by: "ana"));

        var result = await new ListFolderHandler(f.Store, f.Access).Handle(new ListFolderQuery(root.Id), Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Items.Select(i => i.Name).ShouldBe(["Open", "mine.txt"]); // folders first; owner sees their own file
        result.Value.Path.Single().Id.ShouldBe(root.Id);
    }

    [Fact]
    public async Task StorageAdminsSeeEverything()
    {
        var f = new HandlerFixture(admin: true);
        var root = f.Put(Root());
        f.Put(Folder(root, "Secret"));

        var result = await new ListFolderHandler(f.Store, f.Access).Handle(new ListFolderQuery(root.Id), Ct);

        result.Value.Items.Single().Rights.ShouldBe((int)StorageRights.FullControl);
    }

    [Fact]
    public async Task AFolderUnderADeletedFolderIsGone()
    {
        var f = new HandlerFixture(admin: true);
        var root = f.Put(Root());
        var a = f.Put(Folder(root, "a"));
        var b = f.Put(Folder(a, "b"));
        a.Delete("ana", Now);

        (await new ListFolderHandler(f.Store, f.Access).Handle(new ListFolderQuery(b.Id), Ct)).Status.ShouldBe(ResultStatus.NotFound);
    }
}

public class RenameAndDeleteHandle
{
    [Fact]
    public async Task DeleteChildrenOnTheFolderIsEnoughToRename()
    {
        var f = new HandlerFixture();
        var root = f.Put(Root(Allow(PrincipalKind.User, "ana", StorageRights.DeleteChildren | StorageRights.ListFolder)));
        var file = f.Put(File(root, "old.txt", by: "bob"));
        file.SetAccess(false, [], "admin", Now); // nothing on the file itself

        var result = await new RenameItemHandler(f.Store, f.Access, f.Caller).Handle(new RenameItemCommand(file.Id, "new.txt", null), Ct);

        result.IsSuccess.ShouldBeTrue();
        file.Name.ShouldBe("new.txt");
    }

    [Fact]
    public async Task DeleteReportsEachItem()
    {
        var f = new HandlerFixture();
        var root = f.Put(Root(Allow(PrincipalKind.User, "ana", StorageRights.ListFolder, AceInheritance.None)));
        var mine = f.Put(File(root, "mine.txt", by: "ana"));
        mine.SetAccess(true, [Allow(PrincipalKind.User, "ana", StorageRights.Modify, AceInheritance.None)], "ana", Now);
        var theirs = f.Put(File(root, "theirs.txt", by: "bob"));

        var result = await new DeleteItemsHandler(f.Store, f.Access, f.Caller, f.Purger).Handle(new DeleteItemsCommand([mine.Id, theirs.Id]), Ct);

        result.Value.Done.Select(d => d.Id).ShouldBe([mine.Id]);
        result.Value.Failed.Select(d => d.Id).ShouldBe([theirs.Id]);
        mine.IsDeleted.ShouldBeTrue();
        theirs.IsDeleted.ShouldBeFalse();
    }
}
