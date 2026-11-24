using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using soft.square.net.WebApi.Modules.Storage.Core.AccessControl;
using soft.square.net.WebApi.Modules.Storage.Core.BlobAggregate;
using soft.square.net.WebApi.Modules.Storage.Core.Common;
using soft.square.net.WebApi.Modules.Storage.Core.GroupAggregate;
using soft.square.net.WebApi.Modules.Storage.Core.ItemAggregate;
using soft.square.net.WebApi.Modules.Storage.Core.PersonAggregate;
using soft.square.net.WebApi.Modules.Storage.Core.ShareAggregate;
using soft.square.net.WebApi.Modules.Storage.Core.UploadAggregate;

namespace soft.square.net.WebApi.Modules.Storage.Infrastructure.Data.Config;

/// <summary>Column lengths shared by several mappings.</summary>
public static class DataSchemaConstants
{
    public const int EnumLength = 20;
}

public sealed class ItemConfiguration : IEntityTypeConfiguration<Item>
{
    public void Configure(EntityTypeBuilder<Item> item)
    {
        item.ToTable("items");
        item.Property(i => i.Id).ValueGeneratedNever();
        item.Property(i => i.Kind).HasConversion<string>().HasMaxLength(DataSchemaConstants.EnumLength);
        item.Property(i => i.Role).HasConversion<string>().HasMaxLength(DataSchemaConstants.EnumLength);
        item.Property(i => i.Scan).HasConversion<string>().HasMaxLength(DataSchemaConstants.EnumLength);
        item.Property(i => i.Name).HasMaxLength(ItemNames.MaxLength);
        item.Property(i => i.NameKey).HasMaxLength(ItemNames.MaxLength);
        item.Property(i => i.Path).HasMaxLength(Item.PathLength);
        item.Property(i => i.OwnerId).HasMaxLength(Item.UserIdLength);
        item.Property(i => i.CreatedBy).HasMaxLength(Item.UserIdLength);
        item.Property(i => i.ModifiedBy).HasMaxLength(Item.UserIdLength);
        item.Property(i => i.DeletedBy).HasMaxLength(Item.UserIdLength);
        item.Property(i => i.ContentType).HasMaxLength(Item.ContentTypeLength);
        item.Property(i => i.Version).IsConcurrencyToken();

        // A name once per folder (deleted items get a key of their own, so no filtered index is needed on any engine).
        item.HasIndex(i => new { i.TenantId, i.ParentId, i.NameKey }).IsUnique();
        item.HasIndex(i => new { i.TenantId, i.Role });

        item.OwnsMany(i => i.Entries, entry =>
        {
            entry.ToTable("access_entries");
            entry.WithOwner().HasForeignKey("ItemId");
            entry.Property<int>("Id").ValueGeneratedOnAdd();
            entry.HasKey("Id");
            entry.Property(e => e.PrincipalKind).HasConversion<string>().HasMaxLength(DataSchemaConstants.EnumLength);
            entry.Property(e => e.PrincipalId).HasMaxLength(AccessEntry.PrincipalIdLength);
            entry.Property(e => e.PrincipalScope).HasMaxLength(AccessEntry.PrincipalScopeLength);
            entry.Property(e => e.Type).HasConversion<string>().HasMaxLength(DataSchemaConstants.EnumLength);
            // "Who has access where": the principal's entries (shared with me, removing a user).
            entry.HasIndex(e => new { e.PrincipalKind, e.PrincipalId });
        });

        item.OwnsMany(i => i.Versions, version =>
        {
            version.ToTable("file_versions");
            version.WithOwner().HasForeignKey("ItemId");
            version.HasKey("ItemId", nameof(FileVersion.Number));
            version.Property(v => v.Number).ValueGeneratedNever();
            version.Property(v => v.BlobKey).HasMaxLength(FileVersion.BlobKeyLength);
            version.Property(v => v.ContentType).HasMaxLength(Item.ContentTypeLength);
            version.Property(v => v.CreatedBy).HasMaxLength(Item.UserIdLength);
            version.HasIndex(v => v.BlobKey);
        });
    }
}

public sealed class StorageGroupConfiguration : IEntityTypeConfiguration<StorageGroup>
{
    public void Configure(EntityTypeBuilder<StorageGroup> group)
    {
        group.ToTable("groups");
        group.Property(g => g.Id).ValueGeneratedNever();
        group.Property(g => g.Name).HasMaxLength(StorageGroup.NameLength);
        group.Property(g => g.NameKey).HasMaxLength(StorageGroup.NameLength);
        group.Property(g => g.Description).HasMaxLength(StorageGroup.DescriptionLength);
        group.Property(g => g.Version).IsConcurrencyToken();
        group.HasIndex(g => new { g.TenantId, g.NameKey }).IsUnique();

        group.OwnsMany(g => g.Members, member =>
        {
            member.ToTable("group_members");
            member.WithOwner().HasForeignKey("GroupId");
            member.HasKey("GroupId", nameof(GroupMember.UserId));
            member.Property(m => m.UserId).HasMaxLength(GroupMember.UserIdLength);
            member.HasIndex(m => m.UserId);
        });
    }
}

public sealed class BlobConfiguration : IEntityTypeConfiguration<Blob>
{
    public void Configure(EntityTypeBuilder<Blob> blob)
    {
        blob.ToTable("blobs");
        blob.Property(b => b.Id).HasMaxLength(FileVersion.BlobKeyLength).ValueGeneratedNever();
        blob.Property(b => b.Location).HasMaxLength(Blob.LocationLength);
        blob.Property(b => b.References).IsConcurrencyToken();
    }
}

public sealed class UploadSessionConfiguration : IEntityTypeConfiguration<UploadSession>
{
    public void Configure(EntityTypeBuilder<UploadSession> upload)
    {
        upload.ToTable("uploads");
        upload.Property(u => u.Id).ValueGeneratedNever();
        upload.Property(u => u.Name).HasMaxLength(ItemNames.MaxLength);
        upload.Property(u => u.ContentType).HasMaxLength(Item.ContentTypeLength);
        upload.Property(u => u.Conflict).HasConversion<string>().HasMaxLength(DataSchemaConstants.EnumLength);
        upload.Property(u => u.BlobKey).HasMaxLength(FileVersion.BlobKeyLength);
        upload.Property(u => u.Location).HasMaxLength(Blob.LocationLength);
        upload.Property(u => u.ProviderUploadId).HasMaxLength(1024);
        upload.Property(u => u.Parts).HasMaxLength(UploadSession.PartsLength);
        upload.Property(u => u.CreatedBy).HasMaxLength(Item.UserIdLength);
        upload.Property(u => u.Version).IsConcurrencyToken();
        upload.HasIndex(u => u.ExpiresAt);
    }
}

public sealed class ShareLinkConfiguration : IEntityTypeConfiguration<ShareLink>
{
    public void Configure(EntityTypeBuilder<ShareLink> link)
    {
        link.ToTable("share_links");
        link.Property(l => l.Id).ValueGeneratedNever();
        link.Property(l => l.Token).HasMaxLength(ShareLink.TokenLength);
        link.Property(l => l.CreatedBy).HasMaxLength(Item.UserIdLength);
        link.Property(l => l.PasswordHash).HasMaxLength(200);
        link.Property(l => l.Version).IsConcurrencyToken();
        link.HasIndex(l => l.Token).IsUnique();
        link.HasIndex(l => l.ItemId);
    }
}

public sealed class StoragePersonConfiguration : IEntityTypeConfiguration<StoragePerson>
{
    public void Configure(EntityTypeBuilder<StoragePerson> person)
    {
        person.ToTable("people");
        person.Property(p => p.Id).ValueGeneratedNever();
        person.Property(p => p.UserId).HasMaxLength(StoragePerson.UserIdLength);
        person.Property(p => p.Name).HasMaxLength(StoragePerson.NameLength);
        person.Property(p => p.Email).HasMaxLength(StoragePerson.EmailLength);
        person.Property(p => p.SearchKey).HasMaxLength(StoragePerson.NameLength + StoragePerson.EmailLength + 1);
        person.HasIndex(p => new { p.TenantId, p.UserId }).IsUnique();
    }
}
