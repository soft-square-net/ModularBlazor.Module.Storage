# ModularBlazor.Module.Storage

Storage: the tenant's files and folders, with NTFS-style access control, behind a Windows 11-like File Explorer. It is a
module of the [ModularBlazor](https://github.com/soft-square-net/ModularBlazor.Platform) platform, released on its own
as the bundle `storage`.

> **Status: phase 1 of 7 (server foundation).** The API, the data model, the access control and the local disk provider
> are in. S3 / MinIO (phase 2), the Explorer UI (phases 3-4), sharing (phase 5) and the integration points (phase 6-7)
> come next. Nothing here is packed as a bundle yet: build it and upload the entry project's output in the admin area.

## Layout

The server part follows [Ardalis Clean Architecture](https://github.com/ardalis/CleanArchitecture), adapted to a runtime
module, like Module.Content.

```
src/WebApi.Modules.Storage.Core/             Core: ItemAggregate (files, folders, versions), access control (entries, evaluator), groups, blobs, upload sessions
src/WebApi.Modules.Storage.UseCases/         use cases: Mediator commands and queries returning an Ardalis Result; the blob store port
src/WebApi.Modules.Storage.Infrastructure/   data: StorageDbContext, configurations, tenant migrations per provider; the local disk blob store
src/WebApi.Modules.Storage.Web/              entry assembly: IRuntimeModule, the Carter route table, endpoints, signed download links
src/WebApi.Modules.Storage.Client/           typed API client (NSwag), shipped as a hidden client module
src/openapi/   the saved OpenAPI document the client is generated from
tests/         UnitTests (Core, UseCases; NSubstitute) and IntegrationTests (Infrastructure on SQLite, the disk provider)
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
- **Transfers:** uploads are resumable, in chunks (8 MiB by default) streamed through the API; downloads are streamed
  with range support, or by a signed, time-limited link that works without a token (for the browser and for players).

## API (phase 1)

Under `/api/storage`, tenant-bound (header `X-Tenant`, the host name or the token), `storage.access` or `storage.admin`:

| Endpoint | What it does |
| --- | --- |
| `GET /roots` | The caller's top folders: Files, Users, and their home (created on first use) |
| `GET /folders/{id}/items` | A folder's children the caller may see, the path to it, the caller's rights on it |
| `GET /items/{id}` | One item: details, path, versions, the caller's rights |
| `POST /folders` | New folder (`numberIfTaken` gives "New folder (2)") |
| `PATCH /items/{id}` | Rename |
| `POST /items/move`, `/items/copy`, `/items/delete` | Batches; each item that fails is reported, the rest go through |
| `GET /items/{id}/content?version=` | Download (ranges supported) |
| `GET /items/{id}/link?inline=&version=` | A signed link to `GET /links/{token}`, anonymous, valid for `Storage:DownloadLinkLifetime` |
| `POST /uploads`, `PUT /uploads/{id}/chunks?offset=`, `GET /uploads/{id}`, `POST /uploads/{id}/complete`, `DELETE /uploads/{id}` | Resumable upload; on a name conflict: `Fail`, `Replace` (new version) or `KeepBoth` |
| `GET`/`PUT /items/{id}/acl`, `PUT /items/{id}/owner` | The access list (with inherited entries and where they come from), the owner |
| `GET /items/{id}/effective-access?userId=` | The rights a user has on an item, and whether they may use Storage at all |
| `GET /groups`; `POST`, `PUT`, `DELETE /groups/...` (`storage.admin`) | Storage groups |

Settings (`Modules:Settings:storage`): `Storage:Local:Root` (default `%LocalAppData%/ModularBlazor/storage`),
`Storage:MaxFileSize`, `Storage:ChunkSize`, `Storage:UploadLifetime`, `Storage:DownloadLinkLifetime`, `Storage:MaxCopyItems`.

## Building

```bash
dotnet build
dotnet test
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
| server | `WebApi.Modules.Storage.Web` (its build output, zipped: every server layer plus the private Mediator and Ardalis.Result) | Host Modules (`/host-modules`) |
| client, hidden | `WebApi.Modules.Storage.Client` | Client Modules (`/client-modules`) |

## Developing

`playground/` is a small Aspire AppHost: PostgreSQL, the identity service, one WebApi and the Blazor client, with this
module in it. It builds the platform from source, from a
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

Blobs go to `playground/.data/blobs`; data stays in the Docker volume `storage-playground-data` and in
`playground/.data`. The playground is not in `ModularBlazor.Module.Storage.slnx`: CI builds the module without a
platform checkout.

## Branches

Trunk-based: `main` always produces an installable bundle; short-lived `feat/...` and `fix/...` branches; a release
is a tag such as `v1.2.0`.
