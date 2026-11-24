using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace soft.square.net.WebApi.Modules.Storage.Infrastructure.Data.Migrations.MySql
{
    /// <inheritdoc />
    public partial class InitialStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "storage_blobs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false),
                    TenantId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    References = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_storage_blobs", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "storage_groups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    NameKey = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_storage_groups", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "storage_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    Role = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    ParentId = table.Column<Guid>(type: "char(36)", nullable: true),
                    Name = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false),
                    NameKey = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false),
                    Path = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: false),
                    Depth = table.Column<int>(type: "int", nullable: false),
                    OwnerId = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    InheritsAccess = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    ContentType = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true),
                    CurrentVersion = table.Column<int>(type: "int", nullable: false),
                    Scan = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ModifiedBy = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    DeletedBy = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_storage_items", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "storage_uploads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    FolderId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Name = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    ChunkSize = table.Column<int>(type: "int", nullable: false),
                    Received = table.Column<long>(type: "bigint", nullable: false),
                    Conflict = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    BlobKey = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false),
                    ProviderUploadId = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true),
                    Parts = table.Column<string>(type: "longtext", maxLength: 20000, nullable: true),
                    CreatedBy = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_storage_uploads", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "group_members",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    GroupId = table.Column<Guid>(type: "char(36)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_members", x => new { x.GroupId, x.UserId });
                    table.ForeignKey(
                        name: "FK_group_members_storage_groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "storage_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "access_entries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    PrincipalKind = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    PrincipalId = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true),
                    PrincipalScope = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true),
                    Type = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    Rights = table.Column<int>(type: "int", nullable: false),
                    Inheritance = table.Column<int>(type: "int", nullable: false),
                    ItemId = table.Column<Guid>(type: "char(36)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_access_entries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_access_entries_storage_items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "storage_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "file_versions",
                columns: table => new
                {
                    Number = table.Column<int>(type: "int", nullable: false),
                    ItemId = table.Column<Guid>(type: "char(36)", nullable: false),
                    BlobKey = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    ContentType = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_file_versions", x => new { x.ItemId, x.Number });
                    table.ForeignKey(
                        name: "FK_file_versions_storage_items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "storage_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_access_entries_ItemId",
                table: "access_entries",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_access_entries_PrincipalKind_PrincipalId",
                table: "access_entries",
                columns: new[] { "PrincipalKind", "PrincipalId" });

            migrationBuilder.CreateIndex(
                name: "IX_file_versions_BlobKey",
                table: "file_versions",
                column: "BlobKey");

            migrationBuilder.CreateIndex(
                name: "IX_group_members_UserId",
                table: "group_members",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_storage_blobs_TenantId",
                table: "storage_blobs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_storage_groups_TenantId",
                table: "storage_groups",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_storage_groups_TenantId_NameKey",
                table: "storage_groups",
                columns: new[] { "TenantId", "NameKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_storage_items_TenantId",
                table: "storage_items",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_storage_items_TenantId_ParentId_NameKey",
                table: "storage_items",
                columns: new[] { "TenantId", "ParentId", "NameKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_storage_items_TenantId_Role",
                table: "storage_items",
                columns: new[] { "TenantId", "Role" });

            migrationBuilder.CreateIndex(
                name: "IX_storage_uploads_ExpiresAt",
                table: "storage_uploads",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_storage_uploads_TenantId",
                table: "storage_uploads",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "access_entries");

            migrationBuilder.DropTable(
                name: "file_versions");

            migrationBuilder.DropTable(
                name: "group_members");

            migrationBuilder.DropTable(
                name: "storage_blobs");

            migrationBuilder.DropTable(
                name: "storage_uploads");

            migrationBuilder.DropTable(
                name: "storage_items");

            migrationBuilder.DropTable(
                name: "storage_groups");
        }
    }
}
