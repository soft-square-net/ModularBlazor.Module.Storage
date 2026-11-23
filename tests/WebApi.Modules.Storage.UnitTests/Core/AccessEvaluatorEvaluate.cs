using static soft.square.net.WebApi.Modules.Storage.UnitTests.Tree;

namespace soft.square.net.WebApi.Modules.Storage.UnitTests.Core;

public class AccessEvaluatorEvaluate
{
    private static readonly TestPrincipal Ana = new("ana");

    private static StorageRights Rights(TestPrincipal who, params Item[] chain) => AccessEvaluator.Evaluate(chain, who);

    [Fact]
    public void GivesNothingWithoutEntries()
    {
        var root = Root();
        var file = File(root, "a.txt", by: "someone");
        Rights(Ana, root, file).ShouldBe(StorageRights.None);
    }

    [Fact]
    public void InheritsDownTheTree()
    {
        var root = Root(Allow(PrincipalKind.User, "ana", StorageRights.Modify));
        var a = Folder(root, "a");
        var file = File(a, "doc.txt");
        Rights(Ana, root, a, file).ShouldBe(StorageRights.Modify);
    }

    [Fact]
    public void ThisFolderOnlyDoesNotReachChildren()
    {
        var root = Root(Allow(PrincipalKind.Everyone, null, StorageRights.ReadAndExecute, AceInheritance.None));
        var a = Folder(root, "a");
        Rights(Ana, root).ShouldBe(StorageRights.ReadAndExecute);
        Rights(Ana, root, a).ShouldBe(StorageRights.None);
    }

    [Fact]
    public void AnExplicitDenyBeatsAnInheritedAllow()
    {
        var root = Root(Allow(PrincipalKind.Everyone, null, StorageRights.Modify));
        var a = Folder(root, "a");
        Set(a, true, Deny(PrincipalKind.User, "ana", StorageRights.Delete));
        Rights(Ana, root, a).ShouldBe(StorageRights.Modify & ~StorageRights.Delete);
    }

    [Fact]
    public void AnExplicitAllowBeatsAnInheritedDeny()
    {
        var root = Root(Deny(PrincipalKind.User, "ana", StorageRights.Read));
        var a = Folder(root, "a");
        Set(a, true, Allow(PrincipalKind.User, "ana", StorageRights.Read));
        Rights(Ana, root, a).ShouldBe(StorageRights.Read);
    }

    [Fact]
    public void ANearerInheritedDenyBeatsAFartherAllow()
    {
        var root = Root(Allow(PrincipalKind.Everyone, null, StorageRights.Read));
        var a = Folder(root, "a");
        Set(a, true, Deny(PrincipalKind.User, "ana", StorageRights.ListFolder));
        var b = Folder(a, "b");
        Rights(Ana, root, a, b).ShouldBe(StorageRights.Read & ~StorageRights.ListFolder);
    }

    [Fact]
    public void DenyAndAllowAtTheSameLevelDenyWins()
    {
        var root = Root(Allow(PrincipalKind.Everyone, null, StorageRights.Read), Deny(PrincipalKind.User, "ana", StorageRights.Read));
        Rights(Ana, root).ShouldBe(StorageRights.None);
        Rights(new TestPrincipal("bob"), root).ShouldBe(StorageRights.Read);
    }

    [Fact]
    public void AFolderThatDoesNotInheritCutsTheTree()
    {
        var root = Root(Allow(PrincipalKind.Everyone, null, StorageRights.FullControl));
        var a = Folder(root, "a");
        Set(a, false, Allow(PrincipalKind.User, "bob", StorageRights.Read));
        var file = File(a, "x.txt");
        Rights(Ana, root, a, file).ShouldBe(StorageRights.None);
        Rights(new TestPrincipal("bob"), root, a, file).ShouldBe(StorageRights.Read);
    }

    [Fact]
    public void NoPropagateReachesDirectChildrenOnly()
    {
        var root = Root(Allow(PrincipalKind.User, "ana", StorageRights.Read, AceInheritance.Subfolders | AceInheritance.Files | AceInheritance.NoPropagate));
        var a = Folder(root, "a");
        var b = Folder(a, "b");
        Rights(Ana, root, a).ShouldBe(StorageRights.Read);
        Rights(Ana, root, a, b).ShouldBe(StorageRights.None);
    }

    [Fact]
    public void FilesOnlyEntriesSkipFoldersButReachFilesAtAnyDepth()
    {
        var root = Root(Allow(PrincipalKind.User, "ana", StorageRights.Read, AceInheritance.Files | AceInheritance.InheritOnly));
        var a = Folder(root, "a");
        var file = File(a, "deep.txt");
        Rights(Ana, root).ShouldBe(StorageRights.None);
        Rights(Ana, root, a).ShouldBe(StorageRights.None);
        Rights(Ana, root, a, file).ShouldBe(StorageRights.Read);
    }

    [Fact]
    public void CreatorOwnerAppliesToTheOwnerOfEachNewItem()
    {
        var root = Root(
            Allow(PrincipalKind.Everyone, null, StorageRights.ReadAndExecute | StorageRights.CreateFiles, AceInheritance.None),
            Allow(PrincipalKind.CreatorOwner, null, StorageRights.Modify, AceInheritance.Subfolders | AceInheritance.Files | AceInheritance.InheritOnly));
        var mine = File(root, "mine.txt", by: "ana");
        var theirs = File(root, "theirs.txt", by: "bob");
        Rights(Ana, root, mine).ShouldBe(StorageRights.Modify | StorageRights.ChangePermissions);
        Rights(Ana, root, theirs).ShouldBe(StorageRights.None);
        // On the folder itself the entry is inherit-only: it gives nothing there.
        Rights(Ana, root).ShouldBe(StorageRights.ReadAndExecute | StorageRights.CreateFiles);
    }

    [Fact]
    public void TheOwnerCanAlwaysReadAndChangePermissions()
    {
        var root = Root();
        var file = File(root, "a.txt", by: "ana");
        Rights(Ana, root, file).ShouldBe(StorageRights.ReadPermissions | StorageRights.ChangePermissions);
    }

    [Fact]
    public void GroupsAndPermissionHoldersMatchThroughThePrincipal()
    {
        var group = Guid.NewGuid().ToString("D");
        var root = Root(
            Allow(PrincipalKind.Group, group, StorageRights.ListFolder),
            Allow(PrincipalKind.Permission, "content.editor", StorageRights.ReadAttributes, scope: "press:daily"));
        Rights(new TestPrincipal("x", groups: [group]), root).ShouldBe(StorageRights.ListFolder);
        Rights(new TestPrincipal("y", permissions: [("content.editor", "press:daily")]), root).ShouldBe(StorageRights.ReadAttributes);
        Rights(new TestPrincipal("z"), root).ShouldBe(StorageRights.None);
    }

    [Fact]
    public void HomeFoldersBelongToTheirUserOnly()
    {
        var homes = Item.CreateTop("acme", FolderRole.Homes, "Users",
            [AccessEntry.Create(PrincipalKind.Everyone, null, null, AceType.Allow, StorageRights.Traverse)], Now);
        var home = Item.CreateHome(homes, "ana", Now);
        var file = File(home, "cv.pdf", by: "ana");
        Rights(Ana, homes, home, file).ShouldBe(StorageRights.FullControl);
        Rights(new TestPrincipal("bob"), homes, home, file).ShouldBe(StorageRights.None);
        Rights(new TestPrincipal("bob"), homes).ShouldBe(StorageRights.Traverse);
    }
}

public class AccessEvaluatorInherited
{
    [Fact]
    public void ConvertsInheritedEntriesWithTheFlagsTheyHaveBelow()
    {
        var root = Root(
            Allow(PrincipalKind.User, "ana", StorageRights.Read),
            Allow(PrincipalKind.User, "bob", StorageRights.Read, AceInheritance.Files | AceInheritance.InheritOnly),
            Allow(PrincipalKind.User, "eve", StorageRights.Read, AceInheritance.None));
        var a = Folder(root, "a");

        var rows = AccessEvaluator.Inherited([root, a]);

        rows.Select(r => (r.Entry.PrincipalId, r.Entry.Inheritance)).ShouldBe(
        [
            ("ana", AceInheritance.Subfolders | AceInheritance.Files),
            ("bob", AceInheritance.Files | AceInheritance.InheritOnly)
        ]);
        rows.ShouldAllBe(r => r.Source == root && r.Distance == 1);
    }

    [Fact]
    public void ConvertingKeepsAccessTheSame()
    {
        var root = Root(Allow(PrincipalKind.User, "ana", StorageRights.Modify), Allow(PrincipalKind.User, "bob", StorageRights.Read, AceInheritance.Files | AceInheritance.InheritOnly));
        var a = Folder(root, "a");
        var file = File(a, "x.txt");
        var before = (AccessEvaluator.Evaluate([root, a, file], new TestPrincipal("ana")), AccessEvaluator.Evaluate([root, a, file], new TestPrincipal("bob")));

        Set(a, false, [.. AccessEvaluator.InheritedAsExplicit([root, a])]);

        (AccessEvaluator.Evaluate([root, a, file], new TestPrincipal("ana")), AccessEvaluator.Evaluate([root, a, file], new TestPrincipal("bob"))).ShouldBe(before);
    }
}
