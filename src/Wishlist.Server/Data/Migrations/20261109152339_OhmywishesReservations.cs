using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wishlist.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class OhmywishesReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "OhmywishesOccupied",
                table: "Gifts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OhmywishesReservationChanged",
                table: "Gifts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OhmywishesReservationSource",
                table: "Gifts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OhmywishesWebId",
                table: "Gifts",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OhmywishesOccupied",
                table: "Gifts");

            migrationBuilder.DropColumn(
                name: "OhmywishesReservationChanged",
                table: "Gifts");

            migrationBuilder.DropColumn(
                name: "OhmywishesReservationSource",
                table: "Gifts");

            migrationBuilder.DropColumn(
                name: "OhmywishesWebId",
                table: "Gifts");
        }
    }
}
