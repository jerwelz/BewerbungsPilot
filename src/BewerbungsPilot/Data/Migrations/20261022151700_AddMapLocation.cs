using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BewerbungsPilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMapLocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LocationPrecision",
                table: "Applications",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                // Edited by hand: existing applications get "Unknown" (no confirmed position).
                // The generated "" could not be read back as a LocationPrecision value.
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<string>(
                name: "MapLabel",
                table: "Applications",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MapLatitude",
                table: "Applications",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MapLongitude",
                table: "Applications",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LocationPrecision",
                table: "Applications");

            migrationBuilder.DropColumn(
                name: "MapLabel",
                table: "Applications");

            migrationBuilder.DropColumn(
                name: "MapLatitude",
                table: "Applications");

            migrationBuilder.DropColumn(
                name: "MapLongitude",
                table: "Applications");
        }
    }
}
