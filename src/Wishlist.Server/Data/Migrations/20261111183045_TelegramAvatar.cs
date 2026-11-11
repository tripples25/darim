using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wishlist.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class TelegramAvatar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TelegramAvatarUrl",
                table: "Users",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TelegramAvatarUrl",
                table: "Users");
        }
    }
}
