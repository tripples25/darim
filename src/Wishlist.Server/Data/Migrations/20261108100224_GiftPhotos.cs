using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wishlist.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class GiftPhotos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OhmywishesPhotoUrl",
                table: "Gifts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PhotoVersion",
                table: "Gifts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GiftPhotos",
                columns: table => new
                {
                    GiftId = table.Column<Guid>(type: "uuid", nullable: false),
                    Data = table.Column<byte[]>(type: "bytea", nullable: false),
                    ContentType = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GiftPhotos", x => x.GiftId);
                    table.ForeignKey(
                        name: "FK_GiftPhotos_Gifts_GiftId",
                        column: x => x.GiftId,
                        principalTable: "Gifts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GiftPhotos");

            migrationBuilder.DropColumn(
                name: "OhmywishesPhotoUrl",
                table: "Gifts");

            migrationBuilder.DropColumn(
                name: "PhotoVersion",
                table: "Gifts");
        }
    }
}
