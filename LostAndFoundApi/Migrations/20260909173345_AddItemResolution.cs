using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LostAndFoundApi.Migrations
{
    /// <inheritdoc />
    public partial class AddItemResolution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "resolvedAt",
                table: "Losts",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "resolvedNote",
                table: "Losts",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "resolvedWithItemId",
                table: "Losts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "resolvedWithUserId",
                table: "Losts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "Losts",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "resolvedAt",
                table: "Founds",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "resolvedNote",
                table: "Founds",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "resolvedWithItemId",
                table: "Founds",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "resolvedWithUserId",
                table: "Founds",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "Founds",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "resolvedAt",
                table: "Losts");

            migrationBuilder.DropColumn(
                name: "resolvedNote",
                table: "Losts");

            migrationBuilder.DropColumn(
                name: "resolvedWithItemId",
                table: "Losts");

            migrationBuilder.DropColumn(
                name: "resolvedWithUserId",
                table: "Losts");

            migrationBuilder.DropColumn(
                name: "status",
                table: "Losts");

            migrationBuilder.DropColumn(
                name: "resolvedAt",
                table: "Founds");

            migrationBuilder.DropColumn(
                name: "resolvedNote",
                table: "Founds");

            migrationBuilder.DropColumn(
                name: "resolvedWithItemId",
                table: "Founds");

            migrationBuilder.DropColumn(
                name: "resolvedWithUserId",
                table: "Founds");

            migrationBuilder.DropColumn(
                name: "status",
                table: "Founds");
        }
    }
}
