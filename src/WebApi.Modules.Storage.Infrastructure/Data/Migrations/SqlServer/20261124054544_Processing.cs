using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace soft.square.net.WebApi.Modules.Storage.Infrastructure.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class Processing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Scan",
                schema: "storage",
                table: "blobs",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Clean");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ScannedAt",
                schema: "storage",
                table: "blobs",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Threat",
                schema: "storage",
                table: "blobs",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Thumbnail",
                schema: "storage",
                table: "blobs",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.CreateTable(
                name: "events",
                schema: "storage",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    ParentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FileVersion = table.Column<int>(type: "int", nullable: true),
                    By = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_blobs_Scan",
                schema: "storage",
                table: "blobs",
                column: "Scan");

            migrationBuilder.CreateIndex(
                name: "IX_blobs_Thumbnail",
                schema: "storage",
                table: "blobs",
                column: "Thumbnail");

            migrationBuilder.CreateIndex(
                name: "IX_events_At",
                schema: "storage",
                table: "events",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_events_TenantId",
                schema: "storage",
                table: "events",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "events",
                schema: "storage");

            migrationBuilder.DropIndex(
                name: "IX_blobs_Scan",
                schema: "storage",
                table: "blobs");

            migrationBuilder.DropIndex(
                name: "IX_blobs_Thumbnail",
                schema: "storage",
                table: "blobs");

            migrationBuilder.DropColumn(
                name: "Scan",
                schema: "storage",
                table: "blobs");

            migrationBuilder.DropColumn(
                name: "ScannedAt",
                schema: "storage",
                table: "blobs");

            migrationBuilder.DropColumn(
                name: "Threat",
                schema: "storage",
                table: "blobs");

            migrationBuilder.DropColumn(
                name: "Thumbnail",
                schema: "storage",
                table: "blobs");
        }
    }
}
