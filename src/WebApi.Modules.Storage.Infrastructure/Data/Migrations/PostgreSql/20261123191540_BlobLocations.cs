using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace soft.square.net.WebApi.Modules.Storage.Infrastructure.Data.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class BlobLocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Location",
                schema: "storage",
                table: "uploads",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Location",
                schema: "storage",
                table: "blobs",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Location",
                schema: "storage",
                table: "uploads");

            migrationBuilder.DropColumn(
                name: "Location",
                schema: "storage",
                table: "blobs");
        }
    }
}
