namespace soft.square.net.WebApi.Modules.Storage.UseCases;

/// <summary>
/// The Storage module's platform permissions. They only open the door: what someone may do with a folder or file is
/// decided by its access control list (NTFS-style, see <c>AccessEvaluator</c>). <see cref="Admin"/> bypasses the lists.
/// </summary>
public static class StoragePermissions
{
    /// <summary>Use Storage at all: the home folder and whatever the access lists allow.</summary>
    public const string Access = "storage.access";

    /// <summary>Full control of every folder and file of the tenant, groups, and (later) connections, quotas and the recycle bin.</summary>
    public const string Admin = "storage.admin";

    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        new(Access, "Use Storage: your home folder, and the folders and files their access lists open to you."),
        new(Admin, "Administer Storage: full control of every folder and file, regardless of their access lists, and Storage groups.")
    ];
}
