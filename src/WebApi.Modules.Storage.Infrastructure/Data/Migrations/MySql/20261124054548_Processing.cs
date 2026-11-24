using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace soft.square.net.WebApi.Modules.Storage.Infrastructure.Data.Migrations.MySql
{
    /// <inheritdoc />
    public partial class Processing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Scan",
                table: "storage_blobs",
                type: "varchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Clean");

            migrationBuilder.AddColumn<DateTime>(
                name: "ScannedAt",
                table: "storage_blobs",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Threat",
                table: "storage_blobs",
                type: "varchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Thumbnail",
                table: "storage_blobs",
                type: "varchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.CreateTable(
                name: "storage_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    TenantId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    Type = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false),
                    ItemId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Kind = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false),
                    ParentId = table.Column<Guid>(type: "char(36)", nullable: true),
                    FileVersion = table.Column<int>(type: "int", nullable: true),
                    By = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    At = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Detail = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_storage_events", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_storage_blobs_Scan",
                table: "storage_blobs",
                column: "Scan");

            migrationBuilder.CreateIndex(
                name: "IX_storage_blobs_Thumbnail",
                table: "storage_blobs",
                column: "Thumbnail");

            migrationBuilder.CreateIndex(
                name: "IX_storage_events_At",
                table: "storage_events",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_storage_events_TenantId",
                table: "storage_events",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "storage_events");

            migrationBuilder.DropIndex(
                name: "IX_storage_blobs_Scan",
                table: "storage_blobs");

            migrationBuilder.DropIndex(
                name: "IX_storage_blobs_Thumbnail",
                table: "storage_blobs");

            migrationBuilder.DropColumn(
                name: "Scan",
                table: "storage_blobs");

            migrationBuilder.DropColumn(
                name: "ScannedAt",
                table: "storage_blobs");

            migrationBuilder.DropColumn(
                name: "Threat",
                table: "storage_blobs");

            migrationBuilder.DropColumn(
                name: "Thumbnail",
                table: "storage_blobs");
        }
    }
}
