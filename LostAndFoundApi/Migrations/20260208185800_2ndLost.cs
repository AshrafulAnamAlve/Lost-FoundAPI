using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LostAndFoundApi.Migrations
{
    /// <inheritdoc />
    public partial class _2ndLost : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Reward",
                table: "Losts",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "altContact",
                table: "Losts",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "brand",
                table: "Losts",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "color",
                table: "Losts",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "email",
                table: "Losts",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "location",
                table: "Losts",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "phoneNumber",
                table: "Losts",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "userName",
                table: "Losts",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Reward",
                table: "Losts");

            migrationBuilder.DropColumn(
                name: "altContact",
                table: "Losts");

            migrationBuilder.DropColumn(
                name: "brand",
                table: "Losts");

            migrationBuilder.DropColumn(
                name: "color",
                table: "Losts");

            migrationBuilder.DropColumn(
                name: "email",
                table: "Losts");

            migrationBuilder.DropColumn(
                name: "location",
                table: "Losts");

            migrationBuilder.DropColumn(
                name: "phoneNumber",
                table: "Losts");

            migrationBuilder.DropColumn(
                name: "userName",
                table: "Losts");
        }
    }
}
