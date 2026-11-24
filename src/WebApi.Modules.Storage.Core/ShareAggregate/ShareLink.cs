using System.Security.Cryptography;

namespace soft.square.net.WebApi.Modules.Storage.Core.ShareAggregate;

/// <summary>
/// "Anyone with the link": a revocable token that lets someone who is not signed in open one file, or browse and
/// download one folder and everything below it. Optionally it expires, needs a password (kept as a PBKDF2 hash) or
/// stops after a number of downloads. The link is the authorization: the item's access list is not consulted.
/// </summary>
public sealed class ShareLink : AggregateRoot<Guid>, ITenantOwned
{
    public const int TokenLength = 32;
    public const int PasswordMaxLength = 128;
    public const int MaxDownloadsLimit = 100_000;

    private ShareLink() { } // EF Core

    public string TenantId { get; set; } = "";
    /// <summary>128 random bits, base64url: the part of the URL that opens the link.</summary>
    public string Token { get; private set; } = "";
    public Guid ItemId { get; private set; }
    public string CreatedBy { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }
    public string? PasswordHash { get; private set; }
    public int? MaxDownloads { get; private set; }
    public int Downloads { get; private set; }
    public int Version { get; private set; }

    public bool HasPassword => PasswordHash is not null;

    public static ShareLink Create(Guid itemId, string by, DateTimeOffset? expiresAt, string? password, int? maxDownloads, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(by)) throw new DomainException("A user id is required.");
        if (expiresAt is { } end && end <= now) throw new DomainException("The expiry date must be in the future.");
        if (maxDownloads is < 1 or > MaxDownloadsLimit) throw new DomainException($"The download limit must be between 1 and {MaxDownloadsLimit}.");
        if (password is { Length: > PasswordMaxLength }) throw new DomainException($"A password can have at most {PasswordMaxLength} characters.");
        return new ShareLink
        {
            Id = Guid.CreateVersion7(now),
            Token = NewToken(),
            ItemId = itemId,
            CreatedBy = by,
            CreatedAt = now,
            ExpiresAt = expiresAt,
            PasswordHash = string.IsNullOrEmpty(password) ? null : SharePasswords.Hash(password),
            MaxDownloads = maxDownloads,
        };
    }

    /// <summary>Not expired and not used up.</summary>
    public bool IsActive(DateTimeOffset now) =>
        (ExpiresAt is null || ExpiresAt > now) && (MaxDownloads is null || Downloads < MaxDownloads);

    /// <summary>The password matches (or the link has none).</summary>
    public bool Accepts(string? password) =>
        PasswordHash is null || !string.IsNullOrEmpty(password) && SharePasswords.Verify(password, PasswordHash);

    public void CountDownload()
    {
        Downloads++;
        Version++;
    }

    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>PBKDF2-SHA256 password hashes: "v1.{iterations}.{salt}.{hash}" (base64), compared in fixed time.</summary>
public static class SharePasswords
{
    private const int Iterations = 100_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"v1.{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('.');
        if (parts.Length != 4 || parts[0] != "v1" || !int.TryParse(parts[1], out var iterations)) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }
}
