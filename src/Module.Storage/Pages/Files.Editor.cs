using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.JSInterop;
using soft.square.net.WebApi.Modules.Storage.Client;

namespace soft.square.net.Module.Storage.Pages;

/// <summary>
/// The image editor (MudTail's imageEditor, from UI.MudTail) and the Security dialog. The editor opens a JPEG, PNG or
/// WebP file through a short-lived inline link; Save stores the edited image as a new version of the file (same name and
/// type, so links and pickers keep pointing at it), Save as a copy as a new file next to it ("a (2).png"). Both are
/// ordinary uploads, so the server checks the rights, the quota and the virus scan as for any upload.
/// </summary>
public partial class Files
{
    /// <summary>The types the editor saves back as they are (the browser encodes them); others open read-only elsewhere.</summary>
    private static readonly Dictionary<string, string> EditableTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = "jpeg", ["image/png"] = "png", ["image/webp"] = "webp",
    };

    private sealed record EditedFile(Guid Id, string Name, string ContentType, string Format, Guid FolderId);

    private EditedFile? _edited;
    private bool _editorPending; // the modal is rendering; the editor is made after it
    private ElementReference _editorHost;
    private IJSObjectReference? _editor;
    private bool _editorChanged;
    private bool _editorBusy;

    private async Task OpenEditorAsync(Picked file)
    {
        if (file.ContentType is not { } type || !EditableTypes.TryGetValue(type, out var format))
        {
            await MudTail.ToastAsync(L["Only JPEG, PNG and WebP images can be edited."], "info");
            return;
        }
        var detail = await Api.GetItemAsync(file.Id);
        if (detail.Item.ParentId is not { } folder) return;
        _edited = new EditedFile(file.Id, detail.Item.Name, type, format, folder);
        _editorChanged = false;
        _editorPending = true;
        StateHasChanged();
    }

    /// <summary>Made once the modal's host is in the page (from OnAfterRenderAsync).</summary>
    private async Task CreateEditorAsync()
    {
        if (_edited is not { } file) return;
        try
        {
            var runtime = await MudTail.GetAsync();
            if (runtime is null) return;
            var src = await LinkAsync(file.Id, inline: true);
            _editor = await runtime.InvokeAsync<IJSObjectReference>("create", "imageEditor", _editorHost, new
            {
                src,
                sizes = Array.Empty<object>(), // one output: the edited image at its own size
                format = file.Format,
                quality = 0.92,
                fileName = Path.GetFileNameWithoutExtension(file.Name),
                height = "100%",
            }, _self);
            StateHasChanged(); // the bar's buttons wait for the editor
        }
        catch (Exception ex) when (ex is not AccessTokenNotAvailableException)
        {
            await MudTail.ToastAsync(Describe(ex), "error");
            await CloseEditorAsync(force: true);
        }
    }

    /// <summary>The editor's events (raised to the same OnMudEvent as the explorer's).</summary>
    private void EditorEvent(string name, JsonElement payload)
    {
        switch (name)
        {
            case "change":
                if (!_editorChanged)
                {
                    _editorChanged = true;
                    StateHasChanged();
                }
                break;
            case "error":
                _ = MudTail.ToastAsync(payload.ValueKind == JsonValueKind.String ? payload.GetString() ?? "" : L["The image could not be opened."].Value, "error");
                break;
        }
    }

    /// <summary>Saves the edited image: a new version of the file, or a copy next to it.</summary>
    private async Task SaveEditedAsync(bool copy)
    {
        if (_edited is not { } file || _editor is null || _editorBusy) return;
        _editorBusy = true;
        StateHasChanged();
        Guid? uploadId = null;
        try
        {
            var bytes = await _editor.InvokeAsync<byte[]?>("exportImage", file.Format, 0.92);
            if (bytes is null || bytes.Length == 0) throw new InvalidOperationException(L["The image could not be exported."]);
            var upload = await Api.StartUploadAsync(new StartUploadCommand
            {
                FolderId = file.FolderId, Name = file.Name, Size = bytes.Length, ContentType = file.ContentType,
                Conflict = copy ? NameConflict.KeepBoth : NameConflict.Replace,
            });
            uploadId = upload.Id;
            for (var offset = 0; offset < bytes.Length; offset += upload.ChunkSize)
            {
                var length = (int)Math.Min(upload.ChunkSize, bytes.Length - offset);
                using var chunk = new MemoryStream(bytes, offset, length, writable: false);
                await Api.WriteChunkAsync(upload.Id, offset, new FileParameter(chunk, file.Name, "application/octet-stream"));
            }
            var item = await Api.CompleteUploadAsync(upload.Id);
            uploadId = null;
            _editorChanged = false;
            await MudTail.ToastAsync(copy
                ? L["Saved as \"{0}\".", item.Name].Value
                : L["Saved as version {0} of \"{1}\".", item.CurrentVersion, item.Name].Value, "success");
            await CallAsync("refresh");
        }
        catch (AccessTokenNotAvailableException ex)
        {
            ex.Redirect();
        }
        catch (Exception ex)
        {
            await MudTail.ToastAsync(Describe(ex), "error");
            if (uploadId is { } abandoned)
                try { await Api.CancelUploadAsync(abandoned); } catch (Exception) { /* it expires by itself */ }
        }
        finally
        {
            _editorBusy = false;
            StateHasChanged();
        }
    }

    private async Task CloseEditorAsync(bool force = false)
    {
        if (!force && _editorChanged && !await ConfirmAsync(L["Close the editor without saving your changes?"].Value, L["Discard"].Value)) return;
        var editor = _editor;
        _editor = null;
        _edited = null;
        _editorPending = false;
        StateHasChanged();
        if (editor is null) return;
        try
        {
            await editor.InvokeVoidAsync("dispose");
            await editor.DisposeAsync();
        }
        catch (JSDisconnectedException) { }
    }

    // ───────── Security ─────────

    private sealed record SecurityTarget(Guid Id, string Name, bool IsFolder, int Rights);

    private SecurityTarget? _securityOf;

    /// <summary>Opens Security for the selected item, or the open folder when nothing is selected.</summary>
    private async Task ShowSecurityAsync(Picked item)
    {
        var detail = await Api.GetItemAsync(item.Id);
        _securityOf = new SecurityTarget(item.Id, NameOf(detail.Item), detail.Item.Kind == ItemKind.Folder, detail.Item.Rights);
        StateHasChanged();
    }

    private void CloseSecurity() => _securityOf = null;

    /// <summary>The page's names for its own places; other folders by their name.</summary>
    private string FolderDisplayName(Guid id, string name) =>
        _roots is null ? name
        : id == _roots.Home.Id ? L["My files"].Value
        : id == _roots.Files.Id ? L["Files"].Value
        : id == _roots.Home.ParentId ? L["Users"].Value
        : name;

    private Task SecurityChangedAsync() => CallAsync("refresh");
}
