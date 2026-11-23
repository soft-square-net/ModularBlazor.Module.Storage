# ModularBlazor.Module.Storage

Storage: the tenant's files and folders, with NTFS-style access control, behind a Windows 11-like File Explorer. It is a
module of the [ModularBlazor](https://github.com/soft-square-net/ModularBlazor.Platform) platform, released on its own
as the bundle `storage`.

> **Status: phase 2 of 7.** The API, the data model, the access control, and the blob stores (the local disk, S3 and
> MinIO through named connections, with presigned downloads and per-tenant quotas) are in. The Explorer UI (phases 3-4;
> its MudTail components are on the platform's `feat/mudtail-explorer` branch), the recycle bin and versions (phase 5),
> sharing (phase 6) and the integration points (phase 7) come next. Nothing here is packed as a bundle yet: build it and upload the entry project's output in the admin area.

## Layout

The server part follows [Ardalis Clean Architecture](https://github.com/ardalis/CleanArchitecture), adapted to a runtime
module, like Module.Content.

```
src/WebApi.Modules.Storage.Core/             Core: ItemAggregate (files, folders, versions), access control (entries, evaluator), groups, blobs, upload sessions
src/WebApi.Modules.Storage.UseCases/         use cases: Mediator commands and queries returning an Ardalis Result; the blob store ports, placements and quotas
src/WebApi.Modules.Storage.Infrastructure/   data: StorageDbContext, configurations, tenant migrations per provider; the blob stores (disk, S3 / MinIO)
src/WebApi.Modules.Storage.Web/              entry assembly: IRuntimeModule, the Carter route table, endpoints, signed download links
src/WebApi.Modules.Storage.Client/           typed API client (NSwag), shipped as a hidden client module
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
  per folder). Deleting moves to the recycle bin (soft delete).
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
| `GET /groups`; `POST`, `PUT`, `DELETE /groups/...` (`storage.admin`) | Storage groups |

## Settings

The module's settings (defaults in the manifest, changed in the admin area) and the host's overrides
(`Modules:Settings:storage:<key>`: environment variables, user secrets, a vault; they reach the module but are never
written to the module catalog, so keys and secrets belong there):

| Key | What it is |
| --- | --- |
| `Storage:MaxFileSize`, `Storage:ChunkSize`, `Storage:UploadLifetime`, `Storage:DownloadLinkLifetime`, `Storage:MaxCopyItems` | Limits and lifetimes |
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

## Building

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
| client, hidden | `WebApi.Modules.Storage.Client` | Client Modules (`/client-modules`) |

## Developing

`playground/` is a small Aspire AppHost: PostgreSQL, MinIO, the identity service, one WebApi and the Blazor client,
with this module in it. It builds the platform from source, from a
[ModularBlazor.Platform](https://github.com/soft-square-net/ModularBlazor.Platform) checkout next to this repo
(`<workspace>/Platform`; another location: `-p:PlatformRoot=<path>/`). It uses the platform lab's ports (identity 5090,
API 5080, client 5070), so stop the Hybird lab first.

| Profile | What it does |
| --- | --- |
| `dev` (default) | The WebApi loads the module and its API client straight from their build output (`Modules:Local`, Development only). Rebuild, restart the `webapi` resource |
| `install` | The platform starts without the module; every build zips it to `playground/out/storage.zip`, to upload on http://localhost:5070/host-modules and activate |

```bash
dotnet run --project playground --launch-profile dev
```

In Development the platform seeds the tenants `acme` and `globex`; call the API with the header `X-Tenant: acme`. The
playground grants, through `Access:DevGrants` (Development only, by email):

| Account | Storage permissions in `acme` |
| --- | --- |
| `acme-admin@modular.local` | `storage.*`: bypasses the access lists, manages groups |
| `editor@modular.local` | `storage.access`: a plain user, to try the access lists with two people |

Blobs go to MinIO, bucket `storage` (console http://localhost:9001, user `playground`, password: the parameter
`minio-password` in the dashboard); with `Playground:Blobs=local` they stay on the disk in `playground/.data/blobs`,
where files written before phase 2 also stay. Data stays in the Docker volumes `storage-playground-data` and
`storage-playground-minio`, and in `playground/.data`. The playground is not in `ModularBlazor.Module.Storage.slnx`: CI builds the module without a
platform checkout.

## Branches

Trunk-based: `main` always produces an installable bundle; short-lived `feat/...` and `fix/...` branches; a release
is a tag such as `v1.2.0`.
