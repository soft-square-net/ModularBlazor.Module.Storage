using soft.square.net.WebApi.Modules.Storage.Core.PersonAggregate;
using soft.square.net.WebApi.Modules.Storage.UseCases.Access;
using soft.square.net.WebApi.Modules.Storage.UseCases.Items;

namespace soft.square.net.WebApi.Modules.Storage.UseCases.Sharing;

/// <summary>The caller's name and email as their token gives them (implemented in Web from the request's claims).</summary>
public interface ICallerProfile
{
    string? Name { get; }
    string? Email { get; }
}

/// <summary>Someone the Share dialog can pick: a tenant member who has used Storage.</summary>
public sealed record PersonDto(string UserId, string Name, string? Email)
{
    public static PersonDto From(StoragePerson p) => new(p.UserId, p.Name, p.Email);

    /// <summary>A user Storage has not seen (shown by id until they open Files).</summary>
    public static PersonDto Unknown(string userId) => new(userId, userId, null);
}

/// <summary>Keeps Storage's list of people: the caller is added, or refreshed, when they open Files.</summary>
public sealed class PeopleBook(IStorageStore store, StorageCaller caller, ICallerProfile profile)
{
    /// <summary>Adds or refreshes the caller; true when something was staged (the caller saves).</summary>
    public async Task<bool> RememberCallerAsync(CancellationToken ct)
    {
        var me = caller.UserId;
        var person = await store.PersonAsync(me, ct);
        if (person is not null) return person.Seen(profile.Name, profile.Email, caller.Now);
        store.Add(StoragePerson.Create(me, profile.Name, profile.Email, caller.Now));
        return true;
    }

    /// <summary>The people for these ids, in the same order; unknown ones by id.</summary>
    public async Task<IReadOnlyDictionary<string, PersonDto>> ResolveAsync(IReadOnlyCollection<string> userIds, CancellationToken ct)
    {
        var known = (await store.PeopleAsync(userIds, ct)).ToDictionary(p => p.UserId, PersonDto.From, StringComparer.Ordinal);
        return userIds.Distinct(StringComparer.Ordinal).ToDictionary(id => id, id => known.GetValueOrDefault(id) ?? PersonDto.Unknown(id), StringComparer.Ordinal);
    }
}

/// <summary>The Share dialog's people picker: members whose name or email contains the text (at least 2 characters).</summary>
public sealed record SearchPeopleQuery(string? Search) : IQuery<Result<IReadOnlyList<PersonDto>>>;

public sealed class SearchPeopleHandler(IStorageStore store) : IQueryHandler<SearchPeopleQuery, Result<IReadOnlyList<PersonDto>>>
{
    public async ValueTask<Result<IReadOnlyList<PersonDto>>> Handle(SearchPeopleQuery query, CancellationToken ct)
    {
        var text = (query.Search ?? "").Trim().ToLowerInvariant();
        if (text.Length < 2) return Result<IReadOnlyList<PersonDto>>.Success([]);
        if (text.Length > 100) text = text[..100];
        return (await store.SearchPeopleAsync(text, 20, ct)).Select(PersonDto.From).ToArray();
    }
}
