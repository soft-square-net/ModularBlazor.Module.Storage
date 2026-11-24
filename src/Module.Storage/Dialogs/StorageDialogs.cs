namespace soft.square.net.Module.Storage.Dialogs;

/// <summary>
/// The dialogs this module provides to every module (IModuleDialogs), and what they answer. Other modules do not
/// reference this assembly: the id and the JSON shapes are the contract. Callers keep the ids they get (and, if they
/// like, the names as a snapshot): a file keeps its id through renames, moves and new versions.
/// </summary>
public static class StorageDialogs
{
    /// <summary>
    /// Choose files. Parameters <c>{ multiple?: bool, accept?: ["image/*", "application/pdf", ".docx"], folderId?: id,
    /// publicOnly?: bool }</c>: <c>accept</c> limits the files that can be chosen (content types, "type/*" or
    /// extensions); <c>folderId</c> opens that folder first (else My files); <c>publicOnly</c> offers only files in
    /// public folders (for published pages: they have a stable public address).
    /// Result <c>{ ids: [...], files: [{ id, name, size, contentType, version, folderId, publicUrl }] }</c>, ids as
    /// strings, in the order chosen; <c>publicUrl</c> is the file's public address when it is in a public folder, else null.
    /// Files that are not virus-checked yet (or infected) cannot be chosen.
    /// </summary>
    public const string PickFiles = "Module.Storage/pick-files";

    /// <summary>
    /// Choose a folder. Parameters <c>{ folderId?: id, rights?: "create" | "read" }</c>: <c>rights</c> is what the user
    /// must be allowed in it ("create": add files, the default; "read": list it). Result <c>{ id, name, path: [{ id,
    /// name }], publicFolder: bool }</c>, path from the top folder down to the chosen folder.
    /// </summary>
    public const string PickFolder = "Module.Storage/pick-folder";
}
