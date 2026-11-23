using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wishlist.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class GiftVariants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SelectedVariantJson",
                table: "WishlistReservations",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SelectedVariantJson",
                table: "Gifts",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VariantsJson",
                table: "Gifts",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "VariantsVersion",
                table: "Gifts",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SelectedVariantJson",
                table: "WishlistReservations");

            migrationBuilder.DropColumn(
                name: "SelectedVariantJson",
                table: "Gifts");

            migrationBuilder.DropColumn(
                name: "VariantsJson",
                table: "Gifts");

            migrationBuilder.DropColumn(
                name: "VariantsVersion",
                table: "Gifts");
        }
    }
}
