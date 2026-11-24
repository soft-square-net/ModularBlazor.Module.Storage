using soft.square.net.WebApi.Modules.Storage.Core.GroupAggregate;
using soft.square.net.WebApi.Modules.Storage.Core.PersonAggregate;
using soft.square.net.WebApi.Modules.Storage.UseCases.Access;
using soft.square.net.WebApi.Modules.Storage.UseCases.Sharing;
using static soft.square.net.WebApi.Modules.Storage.UnitTests.Tree;
using static soft.square.net.WebApi.Modules.Storage.UnitTests.UseCases.HandlerFixture;

namespace soft.square.net.WebApi.Modules.Storage.UnitTests.UseCases;

/// <summary>The Security dialog's reads and writes: names for people, groups and the owner; Apply keeps what it is given.</summary>
public class AccessHandlers
{
    private readonly HandlerFixture _f = new();
    private readonly StorageGroup _editors = StorageGroup.Create("Editors", null, ["bob"], Now);
    private readonly AclReader _reader;

    public AccessHandlers()
    {
        _f.Store.PeopleAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([StoragePerson.Create("bob", "Bob Builder", "bob@example.test", Now), StoragePerson.Create("owner-1", "Olga Owner", null, Now)]);
        _f.Groups.ListAsync(Arg.Any<CancellationToken>()).Returns([_editors]);
        _reader = new AclReader(new PeopleBook(_f.Store, _f.Caller, Substitute.For<ICallerProfile>()), _f.Groups);
    }

    [Fact]
    public async Task TheAccessListNamesItsPeopleGroupsAndOwner()
    {
        var root = _f.Put(Root(Allow(PrincipalKind.Everyone, null, StorageRights.FullControl), Allow(PrincipalKind.Group, _editors.Id.ToString(), StorageRights.Modify)));
        var folder = _f.Put(Folder(root, "reports"));
        Set(folder, true, Allow(PrincipalKind.User, "bob", StorageRights.Read), Allow(PrincipalKind.User, "carol", StorageRights.Read));

        var acl = (await new GetAclHandler(_f.Access, _reader).Handle(new GetAclQuery(folder.Id), Ct)).Value;

        acl.OwnerName.ShouldBe("Olga Owner");
        acl.Entries.Where(e => !e.Inherited).Select(e => e.PrincipalName).ShouldBe(["Bob Builder", "carol"]); // carol has not opened Files: her id
        acl.Entries.Single(e => e.PrincipalKind == PrincipalKind.Group).PrincipalName.ShouldBe("Editors");
        acl.Entries.Single(e => e.PrincipalKind == PrincipalKind.Everyone).PrincipalName.ShouldBeNull();
    }

    [Fact]
    public async Task ApplySavesTheOwnEntriesAndIgnoresInheritedRowsAndNames()
    {
        var root = _f.Put(Root(Allow(PrincipalKind.Everyone, null, StorageRights.FullControl)));
        var folder = _f.Put(Folder(root, "reports"));
        var read = await new GetAclHandler(_f.Access, _reader).Handle(new GetAclQuery(folder.Id), Ct);
        var bob = AceDto.Explicit(Allow(PrincipalKind.User, "bob", StorageRights.Modify)) with { PrincipalName = "whatever the client says" };

        var saved = await new SetAclHandler(_f.Store, _f.Access, _f.Caller, _reader)
            .Handle(new SetAclCommand(folder.Id, false, [.. read.Value.Entries, bob], ConvertInherited: false, read.Value.Version), Ct);

        saved.IsSuccess.ShouldBeTrue(string.Join(" ", saved.Errors));
        folder.InheritsAccess.ShouldBeFalse();
        folder.Entries.Single().PrincipalId.ShouldBe("bob");
        saved.Value.Entries.Single().PrincipalName.ShouldBe("Bob Builder");
    }
}
