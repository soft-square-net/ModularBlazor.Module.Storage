# ModularBlazor.Module.Storage

Storage: the tenant's files and folders, with NTFS-style access control, behind a Windows 11-like File Explorer. It is a
module of the [ModularBlazor](https://github.com/soft-square-net/ModularBlazor.Platform) platform, released on its own
as the bundle `storage`.

> **Status: phase 7 of 7.** The API, the data model, the access control, the blob stores (the local disk, S3 and MinIO
> through named connections, with presigned downloads and per-tenant quotas), the Files page (the Explorer: browsing,
> new folders, rename, cut/copy/paste and drag to move, delete, uploads of files and folders, downloads, image previews),
> the recycle bin, previous versions, the sweeper (purging old deletions, unused blobs and abandoned uploads) and sharing
> (with people, by links, public folders), and the integration points (the file and folder pickers for other modules,
> the change feed for Elsa, thumbnails, virus scanning with ClamAV) are in.
> The Explorer needs MudTail's explorer component, on the platform's `feat/mudtail-explorer` branch. The Security
> dialog and the image editor are not built yet. Nothing here is packed as a bundle yet: build it and upload the entry project's output in the admin area.

## Layout

The server part follows [Ardalis Clean Architecture](https://github.com/ardalis/CleanArchitecture), adapted to a runtime
module, like Module.Content.

```
src/WebApi.Modules.Storage.Core/             Core: ItemAggregate (files, folders, versions), access control (entries, evaluator), groups, blobs, upload sessions
src/WebApi.Modules.Storage.UseCases/         use cases: Mediator commands and queries returning an Ardalis Result; the blob store ports, placements and quotas
src/WebApi.Modules.Storage.Infrastructure/   data: StorageDbContext, configurations, tenant migrations per provider; the blob stores (disk, S3 / MinIO)
src/WebApi.Modules.Storage.Web/              entry assembly: IRuntimeModule, the Carter route table, endpoints, signed download links
src/WebApi.Modules.Storage.Client/           typed API client (NSwag), shipped as a hidden client module
src/Module.Storage/                          Blazor client module "Files": the Explorer (MudTail's explorer over the API), i18n ar default
src/Module.Storage.Shares/                   Blazor client module "Shared links": the anonymous page that opens a share link (/s/{tenant}/{token})
src/openapi/   the saved OpenAPI document the client is generated from
tests/         UnitTests (Core, UseCases; NSubstitute) and IntegrationTests (Infrastructure on SQLite, the disk and S3 stores)
playground/    Aspire AppHost: the platform (from a Platform checkout) with this module, see "Developing"
```

## The model

- **The database is the source of truth.** Every file and folder is a row (`storage.items`) with a materialized path
  (`/{id}/{id}/.../`); the blob store holds content only, under keys the module makes (`t/{tenant}/{yyyy}/{MM}/{id}`).
  Copies share a blob (reference counted); every upload of a file is a new version.
- **Top folders** per tenant, created on first use: `Files` (shared), `Users` (homes) and one home per user, named by
  the user id. Names follow the Windows rules (no `\ / : * ? " < > |`, no reserved names, case-insensitive uniqueness
  per folder). Deleting moves to the recycle bin (soft delete): a deleted folder takes what is in it along, and
  restoring puts it back where it was (numbered, "a (2).txt", if the name was taken meanwhile; a folder that is itself
  in the bin must be restored first). Each user sees what they deleted; `storage.admin` sees the whole tenant's bin.
- **Purging and blob GC:** purging (Shift+Delete, deleting in the bin, emptying it, or the sweeper after
  `Storage:RecycleBinDays`) removes the items and releases each version's blob. A blob nothing refers to any more
  (no version, no copy) is deleted from its store, then its row; a store that fails is retried on the next sweep.
- **Platform permissions** are the gate: `storage.access` to use Storage at all, `storage.admin` to bypass the access
  lists and manage storage groups.
- **Access lists (NTFS-style)** on every item: allow and deny entries for a user, everyone in the tenant, the holders of
  a platform permission, a storage group, or CREATOR OWNER; NTFS rights (read, write, delete, delete children, change
  permissions, take ownership, ...) and inheritance flags (this folder, subfolders, files, no propagation, inherit only).
  Evaluation is canonical: explicit deny, explicit allow, then inherited entries nearest first. Inheritance can be
  broken (copying or dropping the inherited entries). The owner can always read and change the permissions. Listings
  show only what the caller may see (access-based enumeration).
- **Transfers:** uploads are resumable, in chunks (8 MiB by default) streamed through the API; on S3 every chunk is a
  part of a multipart upload (at least 5 MiB), and a file has at most 500 chunks (big files get bigger ones). Downloads
  are streamed by the API (ranges on the local disk), or by a time-limited link that works without a token, for the
  browser and for players: presigned by S3 (the bytes go from the bucket to the browser), signed by the module on disk.
- **Where blobs live:** each tenant has a placement (a named connection, a bucket, a key prefix) and an optional quota.
  A blob remembers where it was written, so a new placement moves only new files. The quota counts what the tenant
  keeps (every version; a blob shared by copies once) plus uploads under way.
- **Sharing** is three things. *With people*: the Share dialog writes the person's access entries on the item (view =
  read and execute, edit = modify; a folder's entries reach its subfolders and files); it needs the new right `Share`
  (edit also needs `Modify` yourself). The people are Storage's own list: members who have opened Files, with the name
  and email from their token. *Links*: "anyone with the link" for a file or a folder, read-only, with an optional
  expiry, password (PBKDF2; sent in the `X-Share-Password` header, never in a URL) and download limit; revocable,
  at most 20 per item. *Public folders*: everything in (and below) a public folder has a stable anonymous address,
  `/api/storage/public/{tenant}/{fileId}/{name}`, for Content's published pages.
- **Virus scanning** (when `Storage:ClamAV:Host` is set): a new upload is *pending* until clamd has checked it, in the
  background. Until it is clean a file is listed (with its state) but cannot be downloaded, previewed, linked, shared by
  a link or served from a public folder. An infected file stays quarantined until someone deletes it; restoring a clean
  previous version recovers it. When clamd cannot be reached, files wait; without a scanner configured (or after it is
  removed) files are clean at once. The verdict belongs to the blob, so copies and versions sharing it share it.
- **Thumbnails:** clean JPEG, PNG, GIF, WebP, BMP and TIFF files (up to `Storage:ThumbnailMaxBytes`) get a WebP
  thumbnail in the background (ImageSharp), stored next to the blob (`{key}.thumb.webp`) and deleted with it. Listings
  carry a short-lived `thumbnailUrl`.
- **The change feed** (an outbox, `storage.events`): every create, new version, rename, move, delete, restore, purge
  and scan verdict is a row written in the same transaction as the change, kept `Storage:EventRetentionDays`. Elsa (or
  anything else) reads it with `GET /events?after={last id}`.

## API

The API so far (the client module `WebApi.Modules.Storage.Client` is generated from it):

Under `/api/storage`, tenant-bound (header `X-Tenant`, the host name or the token), `storage.access` or `storage.admin`:

| Endpoint | What it does |
| --- | --- |
| `GET /roots` | The caller's top folders: Files, Users, and their home (created on first use) |
| `GET /usage` | The tenant's bytes kept, bytes of uploads under way, and the quota (null: none) |
| `GET /folders/{id}/items` | A folder's children the caller may see, the path to it, the caller's rights on it |
| `GET /items/{id}` | One item: details, path, versions, the caller's rights |
| `POST /folders` | New folder (`numberIfTaken` gives "New folder (2)") |
| `PATCH /items/{id}` | Rename |
| `POST /items/move`, `/items/copy`, `/items/delete` | Batches; each item that fails is reported, the rest go through |
| `GET /items/{id}/content?version=` | Download (ranges supported) |
| `GET /items/{id}/link?inline=&version=` | A link valid for `Storage:DownloadLinkLifetime`: presigned by S3, or the module's anonymous `GET /links/{token}` |
| `POST /uploads`, `PUT /uploads/{id}/chunks?offset=`, `GET /uploads/{id}`, `POST /uploads/{id}/complete`, `DELETE /uploads/{id}` | Resumable upload; on a name conflict: `Fail`, `Replace` (new version) or `KeepBoth` |
| `GET`/`PUT /items/{id}/acl`, `PUT /items/{id}/owner` | The access list (with inherited entries and where they come from), the owner |
| `GET /items/{id}/effective-access?userId=` | The rights a user has on an item, and whether they may use Storage at all |
| `POST /items/delete` with `permanent: true` | Delete without the recycle bin (needs the same rights) |
| `GET /recycle-bin` | What the caller deleted (everything, for `storage.admin`): the item, where it was, when, by whom |
| `POST /recycle-bin/restore`, `POST /recycle-bin/purge` | Restore or purge a batch (purge with no ids empties the visible bin); the deleter, or an admin for purge |
| `POST /items/{id}/versions/{number}/restore` | Make an old version current again (a new version with its content; needs `CreateFiles` on the folder) |
| `DELETE /items/{id}/versions/{number}` | Delete an old version (not the current one) |
| `GET /groups`; `POST`, `PUT`, `DELETE /groups/...` (`storage.admin`) | Storage groups |
| `POST /maintenance/sweep` (`storage.admin`) | Run the tenant's sweep now; returns what it purged and freed |
| `GET /people?search=` | Storage's people (members who have used Files), by name or email; at least 2 characters |
| `GET /shared-with-me` | Items shared with the caller by name, with the owner and where they are |
| `GET /items/{id}/sharing` | The Share dialog: the people, the links, whether the folder is (or is in) a public folder |
| `POST /items/{id}/sharing/people`, `DELETE /items/{id}/sharing/people/{userId}` | Share with people (`View` or `Edit`), stop sharing |
| `PUT /items/{id}/sharing/public` | Make a folder public, or not (`Share` and `ChangePermissions`) |
| `POST /items/{id}/sharing/links`, `DELETE /sharing/links/{linkId}` | Create a link (expiry, password, download limit), remove one |
| `GET /events?after=&take=` (`storage.events` or `storage.admin`) | The change feed: events after an id, oldest first (`take` up to 500, default 100), `last` (the id to ask after next) and `more`. The newest `Storage:EventDelay` is held back, so a slower transaction's event is never skipped |

Downloads, links, share links and public addresses of a file that is not clean answer 409 (404 for public addresses).

The change feed's events are `{ id, type, itemId, kind, name, parentId, fileVersion, by, at, detail }`, with `type` one
of `item.created`, `file.versioned`, `item.renamed` (detail: the old name), `item.moved` (detail: the old folder's id),
`item.deleted`, `item.restored`, `item.purged` (detail: how many items went), `file.scanned` (detail: `clean` or
`infected: {threat}`). For Elsa, give its client the platform permission `storage.events` in the tenant and poll with a
client-credentials token and `X-Tenant`, keeping `last`.

Anonymous (no token; the tenant is in the URL):

| Endpoint | What it does |
| --- | --- |
| `GET /shares/{tenant}/{token}?folderId=` | Open a link: the shared item, or a folder in it (header `X-Share-Password` when it has one); 401 for a missing or wrong password, 404 when it no longer works |
| `GET /shares/{tenant}/{token}/files/{fileId}/link?inline=` | A short-lived download link of a file in the share; counts a download |
| `GET /public/{tenant}/{fileId}/{name}` | A file in a public folder: a redirect to a short-lived link (cached a minute) |

## Settings

The module's settings (defaults in the manifest, changed in the admin area) and the host's overrides
(`Modules:Settings:storage:<key>`: environment variables, user secrets, a vault; they reach the module but are never
written to the module catalog, so keys and secrets belong there):

| Key | What it is |
| --- | --- |
| `Storage:MaxFileSize`, `Storage:ChunkSize`, `Storage:UploadLifetime`, `Storage:DownloadLinkLifetime`, `Storage:MaxCopyItems` | Limits and lifetimes |
| `Storage:RecycleBinDays` | Days a deleted item stays in the recycle bin before the sweeper purges it (default 30; 0 keeps everything) |
| `Storage:EventRetentionDays`, `Storage:EventDelay` | How long the change feed keeps events (default 7 days; the sweeper removes older ones) and how much of its newest end it holds back (default `00:00:05`) |
| `Storage:ProcessInterval` | How often every active tenant's pending scans and thumbnails are looked for (default `00:01:00`); new uploads are processed at once |
| `Storage:ThumbnailSize`, `Storage:ThumbnailMaxBytes` | The thumbnails' longest side (default 320) and the largest image given one (default 50 MiB) |
| `Storage:ClamAV:Host`, `:Port`, `:Timeout` | clamd (empty host: no scanning; port 3310; timeout per file `00:02:00`). Set clamd's `StreamMaxLength` to at least `Storage:MaxFileSize`, or big files are reported as errors and wait |
| `Storage:SweepInterval` | How often every active tenant is swept (default `01:00:00`, the first a minute after the module loads; `00:00:00` turns it off). Every replica sweeps; a sweep is safe to run twice |
| `Storage:Local:Root` | The local disk (default `%LocalAppData%/ModularBlazor/storage`); every replica must see the same folder |
| `Storage:Connections:{name}:Kind` | `S3` (default) or `Local` |
| `...:ServiceUrl`, `...:Region` | S3: the endpoint (empty for Amazon S3) and the region (default `us-east-1`) |
| `...:AccessKey`, `...:SecretKey` | S3: the keys (empty: the default AWS credentials, e.g. an instance role). Host overrides only |
| `...:ForcePathStyle` | S3: `true` for MinIO |
| `...:PublicUrl` | S3: the endpoint browsers reach, when the API reaches another one; links are presigned for it |
| `...:CreateBucket` | S3: create a missing bucket on first use (development) |
| `...:Root` | Local: the folder (blobs under `Root/bucket/prefix`) |
| `Storage:Default:Connection`, `:Bucket`, `:Prefix`, `:Quota` | Every tenant's placement and quota in bytes (connection empty or `local`: the local disk; quota 0 or empty: none) |
| `Storage:Tenants:{tenant}:...` | The same keys for one tenant, over the default key by key |

For example, every tenant in MinIO, one tenant on the disk with a 10 GiB quota:

```bash
Modules__Settings__storage__Storage__Connections__minio__ServiceUrl=http://minio:9000
Modules__Settings__storage__Storage__Connections__minio__PublicUrl=https://files.example.com
Modules__Settings__storage__Storage__Connections__minio__ForcePathStyle=true
Modules__Settings__storage__Storage__Connections__minio__AccessKey=storage
Modules__Settings__storage__Storage__Connections__minio__SecretKey=<secret>
Modules__Settings__storage__Storage__Default__Connection=minio
Modules__Settings__storage__Storage__Default__Bucket=storage
Modules__Settings__storage__Storage__Tenants__acme__Connection=local
Modules__Settings__storage__Storage__Tenants__acme__Quota=10737418240
```

Removing a connection that blobs were written to makes those files unreadable (the error names the connection).

## Pickers for other modules

Other modules open Storage's dialogs through `IModuleDialogs` (the result is web JSON; null when cancelled). Keep the
file's id and the snapshot: names change, the id does not.

| Dialog | Parameters | Result |
| --- | --- | --- |
| `Module.Storage/pick-files` | `{ multiple?, accept?: [".pdf", "image/*", "text/plain"], folderId?, publicOnly? }` | `{ ids, files: [{ id, name, size, contentType, version, folderId, publicUrl }] }` |
| `Module.Storage/pick-folder` | `{ folderId?, rights?: "create" \| "read" }` (default `create`: the caller may add files) | `{ id, name, path: [{ id, name }], publicFolder }` |

Files that are pending or infected cannot be chosen. `publicOnly` offers only files in public folders, and `publicUrl`
is their anonymous address (null elsewhere), for published pages.

## Building

Thumbnails use SixLabors.ImageSharp 4, which needs a licence key at build time: set the `SixLaborsLicenseKey`
environment variable (a CI secret). Debug builds only warn without it; Release builds fail. Never commit the key or a
`sixlabors.lic` file.

```bash
dotnet build
dotnet test
```

The S3 store's tests run against a real server when one is given, and are skipped otherwise:

```bash
docker run -d -p 9000:9000 -e MINIO_ROOT_USER=storagetest -e MINIO_ROOT_PASSWORD=<secret> minio/minio:RELEASE.2025-09-07T16-13-09Z server /data
STORAGE_TEST_S3_URL=http://localhost:9000 STORAGE_TEST_S3_ACCESS_KEY=storagetest STORAGE_TEST_S3_SECRET_KEY=<secret> dotnet test
```

The platform packages come from the soft-square-net GitHub Packages feed, which needs a token to restore even though
the packages are public: create a personal access token (classic) with `read:packages` and put it in the environment
as `GITHUB_PACKAGES_TOKEN` (`NuGet.Config` reads it). The platform version is pinned once, in `global.json`.

Tools that need the platform's source run from a checkout of
[ModularBlazor.Platform](https://github.com/soft-square-net/ModularBlazor.Platform), pointed at this repo. Re-export the
OpenAPI document (the entry project is `Web`), then regenerate the client (`nswag.json` in the Client project):

```powershell
./src/WebApi/scripts/Export-ModuleOpenApi.ps1 -Project <this repo>/src/WebApi.Modules.Storage.Web -ModuleId storage
```

Add a migration for every database provider:

```powershell
./src/WebApi/scripts/Add-Migration.ps1 -Project <this repo>/src/WebApi.Modules.Storage.Infrastructure -Name <Name> `
    -MigrationsDir Data/Migrations -Namespace soft.square.net.WebApi.Modules.Storage.Infrastructure.Data.Migrations
```

## How it builds and ships

- It compiles against the platform's contract packages (`WebApi.Contracts`, `ModularBlazor.Abstractions`, ...) from the
  soft-square-net GitHub Packages feed. It never references platform source.
- CI packs one bundle per release: `<module>-<version>.modpkg`, a zip with `module.json`, `server/`, `client/` and
  `contracts/`. The platform's admin area installs it: server part first (tenant migrations), then the client part.
- One version for the whole bundle, set once from the git tag. Unchanged parts are skipped on install.
- An API client ships once, inside the bundle that owns the API, as a hidden client module.

| Part | Project | Installed on |
| --- | --- | --- |
| server | `WebApi.Modules.Storage.Web` (its build output, zipped: every server layer plus the private Mediator, Ardalis.Result and AWS SDK) | Host Modules (`/host-modules`) |
| client, hidden | `WebApi.Modules.Storage.Client` | Client Modules (`/client-modules`), first |
| client | `Module.Storage`: menu item "Files", the page `/files` (`?folder={id}` opens a folder) | Client Modules, after the API client |
| client | `Module.Storage.Shares`: no menu item, the page `/s/{tenant}/{token}`, layout `public`, no sign-in | Client Modules, after the API client |

`Module.Storage` lists the API client in its `Dependencies` and references it with `Private="false"`, so its output
holds only `soft.square.net.Module.Storage.dll`. The API client does not require sign-in itself (`RequiresAuth = false`),
so the anonymous Shared links module can load it; its endpoints still need a token, except the anonymous ones above. Each build also copies both client parts to `playground/out/client`.

### The Explorer

The page creates MudTail's `explorer` (from the Layouts.Default runtime) and answers its questions from the API: the
places (My files, Files), folder listings and the tree, in-place renames, image previews (short-lived inline links), and
the commands. Uploads go chunk by chunk (the chunk size the API sets; each chunk read from the browser), a taken name
gets a number (`KeepBoth`), and a picked folder keeps its structure. The rights the API returns become the explorer's
deny lists, so refused commands are greyed out: renaming, moving and deleting need `Delete` on the item or
`DeleteChildren` on its folder; New folder needs `CreateFolders`, uploads `CreateFiles`. Search filters the open
folder. Pinned folders (Quick access) are kept in the browser. Security and Edit are disabled until their phases.

**Share** (a file's or folder's context menu, or the command bar, for those with the `Share` right) opens the Share
dialog: add people by name or email with view or edit access, change or remove it; create links (each is copied when
made) and remove them; make a folder public, or copy a file's public address when it is in a public folder.
**Shared with me** is a place in the navigation pane listing what others shared with the caller by name.

Images show their **thumbnails** in the large icon views, and a file that is waiting for the virus check or infected
says so in the details pane; it cannot be opened, downloaded or shared until it is clean.

The **Recycle bin** is a place in the navigation pane: Date modified is when the item was deleted; Restore puts the
selection back, Delete purges it, and Empty recycle bin purges everything the caller can see, each after asking.
Shift+Delete anywhere skips the bin (after asking). **Previous versions** (a file's context menu) lists every kept
version: open one in a new tab, restore it (it becomes a new current version) or delete it.

## Developing

`playground/` is a small Aspire AppHost: PostgreSQL, MinIO, the identity service, one WebApi and the Blazor client,
with this module in it. It builds the platform from source, from a
[ModularBlazor.Platform](https://github.com/soft-square-net/ModularBlazor.Platform) checkout next to this repo
(`<workspace>/Platform`; another location: `-p:PlatformRoot=<path>/`). It uses the platform lab's ports (identity 5090,
API 5080, client 5070), so stop the Hybird lab first.

| Profile | What it does |
| --- | --- |
| `dev` (default) | The WebApi loads the module and its client parts straight from their build output (`Modules:Local`, `ClientModules:Local`; Development only). Rebuild, restart the `webapi` resource |
| `install` | The platform starts without the module; every build zips it to `playground/out/storage.zip`, to upload on http://localhost:5070/host-modules and activate |

```bash
dotnet run --project playground --launch-profile dev
```

The Files page needs a platform with MudTail's explorer (until `feat/mudtail-explorer` is merged, point PlatformRoot at
a checkout of that branch: `dotnet run --project playground -p:PlatformRoot=<that checkout>/`). Sign in on
http://localhost:5070 as an `acme` account and open Files.

In Development the platform seeds the tenants `acme` and `globex`; call the API with the header `X-Tenant: acme`. The
playground grants, through `Access:DevGrants` (Development only, by email):

| Account | Storage permissions in `acme` |
| --- | --- |
| `acme-admin@modular.local` | `storage.*`: bypasses the access lists, manages groups |
| `editor@modular.local` | `storage.access`: a plain user, to try the access lists with two people |

Blobs go to MinIO, bucket `storage` (console http://localhost:9001, user `playground`, password: the parameter
`minio-password` in the dashboard); with `Playground:Blobs=local` they stay on the disk in `playground/.data/blobs`,
where files written before phase 2 also stay.

The MinIO image is pinned to `minio/minio:RELEASE.2025-09-07T16-13-09Z`. MinIO removed its images from Docker Hub (and
`quay.io/minio/minio` refuses anonymous pulls), so that tag can no longer be pulled: it has to be in the local Docker
already. A machine that has that release under another tag can name it, e.g.
`docker tag minio/minio:latest minio/minio:RELEASE.2025-09-07T16-13-09Z` (check first with
`docker run --rm minio/minio:latest --version`). Without it, use `Playground:Blobs=local`.

Data stays in the Docker volumes `storage-playground-data` and
`storage-playground-minio`, and in `playground/.data`. The playground is not in `ModularBlazor.Module.Storage.slnx`: CI builds the module without a
platform checkout.

## Branches

Trunk-based: `main` always produces an installable bundle; short-lived `feat/...` and `fix/...` branches; a release
is a tag such as `v1.2.0`.
