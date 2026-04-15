using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LostAndFoundApi.Migrations
{
    /// <inheritdoc />
    public partial class report : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Reward",
                table: "Founds",
                newName: "type");

            migrationBuilder.AddColumn<string>(
                name: "type",
                table: "Losts",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "type",
                table: "Losts");

            migrationBuilder.RenameColumn(
                name: "type",
                table: "Founds",
                newName: "Reward");
        }
    }
}
