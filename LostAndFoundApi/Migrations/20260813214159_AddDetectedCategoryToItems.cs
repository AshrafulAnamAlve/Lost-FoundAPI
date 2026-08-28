using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LostAndFoundApi.Migrations
{
    /// <inheritdoc />
    public partial class AddDetectedCategoryToItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "detectedCategory",
                table: "Losts",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "detectedConfidence",
                table: "Losts",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "detectedCategory",
                table: "Founds",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "detectedConfidence",
                table: "Founds",
                type: "float",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "detectedCategory",
                table: "Losts");

            migrationBuilder.DropColumn(
                name: "detectedConfidence",
                table: "Losts");

            migrationBuilder.DropColumn(
                name: "detectedCategory",
                table: "Founds");

            migrationBuilder.DropColumn(
                name: "detectedConfidence",
                table: "Founds");
        }
    }
}
