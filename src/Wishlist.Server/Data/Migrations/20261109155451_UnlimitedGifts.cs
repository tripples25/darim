using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wishlist.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class UnlimitedGifts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Bought",
                table: "Shares",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "OriginalJson",
                table: "Shares",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Unlimited",
                table: "Gifts",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Bought",
                table: "Shares");

            migrationBuilder.DropColumn(
                name: "OriginalJson",
                table: "Shares");

            migrationBuilder.DropColumn(
                name: "Unlimited",
                table: "Gifts");
        }
    }
}
