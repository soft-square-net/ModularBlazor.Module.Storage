using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace soft.square.net.WebApi.Modules.Storage.Infrastructure.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class Sharing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPublic",
                schema: "storage",
                table: "items",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "people",
                schema: "storage",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SearchKey = table.Column<string>(type: "nvarchar(457)", maxLength: 457, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_people", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "share_links",
                schema: "storage",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Token = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PasswordHash = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    MaxDownloads = table.Column<int>(type: "int", nullable: true),
                    Downloads = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_share_links", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_people_TenantId",
                schema: "storage",
                table: "people",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_people_TenantId_UserId",
                schema: "storage",
                table: "people",
                columns: new[] { "TenantId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_share_links_ItemId",
                schema: "storage",
                table: "share_links",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_share_links_TenantId",
                schema: "storage",
                table: "share_links",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_share_links_Token",
                schema: "storage",
                table: "share_links",
                column: "Token",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "people",
                schema: "storage");

            migrationBuilder.DropTable(
                name: "share_links",
                schema: "storage");

            migrationBuilder.DropColumn(
                name: "IsPublic",
                schema: "storage",
                table: "items");
        }
    }
}
