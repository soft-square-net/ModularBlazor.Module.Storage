namespace soft.square.net.WebApi.Modules.Storage.Core.PersonAggregate;

/// <summary>
/// A tenant member who has used Storage, as their token named them: the people the Share dialog can find. Storage keeps
/// its own list because modules cannot list the identity service's users; someone who never opened Files is not in it
/// (Everyone, permission and group entries still reach them).
/// </summary>
public sealed class StoragePerson : AggregateRoot<Guid>, ITenantOwned
{
    public const int UserIdLength = 128;
    public const int NameLength = 200;
    public const int EmailLength = 256;

    /// <summary>How long a visit keeps the entry fresh before the next one writes again.</summary>
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(1);

    private StoragePerson() { } // EF Core

    public string TenantId { get; set; } = "";
    public string UserId { get; private set; } = "";
    public string Name { get; private set; } = "";
    /// <summary>Lower-case name and email, what the search compares.</summary>
    public string SearchKey { get; private set; } = "";
    public string? Email { get; private set; }
    public DateTimeOffset LastSeenAt { get; private set; }

    public static StoragePerson Create(string userId, string? name, string? email, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(userId) || userId.Length > UserIdLength) throw new DomainException("A user id is required.");
        var person = new StoragePerson { Id = Guid.CreateVersion7(now), UserId = userId };
        person.Seen(name, email, now);
        return person;
    }

    /// <summary>Records a visit; false when nothing needed writing (same name and email, seen recently).</summary>
    public bool Seen(string? name, string? email, DateTimeOffset now)
    {
        name = Clip(string.IsNullOrWhiteSpace(name) ? email ?? UserId : name.Trim(), NameLength)!;
        email = Clip(string.IsNullOrWhiteSpace(email) ? null : email.Trim(), EmailLength);
        if (name == Name && email == Email && now - LastSeenAt < RefreshAfter) return false;
        Name = name;
        Email = email;
        SearchKey = Clip($"{name} {email}".ToLowerInvariant(), NameLength + EmailLength + 1)!;
        LastSeenAt = now;
        return true;
    }

    private static string? Clip(string? text, int length) => text is { } t && t.Length > length ? t[..length] : text;
}
