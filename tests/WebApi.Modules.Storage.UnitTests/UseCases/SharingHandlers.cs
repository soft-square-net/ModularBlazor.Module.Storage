using Microsoft.Extensions.Options;
using soft.square.net.WebApi.Modules.Storage.Core.PersonAggregate;
using soft.square.net.WebApi.Modules.Storage.Core.ShareAggregate;
using soft.square.net.WebApi.Modules.Storage.UseCases.Blobs;
using soft.square.net.WebApi.Modules.Storage.UseCases.Sharing;
using static soft.square.net.WebApi.Modules.Storage.UnitTests.Tree;
using static soft.square.net.WebApi.Modules.Storage.UnitTests.UseCases.HandlerFixture;

namespace soft.square.net.WebApi.Modules.Storage.UnitTests.UseCases;

/// <summary>Sharing: with people (entries), "anyone with the link" links, public folders.</summary>
public class SharingHandlers
{
    private readonly HandlerFixture _f = new();
    private readonly Item _root;
    private readonly Item _reports;
    private readonly Item _file;
    private readonly List<ShareLink> _links = [];
    private readonly ICallerProfile _profile = Substitute.For<ICallerProfile>();
    private readonly IDownloadTickets _tickets = Substitute.For<IDownloadTickets>();
    private readonly IOptions<StorageOptions> _options = Options.Create(new StorageOptions());

    public SharingHandlers()
    {
        // ana has full control (so Share); everyone may only pass through.
        _root = _f.Put(Root(Allow(PrincipalKind.User, "ana", StorageRights.FullControl), Allow(PrincipalKind.Everyone, null, StorageRights.Traverse)));
        _reports = _f.Put(Folder(_root, "Reports", "ana"));
        _file = _f.Put(File(_reports, "q3.pdf", "ana"));
        _f.Store.LinksOfAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(call => _links.Where(l => l.ItemId == call.Arg<Guid>()).ToArray());
        _f.Store.LinkByTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => _links.FirstOrDefault(l => l.Token == call.Arg<string>()));
        _f.Store.LinkAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(call => _links.FirstOrDefault(l => l.Id == call.Arg<Guid>()));
        _f.Store.When(s => s.Add(Arg.Any<ShareLink>())).Do(call => _links.Add(call.Arg<ShareLink>()));
        _f.Store.When(s => s.Remove(Arg.Any<ShareLink>())).Do(call => _links.Remove(call.Arg<ShareLink>()));
        _f.Store.PeopleAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([StoragePerson.Create("bob", "Bob Builder", "bob@example.test", Now)]);
        _f.Store.BlobsAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns([]);
        _f.Blobs.GetDownloadUrlAsync(default!, default!, default!, default, default, default)
            .ReturnsForAnyArgs(new Uri("https://blobs.example.test/q3.pdf"));
    }

    private Sharer Sharer(HandlerFixture f) => new(f.Store, f.Caller, new PeopleBook(f.Store, f.Caller, _profile), f.Tenant);

    /// <summary>bob, in the same tree (the store and items are ana's fixture's).</summary>
    private HandlerFixture Bob()
    {
        var bob = new HandlerFixture("bob");
        bob.Store.ChainAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(call => _f.Store.ChainAsync(call.Arg<Guid>(), Ct));
        return bob;
    }

    [Fact]
    public async Task SharingAFolderToViewLetsThemReadWhatIsInIt()
    {
        var bob = Bob();
        (await bob.Caller.RightsAsync([_root, _reports, _file], Ct)).HasFlag(StorageRights.ListFolder).ShouldBeFalse();

        var view = await new ShareWithPeopleHandler(_f.Store, _f.Access, _f.Caller, Sharer(_f))
            .Handle(new ShareWithPeopleCommand(_reports.Id, ["bob"], ShareAccess.View), Ct);

        view.IsSuccess.ShouldBeTrue();
        view.Value.People.ShouldHaveSingleItem().ShouldBe(new SharedPersonDto("bob", "Bob Builder", "bob@example.test", ShareAccess.View));
        var rights = await new HandlerFixture("bob").Caller.RightsAsync([_root, _reports, _file], Ct);
        rights.HasFlag(StorageRights.ListFolder).ShouldBeTrue();
        rights.HasFlag(StorageRights.CreateFiles).ShouldBeFalse();
    }

    [Fact]
    public async Task SharingAgainReplacesTheirAccessAndStopRemovesIt()
    {
        var share = new ShareWithPeopleHandler(_f.Store, _f.Access, _f.Caller, Sharer(_f));
        await share.Handle(new ShareWithPeopleCommand(_reports.Id, ["bob"], ShareAccess.View), Ct);
        var edit = await share.Handle(new ShareWithPeopleCommand(_reports.Id, ["bob"], ShareAccess.Edit), Ct);
        edit.Value.People.ShouldHaveSingleItem().Access.ShouldBe(ShareAccess.Edit);
        _reports.Entries.Count(e => e.PrincipalId == "bob").ShouldBe(1);

        var stopped = await new StopSharingHandler(_f.Store, _f.Access, _f.Caller, Sharer(_f)).Handle(new StopSharingCommand(_reports.Id, "bob"), Ct);
        stopped.Value.People.ShouldBeEmpty();
        _reports.Entries.ShouldNotContain(e => e.PrincipalId == "bob");
    }

    [Fact]
    public async Task YouCannotShareToEditWhatYouCanOnlyRead()
    {
        Set(_reports, true, Allow(PrincipalKind.User, "carl", StorageRights.ReadAndExecute | StorageRights.Share));
        var carl = new HandlerFixture("carl");
        carl.Store.ChainAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(call => _f.Store.ChainAsync(call.Arg<Guid>(), Ct));

        var result = await new ShareWithPeopleHandler(carl.Store, carl.Access, carl.Caller, Sharer(carl))
            .Handle(new ShareWithPeopleCommand(_reports.Id, ["bob"], ShareAccess.Edit), Ct);

        result.Status.ShouldBe(ResultStatus.Forbidden);
    }

    [Fact]
    public async Task SharingNeedsTheShareRight()
    {
        var result = await new ShareWithPeopleHandler(Bob().Store, Bob().Access, Bob().Caller, Sharer(Bob()))
            .Handle(new ShareWithPeopleCommand(_reports.Id, ["dan"], ShareAccess.View), Ct);
        result.Status.ShouldBe(ResultStatus.Forbidden);
    }

    [Fact]
    public async Task SharedWithMeListsWhatWasSharedButNotYourOwnHome()
    {
        await new ShareWithPeopleHandler(_f.Store, _f.Access, _f.Caller, Sharer(_f)).Handle(new ShareWithPeopleCommand(_file.Id, ["bob"], ShareAccess.View), Ct);
        var bob = Bob();
        var home = Item.CreateHome(_f.Put(Item.CreateTop("acme", FolderRole.Homes, "Users", [], Now)), "bob", Now);
        _f.Put(home);
        bob.Store.SharedWithAsync("bob", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([_file, home]);
        bob.Store.PeopleAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns([]);

        var shared = await new GetSharedWithMeHandler(bob.Store, bob.Caller, new PeopleBook(bob.Store, bob.Caller, _profile))
            .Handle(new GetSharedWithMeQuery(), Ct);

        shared.Value.ShouldHaveSingleItem().Item.Id.ShouldBe(_file.Id);
        shared.Value[0].OwnerName.ShouldBe("ana"); // not on Storage's list yet: by id
    }

    [Fact]
    public async Task ALinkWithAPasswordOpensTheFolderAndOnlyWhatIsInIt()
    {
        var link = (await new CreateShareLinkHandler(_f.Store, _f.Access, _f.Caller)
            .Handle(new CreateShareLinkCommand(_reports.Id, null, "s3cret", null), Ct)).Value;
        link.HasPassword.ShouldBeTrue();
        var open = new OpenShareHandler(_f.Store, _f.Clock);

        (await open.Handle(new OpenShareQuery(link.Token, null, null), Ct)).Status.ShouldBe(ResultStatus.Unauthorized);
        (await open.Handle(new OpenShareQuery(link.Token, "wrong", null), Ct)).Status.ShouldBe(ResultStatus.Unauthorized);
        var view = await open.Handle(new OpenShareQuery(link.Token, "s3cret", null), Ct);
        view.Value.Shared.Id.ShouldBe(_reports.Id);
        view.Value.Items.Select(i => i.Name).ShouldBe(["q3.pdf"]);
        view.Value.Path.Select(p => p.Id).ShouldBe([_reports.Id]); // nothing above the shared folder

        (await open.Handle(new OpenShareQuery(link.Token, "s3cret", _root.Id), Ct)).Status.ShouldBe(ResultStatus.NotFound);
        (await open.Handle(new OpenShareQuery("no-such-token", null, null), Ct)).Status.ShouldBe(ResultStatus.NotFound);
    }

    [Fact]
    public async Task ADownloadLimitStopsTheLinkAndRemovingItStopsItAtOnce()
    {
        var link = (await new CreateShareLinkHandler(_f.Store, _f.Access, _f.Caller)
            .Handle(new CreateShareLinkCommand(_reports.Id, null, null, 1), Ct)).Value;
        var file = new OpenSharedFileHandler(_f.Store, _f.Stores, _tickets, _f.Tenant, _f.Clock, _options);

        (await file.Handle(new OpenSharedFileQuery(link.Token, null, _file.Id, false), Ct)).Value.Url.Host.ShouldBe("blobs.example.test");
        (await file.Handle(new OpenSharedFileQuery(link.Token, null, _file.Id, false), Ct)).Status.ShouldBe(ResultStatus.NotFound);

        var second = (await new CreateShareLinkHandler(_f.Store, _f.Access, _f.Caller)
            .Handle(new CreateShareLinkCommand(_file.Id, Now.AddDays(1), null, null), Ct)).Value;
        (await new DeleteShareLinkHandler(_f.Store, _f.Access, _f.Caller).Handle(new DeleteShareLinkCommand(second.Id), Ct)).IsSuccess.ShouldBeTrue();
        (await new OpenShareHandler(_f.Store, _f.Clock).Handle(new OpenShareQuery(second.Token, null, null), Ct)).Status.ShouldBe(ResultStatus.NotFound);
    }

    [Fact]
    public async Task ALinkToAnItemInTheRecycleBinDoesNotWork()
    {
        var link = (await new CreateShareLinkHandler(_f.Store, _f.Access, _f.Caller)
            .Handle(new CreateShareLinkCommand(_reports.Id, null, null, null), Ct)).Value;
        _reports.Delete("ana", Now);
        (await new OpenShareHandler(_f.Store, _f.Clock).Handle(new OpenShareQuery(link.Token, null, null), Ct)).Status.ShouldBe(ResultStatus.NotFound);
    }

    [Fact]
    public async Task FilesInAPublicFolderOpenWithoutSigningIn()
    {
        var open = new OpenPublicFileHandler(_f.Store, _f.Stores, _tickets, _f.Tenant, _f.Clock, _options);
        (await open.Handle(new OpenPublicFileQuery(_file.Id), Ct)).Status.ShouldBe(ResultStatus.NotFound);

        var sharing = await new SetPublicHandler(_f.Store, _f.Access, _f.Caller, Sharer(_f)).Handle(new SetPublicCommand(_reports.Id, true), Ct);
        sharing.Value.IsPublic.ShouldBeTrue();
        (await open.Handle(new OpenPublicFileQuery(_file.Id), Ct)).IsSuccess.ShouldBeTrue();
        (await open.Handle(new OpenPublicFileQuery(_reports.Id), Ct)).Status.ShouldBe(ResultStatus.NotFound); // folders are not served
    }
}

public class ShareLinkRules
{
    [Fact]
    public void PasswordsAreHashedAndChecked()
    {
        var link = ShareLink.Create(Guid.NewGuid(), "ana", null, "correct horse", null, Now);
        link.PasswordHash.ShouldNotContain("correct horse");
        link.Accepts("correct horse").ShouldBeTrue();
        link.Accepts("Correct horse").ShouldBeFalse();
        link.Accepts(null).ShouldBeFalse();
        ShareLink.Create(Guid.NewGuid(), "ana", null, null, null, Now).Accepts(null).ShouldBeTrue();
    }

    [Fact]
    public void ExpiryAndLimitEndALink()
    {
        var link = ShareLink.Create(Guid.NewGuid(), "ana", Now.AddHours(1), null, 2, Now);
        link.IsActive(Now).ShouldBeTrue();
        link.IsActive(Now.AddHours(2)).ShouldBeFalse();
        link.CountDownload();
        link.CountDownload();
        link.IsActive(Now).ShouldBeFalse();
        Should.Throw<DomainException>(() => ShareLink.Create(Guid.NewGuid(), "ana", Now.AddMinutes(-1), null, null, Now));
        Should.Throw<DomainException>(() => ShareLink.Create(Guid.NewGuid(), "ana", null, null, 0, Now));
    }

    [Fact]
    public void TokensAreLongRandomAndUrlSafe()
    {
        var a = ShareLink.Create(Guid.NewGuid(), "ana", null, null, null, Now).Token;
        var b = ShareLink.Create(Guid.NewGuid(), "ana", null, null, null, Now).Token;
        a.ShouldNotBe(b);
        a.Length.ShouldBe(22);
        a.ShouldMatch("^[A-Za-z0-9_-]+$");
    }

    [Fact]
    public void APersonIsWrittenAgainOnlyWhenSomethingChanged()
    {
        var person = StoragePerson.Create("bob", "Bob", "bob@example.test", Now);
        person.Seen("Bob", "bob@example.test", Now.AddHours(1)).ShouldBeFalse();
        person.Seen("Robert", "bob@example.test", Now.AddHours(1)).ShouldBeTrue();
        person.SearchKey.ShouldBe("robert bob@example.test");
        person.Seen("Robert", "bob@example.test", Now.AddDays(2)).ShouldBeTrue();
    }
}
