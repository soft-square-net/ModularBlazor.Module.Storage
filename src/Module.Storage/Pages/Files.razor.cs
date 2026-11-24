using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;
using soft.square.net.ModularBlazor.Abstractions;
using soft.square.net.ModularBlazor.Abstractions.MudTail;
using soft.square.net.WebApi.Modules.Storage.Client;

namespace soft.square.net.Module.Storage.Pages;

/// <summary>
/// The bridge between MudTail's explorer and the Storage API. The explorer raises OnMudEvent("request",
/// { requestId, kind, ... }) for everything it needs; each is answered with handle.resolve(requestId, value). Commands
/// that fail are reported with a toast and answered <c>false</c> (no refresh). Rights become deny lists: an item's
/// refuses commands on it, the open folder's refuses what would change the folder.
/// </summary>
public partial class Files : ComponentBase, IAsyncDisposable
{
    private const string PinnedKey = "storage.explorer.pinned";
    /// <summary>The recycle bin's place id (not a folder: the API's GET /recycle-bin).</summary>
    private const string BinId = "recycle-bin";
    /// <summary>"Shared with me": items others shared with the caller by name (GET /shared-with-me).</summary>
    private const string SharedId = "shared-with-me";
    /// <summary>The bin's own commands (hidden in the live tree), and the commands that make no sense in the bin.</summary>
    private static readonly string[] BinOnly = ["restore", "emptyBin"];
    private static readonly string[] NotInBin = ["open", "download", "rename", "cut", "copy", "moveTo", "copyTo", "share", "edit", "security", "versions", "pin", "openTab"];

    [Inject] private IHttpClientFactory HttpClients { get; set; } = default!;
    [Inject] private MudTailRuntime MudTail { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;
    [Inject] private IStringLocalizerFactory Texts { get; set; } = default!;

    /// <summary>The folder to open first (else the user's home folder).</summary>
    [SupplyParameterFromQuery(Name = "folder")] public Guid? StartFolder { get; set; }

    private IStringLocalizer? _l;
    private IStringLocalizer L => _l ??= Texts.Create(GetType());

    private ElementReference _host;
    private IJSObjectReference? _explorer;
    // The explorer asks its first questions (places, the start folder) while create() runs, before the handle is here.
    private readonly TaskCompletionSource<IJSObjectReference> _created = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DotNetObjectReference<Files>? _self;
    private Roots? _roots;
    private bool _loading = true;
    private string? _error;
    private bool _disposed;

    /// <summary>Storage routes (/api/storage/...) reach the pool that runs the module through the gateway.</summary>
    private StorageClient Api => new(HttpClients.CreateClient(HttpClientNames.WebApi));

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_editorPending)
        {
            _editorPending = false;
            await CreateEditorAsync();
        }
        if (!firstRender) return;
        try
        {
            var runtime = await MudTail.GetAsync();
            if (runtime is null)
            {
                _error = L["The explorer needs the MudTail runtime (UI.MudTail), which is not active."];
                return;
            }

            _roots = await Api.GetRootsAsync();
            _self = DotNetObjectReference.Create(this);
            var options = new
            {
                start = (StartFolder ?? _roots.Home.Id).ToString(),
                pinned = await PinnedAsync(),
                view = "details",
                preview = true,
                renameInPlace = true,
                // Shown where the deny lists allow them (MudTail hides an entry that an item's or the folder's list names).
                menuItems = new object[]
                {
                    new { id = "restore", label = L["Restore"].Value, icon = "undo" },
                    new { id = "versions", label = L["Previous versions"].Value, icon = "clock", single = true },
                    new { id = "emptyBin", label = L["Empty recycle bin"].Value, icon = "delete" },
                },
            };
            _explorer = await runtime.InvokeAsync<IJSObjectReference>("create", "explorer", _host, options, _self);
            _created.TrySetResult(_explorer);
        }
        catch (AccessTokenNotAvailableException ex)
        {
            ex.Redirect();
        }
        catch (Exception ex)
        {
            _error = Describe(ex);
        }
        finally
        {
            _created.TrySetCanceled();
            _loading = false;
            StateHasChanged();
        }
    }

    /// <summary>Events of the explorer: questions ("request") and what the user did (navigate, pinned, ...).</summary>
    [JSInvokable]
    public Task OnMudEvent(string name, JsonElement payload)
    {
        switch (name)
        {
            case "request":
                // Answered on its own: the explorer may ask again (another folder) before this one is answered.
                _ = AnswerAsync(payload.GetProperty("requestId").GetInt32(), payload.GetProperty("kind").GetString() ?? "", payload.Clone());
                break;
            case "pinned":
                _ = SavePinnedAsync(payload.GetRawText());
                break;
            case "change" or "error":
                EditorEvent(name, payload.Clone()); // the image editor's
                break;
        }
        return Task.CompletedTask;
    }

    private async Task AnswerAsync(int requestId, string kind, JsonElement request)
    {
        object? value;
        try
        {
            value = kind switch
            {
                "places" => Places(),
                "list" => await ListAsync(request),
                "children" => await ChildrenAsync(request),
                "rename" => await RenameAsync(request),
                "preview" => await PreviewAsync(request),
                "command" => await CommandAsync(request),
                _ => null,
            };
        }
        catch (AccessTokenNotAvailableException ex)
        {
            ex.Redirect();
            return;
        }
        catch (Exception ex)
        {
            var message = Describe(ex);
            // A folder that cannot be listed (no rights, gone): the explorer shows the message in its own panel.
            if (kind is "list" or "children")
            {
                await CallAsync("reject", requestId, message);
                return;
            }
            if (kind == "command") await MudTail.ToastAsync(message, "error");
            await CallAsync("resolve", requestId, kind == "command" ? false : null);
            return;
        }
        await CallAsync("resolve", requestId, value);
    }

    // ───────── questions ─────────

    private object[] Places() =>
    [
        new { id = _roots!.Home.Id.ToString(), name = L["My files"].Value, hasChildren = _roots.Home.HasSubfolders, icon = "home" },
        new { id = _roots.Files.Id.ToString(), name = L["Files"].Value, hasChildren = true, icon = "drive" },
        new { id = SharedId, name = L["Shared with me"].Value, hasChildren = false, icon = "share" },
        new { id = BinId, name = L["Recycle bin"].Value, hasChildren = false, icon = "delete" },
    ];

    private async Task<object> ListAsync(JsonElement request)
    {
        if (Text(request, "folderId") == BinId) return await BinAsync(Text(request, "search")?.Trim());
        if (Text(request, "folderId") == SharedId) return await SharedWithMeAsync(Text(request, "search")?.Trim());
        var id = Id(request, "folderId") ?? throw new InvalidOperationException(L["This place has no files."]);
        var view = await Api.ListFolderAsync(id);
        var search = Text(request, "search")?.Trim();
        var items = string.IsNullOrEmpty(search)
            ? view.Items
            : view.Items.Where(i => i.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)).ToList();
        return new
        {
            folder = ToFolder(view.Folder),
            path = view.Path.Select(c => new { id = c.Id.ToString(), name = CrumbName(c) }),
            items = items.Select(i => ToItem(i, view.Folder.Rights)),
        };
    }

    /// <summary>The recycle bin as a folder: what the caller deleted, "Date modified" being when it was deleted.</summary>
    private async Task<object> BinAsync(string? search)
    {
        var bin = await Api.GetRecycleBinAsync();
        var name = L["Recycle bin"].Value;
        return new
        {
            folder = new { id = BinId, name, kind = "folder", @virtual = true, deny = new[] { "newFolder", "upload", "paste", "moveTo", "copyTo" } },
            path = new[] { new { id = BinId, name } },
            items = bin.Where(d => string.IsNullOrEmpty(search) || d.Item.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase))
                .Select(d => new
                {
                    id = d.Item.Id.ToString(),
                    name = d.Item.Name,
                    kind = d.Item.Kind == ItemKind.Folder ? "folder" : "file",
                    modified = d.DeletedAt,
                    created = d.Item.CreatedAt,
                    size = d.Item.Kind == ItemKind.File ? d.Item.Size : (long?)null,
                    contentType = d.Item.ContentType,
                    origin = string.Join(" › ", d.OriginalPath.Select(CrumbName)),
                    deny = NotInBin,
                }),
        };
    }

    /// <summary>"Shared with me" as a folder: what others shared with the caller; each item keeps its own rights.</summary>
    private async Task<object> SharedWithMeAsync(string? search)
    {
        var shared = await Api.GetSharedWithMeAsync();
        var name = L["Shared with me"].Value;
        return new
        {
            folder = new { id = SharedId, name, kind = "folder", @virtual = true, deny = new[] { "newFolder", "upload", "paste", "moveTo", "copyTo", "restore", "emptyBin" } },
            path = new[] { new { id = SharedId, name } },
            items = shared.Where(s => string.IsNullOrEmpty(search) || s.Item.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase))
                .Select(s => ToItem(s.Item, 0)),
        };
    }

    private async Task<object> ChildrenAsync(JsonElement request)
    {
        var id = Id(request, "folderId");
        if (id is null) return Array.Empty<object>();
        var view = await Api.ListFolderAsync(id.Value);
        return view.Items.Where(i => i.Kind == ItemKind.Folder)
            .Select(i => new { id = i.Id.ToString(), name = i.Name, hasChildren = i.HasSubfolders });
    }

    /// <summary>In-place rename: true, or the reason the explorer shows under the name.</summary>
    private async Task<object> RenameAsync(JsonElement request)
    {
        var item = Picked.From(request.GetProperty("item"));
        var name = Text(request, "name")?.Trim() ?? "";
        if (item is null || name.Length == 0) return L["Type a name."].Value;
        try
        {
            await Api.RenameItemAsync(item.Id, new RenameRequest { Name = name, Version = item.Version });
            return true;
        }
        catch (StorageApiException ex)
        {
            return Describe(ex);
        }
    }

    /// <summary>The details pane's preview: a short-lived inline link, for images only.</summary>
    private async Task<string?> PreviewAsync(JsonElement request)
    {
        var item = Picked.From(request.GetProperty("item"));
        if (item is null || item.IsFolder || item.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true) return null;
        return await LinkAsync(item.Id, inline: true);
    }

    // ───────── commands ─────────

    private async Task<object?> CommandAsync(JsonElement request)
    {
        var command = Text(request, "command") ?? "";
        var items = request.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(Picked.From).OfType<Picked>().ToList()
            : [];
        var folder = request.TryGetProperty("folder", out var f) ? Picked.From(f) : null;
        var inBin = request.TryGetProperty("folder", out var bf) && Text(bf, "id") == BinId;
        var permanent = request.TryGetProperty("permanent", out var pf) && pf.ValueKind == JsonValueKind.True;
        var target = request.TryGetProperty("target", out var t) ? Picked.From(t) : null;

        switch (command)
        {
            case "newFolder":
            {
                if (folder is null) return false;
                var created = await Api.CreateFolderAsync(new CreateFolderCommand
                {
                    ParentId = folder.Id, Name = Text(request, "name") ?? L["New folder"], NumberIfTaken = true,
                });
                return new { refresh = true, select = new[] { created.Id.ToString() } };
            }
            case "paste":
                if (folder is null) return false;
                return await TransferAsync(items, folder.Id, copy: Text(request, "mode") != "cut");
            case "moveTo":
            case "copyTo":
                if (target is null) return false;
                return await TransferAsync(items, target.Id, copy: command == "copyTo");
            case "delete":
                return inBin || permanent ? await DeletePermanentlyAsync(items, inBin) : await DeleteAsync(items);
            case "restore":
                return await RestoreAsync(items);
            case "emptyBin":
                return await EmptyBinAsync(inBin);
            case "versions":
                if (items is [{ IsFolder: false } file]) await ShowVersionsAsync(file);
                return null;
            case "upload":
                if (folder is null || !request.TryGetProperty("files", out var files)) return false;
                return await UploadAsync(folder.Id, files);
            case "open":
            case "download":
                await OpenFilesAsync(items, inline: command == "open");
                return null;
            case "properties":
                await CallAsync("toggleDetails", true);
                return null;
            case "share":
                if (items is [var one]) await ShowShareAsync(one);
                return null;
            case "security":
                if (items is [var only]) await ShowSecurityAsync(only);
                else if (items.Count == 0 && folder is not null) await ShowSecurityAsync(folder);
                return null;
            case "edit":
                if (items is [{ IsFolder: false } image]) await OpenEditorAsync(image);
                return null;
            default:
                await MudTail.ToastAsync(L["This command is not available yet."], "info");
                return null;
        }
    }

    private async Task<object> TransferAsync(List<Picked> items, Guid targetId, bool copy)
    {
        if (items.Count == 0) return false;
        var ids = items.Select(i => i.Id).ToList();
        var result = copy
            ? await Api.CopyItemsAsync(new CopyItemsCommand { Ids = ids, TargetFolderId = targetId })
            : await Api.MoveItemsAsync(new MoveItemsCommand { Ids = ids, TargetFolderId = targetId });
        await ReportFailuresAsync(result, items);
        if (items.Any(i => i.IsFolder)) await CallAsync("refreshTree", targetId.ToString());
        return new { refresh = true, select = copy ? result.Done.Select(d => d.Id.ToString()).ToArray() : null };
    }

    private async Task<object> DeleteAsync(List<Picked> items)
    {
        if (items.Count == 0) return false;
        var result = await Api.DeleteItemsAsync(new DeleteItemsCommand { Ids = items.Select(i => i.Id).ToList() });
        await ReportFailuresAsync(result, items);
        return new { refresh = true };
    }

    /// <summary>Shift+Delete, or Delete in the recycle bin: asks first, as Windows does.</summary>
    private async Task<object> DeletePermanentlyAsync(List<Picked> items, bool inBin)
    {
        if (items.Count == 0) return false;
        var question = items.Count == 1
            ? L["Are you sure you want to permanently delete \"{0}\"?", items[0].Name].Value
            : L["Are you sure you want to permanently delete these {0} items?", items.Count].Value;
        if (!await ConfirmAsync(question, L["Delete"].Value)) return false;
        var ids = items.Select(i => i.Id).ToList();
        var result = inBin
            ? await Api.PurgeItemsAsync(new PurgeItemsCommand { Ids = ids })
            : await Api.DeleteItemsAsync(new DeleteItemsCommand { Ids = ids, Permanent = true });
        await ReportFailuresAsync(result, items);
        return new { refresh = true };
    }

    private async Task<object> RestoreAsync(List<Picked> items)
    {
        if (items.Count == 0) return false;
        var result = await Api.RestoreItemsAsync(new RestoreItemsCommand { Ids = items.Select(i => i.Id).ToList() });
        await ReportFailuresAsync(result, items);
        if (result.Done.Count > 0)
        {
            await MudTail.ToastAsync(result.Done.Count == 1
                ? L["\"{0}\" was restored to where it was.", result.Done[0].Name].Value
                : L["{0} items were restored to where they were.", result.Done.Count].Value, "success");
            if (result.Done.Any(d => d.Kind == ItemKind.Folder))
                foreach (var place in new[] { _roots!.Home.Id, _roots.Files.Id }) await CallAsync("refreshTree", place.ToString());
        }
        return new { refresh = true };
    }

    private async Task<object> EmptyBinAsync(bool inBin)
    {
        if (!await ConfirmAsync(L["Are you sure you want to permanently delete everything in the recycle bin?"].Value, L["Empty recycle bin"].Value))
            return false;
        var result = await Api.PurgeItemsAsync(new PurgeItemsCommand { Ids = [] });
        await ReportFailuresAsync(result, []);
        return new { refresh = inBin };
    }

    private async Task ReportFailuresAsync(BatchResult result, List<Picked> items)
    {
        foreach (var failure in result.Failed)
        {
            var name = items.FirstOrDefault(i => i.Id == failure.Id)?.Name ?? failure.Id.ToString();
            await MudTail.ToastAsync($"{name}: {failure.Reason}", "error");
        }
    }

    /// <summary>Opens (inline) or downloads each selected file in a new window, with a short-lived link.</summary>
    private async Task OpenFilesAsync(List<Picked> items, bool inline)
    {
        var files = items.Where(i => !i.IsFolder).ToList();
        if (files.Count < items.Count) await MudTail.ToastAsync(L["Folders cannot be downloaded yet: select the files in them."], "info");
        foreach (var file in files)
            await Js.InvokeVoidAsync("open", await LinkAsync(file.Id, inline), "_blank", "noopener");
    }

    private async Task<string> LinkAsync(Guid id, bool inline)
    {
        var link = await Api.GetDownloadLinkAsync(id, inline: inline);
        if (link.Url.IsAbsoluteUri) return link.Url.ToString();
        var gateway = HttpClients.CreateClient(HttpClientNames.WebApi).BaseAddress;
        return gateway is null ? link.Url.ToString() : new Uri(gateway, link.Url).ToString();
    }

    // ───────── dialogs: confirm, previous versions ─────────

    private sealed record Confirmation(string Question, string Action, TaskCompletionSource<bool> Answer);
    private Confirmation? _confirm;

    /// <summary>Asks a yes/no question in a modal; true when the user picks <paramref name="action"/>.</summary>
    private Task<bool> ConfirmAsync(string question, string action)
    {
        _confirm?.Answer.TrySetResult(false);
        _confirm = new Confirmation(question, action, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        StateHasChanged();
        return _confirm.Answer.Task;
    }

    private void Answer(bool yes)
    {
        var confirm = _confirm;
        _confirm = null;
        confirm?.Answer.TrySetResult(yes);
    }

    private Picked? _versionsOf;
    private IReadOnlyList<VersionDto> _versions = [];
    private bool _versionsBusy;

    /// <summary>Opens "Previous versions" for a file: every kept version, newest first; open, restore or delete one.</summary>
    private async Task ShowVersionsAsync(Picked file)
    {
        _versionsOf = file;
        _versions = [];
        StateHasChanged();
        await VersionsDoAsync(() => Api.GetItemAsync(file.Id));
    }

    private Task RestoreVersionAsync(int number) => VersionsDoAsync(() => Api.RestoreVersionAsync(_versionsOf!.Id, number), refresh: true);

    private async Task DeleteVersionAsync(VersionDto version)
    {
        if (!await ConfirmAsync(L["Are you sure you want to permanently delete version {0}?", version.Number].Value, L["Delete"].Value)) return;
        await VersionsDoAsync(() => Api.DeleteVersionAsync(_versionsOf!.Id, version.Number), refresh: true);
    }

    private async Task OpenVersionAsync(int number)
    {
        try
        {
            var link = await Api.GetDownloadLinkAsync(_versionsOf!.Id, number, inline: true);
            var url = link.Url.IsAbsoluteUri || HttpClients.CreateClient(HttpClientNames.WebApi).BaseAddress is not { } gateway
                ? link.Url.ToString() : new Uri(gateway, link.Url).ToString();
            await Js.InvokeVoidAsync("open", url, "_blank", "noopener");
        }
        catch (Exception ex) when (ex is StorageApiException or HttpRequestException)
        {
            await MudTail.ToastAsync(Describe(ex), "error");
        }
    }

    private async Task VersionsDoAsync(Func<Task<ItemDetail>> call, bool refresh = false)
    {
        _versionsBusy = true;
        StateHasChanged();
        try
        {
            var detail = await call();
            _versions = detail.Versions.OrderByDescending(v => v.Number).ToList();
            if (refresh) await CallAsync("refresh");
        }
        catch (Exception ex) when (ex is StorageApiException or HttpRequestException)
        {
            await MudTail.ToastAsync(Describe(ex), "error");
        }
        finally
        {
            _versionsBusy = false;
            StateHasChanged();
        }
    }

    private void CloseVersions() => _versionsOf = null;

    /// <summary>"12.3 KB", as the explorer shows sizes (1024-based).</summary>
    private string SizeText(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes} {L["bytes"]}" : $"{value:0.#} {units[unit]}";
    }

    // ───────── uploads ─────────

    /// <summary>
    /// Uploads the files the user dropped or picked, chunk by chunk (each chunk read from the browser), into the open
    /// folder; a picked folder keeps its structure. A name that is taken gets a number ("a (2).txt"), as Windows does
    /// when it keeps both.
    /// </summary>
    private async Task<object> UploadAsync(Guid folderId, JsonElement files)
    {
        var token = files.GetProperty("token").GetString()!;
        var list = files.GetProperty("files").EnumerateArray().Select(f => new
        {
            Index = f.GetProperty("index").GetInt32(),
            Name = f.GetProperty("name").GetString() ?? "file",
            Size = f.GetProperty("size").GetInt64(),
            Type = f.TryGetProperty("type", out var type) ? type.GetString() : null,
            Path = f.TryGetProperty("path", out var path) ? path.GetString() : null,
        }).ToList();

        var total = Math.Max(1, list.Sum(f => f.Size));
        long sent = 0;
        var done = new List<string>();
        var folders = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        try
        {
            for (var n = 0; n < list.Count; n++)
            {
                var file = list[n];
                Guid? uploadId = null;
                try
                {
                    var parent = await FolderOfAsync(folderId, file.Path, folders, done);
                    var upload = await Api.StartUploadAsync(new StartUploadCommand
                    {
                        FolderId = parent, Name = file.Name, Size = file.Size,
                        ContentType = string.IsNullOrEmpty(file.Type) ? null : file.Type, Conflict = NameConflict.KeepBoth,
                    });
                    uploadId = upload.Id;
                    for (long offset = 0; offset < file.Size; offset += upload.ChunkSize)
                    {
                        var length = (int)Math.Min(upload.ChunkSize, file.Size - offset);
                        var bytes = await _explorer!.InvokeAsync<byte[]>("readFile", token, file.Index, offset, length);
                        using var chunk = new MemoryStream(bytes, writable: false);
                        await Api.WriteChunkAsync(upload.Id, offset, new FileParameter(chunk, file.Name, "application/octet-stream"));
                        sent += bytes.Length;
                        await ProgressAsync(L["Uploading {0} ({1} of {2})", file.Name, n + 1, list.Count], sent * 100 / total);
                    }
                    var item = await Api.CompleteUploadAsync(upload.Id);
                    uploadId = null;
                    if (parent == folderId) done.Add(item.Id.ToString());
                    await ProgressAsync(L["Uploading {0} ({1} of {2})", file.Name, n + 1, list.Count], sent * 100 / total);
                }
                catch (Exception ex) when (ex is not AccessTokenNotAvailableException)
                {
                    await MudTail.ToastAsync($"{file.Name}: {Describe(ex)}", "error");
                    if (uploadId is { } abandoned)
                        try { await Api.CancelUploadAsync(abandoned); } catch (Exception) { /* it expires by itself */ }
                    sent += file.Size;
                }
            }
        }
        finally
        {
            await CallAsync("releaseFiles", token);
            await ProgressAsync(null, 0);
        }

        if (folders.Count > 0) await CallAsync("refreshTree", folderId.ToString());
        return new { refresh = true, select = done.ToArray() };
    }

    /// <summary>The folder a file of a picked folder goes to ("photos/2026/a.jpg" → photos/2026), made when missing.</summary>
    private async Task<Guid> FolderOfAsync(Guid root, string? path, Dictionary<string, Guid> made, List<string> selectInRoot)
    {
        var parts = (path ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var parent = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var key = string.Join('/', parts.Take(i + 1));
            if (!made.TryGetValue(key, out var id))
            {
                var existing = (await Api.ListFolderAsync(parent)).Items
                    .FirstOrDefault(x => x.Kind == ItemKind.Folder && string.Equals(x.Name, parts[i], StringComparison.OrdinalIgnoreCase));
                id = existing?.Id ?? (await Api.CreateFolderAsync(new CreateFolderCommand { ParentId = parent, Name = parts[i] })).Id;
                made[key] = id;
                if (parent == root) selectInRoot.Add(id.ToString());
            }
            parent = id;
        }
        return parent;
    }

    private Task ProgressAsync(string? label, long value) =>
        CallAsync("setProgress", label is null ? null : new { label, value = (int)Math.Clamp(value, 0, 100) });

    // ───────── items ─────────

    private object ToFolder(ItemDto folder) => new
    {
        id = folder.Id.ToString(),
        name = NameOf(folder),
        kind = "folder",
        deny = FolderDeny(folder.Rights),
    };

    private object ToItem(ItemDto item, int folderRights) => new
    {
        id = item.Id.ToString(),
        name = NameOf(item),
        kind = item.Kind == ItemKind.Folder ? "folder" : "file",
        modified = item.ModifiedAt,
        created = item.CreatedAt,
        size = item.Kind == ItemKind.File ? item.Size : (long?)null,
        contentType = item.ContentType,
        hasChildren = item.HasSubfolders,
        version = item.Version,
        thumbnail = item.ThumbnailUrl?.ToString(),
        properties = item.Scan == ScanStatus.Clean ? null : new[] { new { label = L["Virus check"].Value, value = ScanText(item.Scan) } },
        deny = ItemDeny(item, folderRights),
    };

    private string ScanText(ScanStatus scan) => scan == ScanStatus.Infected
        ? L["Infected: quarantined"].Value
        : L["Waiting for the virus check"].Value;

    private string NameOf(ItemDto item) => DisplayName(item.Id, item.Role, item.Name);

    private string CrumbName(Crumb crumb) => DisplayName(crumb.Id, crumb.Role, crumb.Name);

    /// <summary>The top folders in the user's language; the user's own home folder (named after the user id) is "My files".</summary>
    private string DisplayName(Guid id, FolderRole role, string name) => role switch
    {
        FolderRole.Root => L["Files"].Value,
        FolderRole.Homes => L["Users"].Value,
        FolderRole.Home when id == _roots?.Home.Id => L["My files"].Value,
        _ => name,
    };

    /// <summary>What the rights refuse on an item. Renaming, moving and deleting need Delete on it or DeleteChildren
    /// on its folder; reading a file needs ListFolder (ReadData); top folders and home folders stay where they are.</summary>
    private static string[] ItemDeny(ItemDto item, int folderRights)
    {
        var deny = new List<string>();
        if (!Has(item.Rights, Rights.Share)) deny.Add("share");
        if (!Has(item.Rights, Rights.ReadPermissions)) deny.Add("security");
        // Editing saves a new version: an image the browser can write back, clean, and the right to write the file.
        var editable = item.Kind == ItemKind.File && item.Scan == ScanStatus.Clean && item.ContentType is { } type && EditableTypes.ContainsKey(type)
            && Has(item.Rights, Rights.ListFolder) && Has(item.Rights, Rights.CreateFiles);
        if (!editable) deny.Add("edit");
        deny.AddRange(BinOnly);
        if (item.Kind == ItemKind.Folder) deny.Add("versions");
        var removable = item.Role == FolderRole.Normal && (Has(item.Rights, Rights.Delete) || Has(folderRights, Rights.DeleteChildren));
        if (!removable) deny.AddRange(["rename", "delete", "cut", "moveTo"]);
        if (!Has(item.Rights, Rights.ListFolder)) deny.AddRange(["open", "download", "copy", "copyTo"]);
        if (item.Kind == ItemKind.Folder) deny.Add("download");
        // Not scanned yet, or infected: nothing hands its bytes out (the server refuses too).
        if (item.Kind == ItemKind.File && item.Scan != ScanStatus.Clean) deny.AddRange(["open", "download", "share"]); // Previous versions stays: restoring a clean one recovers the file
        return [.. deny.Distinct()];
    }

    /// <summary>What the rights refuse in the open folder.</summary>
    private static string[] FolderDeny(int rights)
    {
        var deny = new List<string>(BinOnly);
        if (!Has(rights, Rights.ReadPermissions)) deny.Add("security");
        if (!Has(rights, Rights.CreateFolders)) deny.Add("newFolder");
        if (!Has(rights, Rights.CreateFiles)) deny.Add("upload");
        if (!Has(rights, Rights.CreateFiles) && !Has(rights, Rights.CreateFolders)) deny.AddRange(["paste", "moveTo", "copyTo"]);
        return [.. deny];
    }

    private static bool Has(int rights, Rights right) => (rights & (int)right) == (int)right;

    /// <summary>StorageRights of the server (the API sends them as a number).</summary>
    [Flags]
    private enum Rights
    {
        ListFolder = 0x1,
        CreateFiles = 0x2,
        CreateFolders = 0x4,
        DeleteChildren = 0x40,
        Delete = 0x10000,
        ReadPermissions = 0x20000,
        Share = 0x1000000,
    }

    /// <summary>An item as the explorer sends it back (the fields this page gave it).</summary>
    private sealed record Picked(Guid Id, string Name, bool IsFolder, int? Version, string? ContentType)
    {
        public static Picked? From(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object || !Guid.TryParse(Text(e, "id"), out var id)) return null;
            int? version = e.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
            return new Picked(id, Text(e, "name") ?? "", Text(e, "kind") == "folder", version, Text(e, "contentType"));
        }
    }

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static Guid? Id(JsonElement e, string name) => Guid.TryParse(Text(e, name), out var id) ? id : null;

    // ───────── pinned folders (Quick access), kept in this browser ─────────

    private async Task<JsonElement?> PinnedAsync()
    {
        try
        {
            var json = await Js.InvokeAsync<string?>("localStorage.getItem", PinnedKey);
            return string.IsNullOrEmpty(json) ? null : JsonDocument.Parse(json).RootElement.Clone();
        }
        catch (Exception) { return null; }
    }

    private async Task SavePinnedAsync(string json)
    {
        try { await Js.InvokeVoidAsync("localStorage.setItem", PinnedKey, json); }
        catch (Exception) { /* private window: Quick access lasts for this visit */ }
    }

    // ───────── plumbing ─────────

    private async Task CallAsync(string method, params object?[] args)
    {
        if (_disposed) return;
        try
        {
            var explorer = await _created.Task;
            if (!_disposed) await explorer.InvokeVoidAsync(method, args);
        }
        catch (TaskCanceledException) { } // the explorer was never made
        catch (JSDisconnectedException) { }
        catch (ObjectDisposedException) { }
    }

    private string Describe(Exception ex) => ex switch
    {
        StorageApiException { StatusCode: 400 } api when Mentions(api, "tenant") =>
            L["400: no tenant. Files belong to a tenant: sign in to one (your organisation) to use them."],
        StorageApiException { StatusCode: 401 } => L["401: the gateway did not accept the access token. Sign in again."],
        StorageApiException { StatusCode: 403 } api => $"403: {ProblemDetail(api) ?? L["you do not have access to this."]}",
        StorageApiException { StatusCode: 404 } api => $"404: {ProblemDetail(api) ?? L["not found, or not visible to you."]}",
        StorageApiException { StatusCode: 409 } api => $"409: {ProblemDetail(api) ?? L["someone changed it meanwhile. Refresh and try again."]}",
        StorageApiException api => $"{api.StatusCode}: {ProblemDetail(api) ?? FirstLine(api.Response) ?? FirstLine(api.Message)}",
        HttpRequestException http => L["The gateway is not reachable ({0}).", http.Message],
        _ => ex.Message,
    };

    /// <summary>The "detail" (else "title") of a problem-details response body, if it is one.</summary>
    private static string? ProblemDetail(StorageApiException api)
    {
        if (string.IsNullOrWhiteSpace(api.Response) || !api.Response.TrimStart().StartsWith('{')) return null;
        try
        {
            using var json = JsonDocument.Parse(api.Response);
            foreach (var name in new[] { "detail", "title" })
                if (json.RootElement.TryGetProperty(name, out var value) && value.GetString() is { Length: > 0 } text)
                    return text;
        }
        catch (JsonException) { }
        return null;
    }

    private static bool Mentions(StorageApiException api, string word) =>
        (api.Response ?? "").Contains(word, StringComparison.OrdinalIgnoreCase);

    private static string? FirstLine(string? text)
        => string.IsNullOrWhiteSpace(text) ? null : text.Split('\n', 2)[0].Trim();

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_editor is not null)
        {
            try
            {
                await _editor.InvokeVoidAsync("dispose");
                await _editor.DisposeAsync();
            }
            catch (JSDisconnectedException) { }
        }
        if (_explorer is not null)
        {
            try
            {
                await _explorer.InvokeVoidAsync("dispose");
                await _explorer.DisposeAsync();
            }
            catch (JSDisconnectedException) { }
        }
        _self?.Dispose();
    }
}
