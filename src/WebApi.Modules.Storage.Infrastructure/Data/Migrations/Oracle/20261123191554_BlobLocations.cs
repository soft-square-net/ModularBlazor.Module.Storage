using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace soft.square.net.WebApi.Modules.Storage.Infrastructure.Data.Migrations.Oracle
{
    /// <inheritdoc />
    public partial class BlobLocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Location",
                table: "storage_uploads",
                type: "NVARCHAR2(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Location",
                table: "storage_blobs",
                type: "NVARCHAR2(300)",
                maxLength: 300,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Location",
                table: "storage_uploads");

            migrationBuilder.DropColumn(
                name: "Location",
                table: "storage_blobs");
        }
    }
}
