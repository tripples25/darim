using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wishlist.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class OpenEndedSplits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "JoinOrder",
                table: "Shares",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "SplitOpen",
                table: "Gifts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("""
                UPDATE "Gifts" SET "SplitOpen" = TRUE WHERE "Mode" = 'split' AND NOT "Bought";
                WITH ordered AS (
                    SELECT s."GiftId", s."UserId", ROW_NUMBER() OVER (
                        PARTITION BY s."GiftId" ORDER BY (s."UserId" = g."DonorId") DESC, s."UserId")::integer AS n
                    FROM "Shares" s JOIN "Gifts" g ON g."Id" = s."GiftId" WHERE g."Mode" = 'split'
                )
                UPDATE "Shares" s SET "JoinOrder" = o.n FROM ordered o
                WHERE s."GiftId" = o."GiftId" AND s."UserId" = o."UserId";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "Gifts" g SET "Slots" = GREATEST(2,
                    (SELECT COUNT(*)::integer FROM "Shares" s WHERE s."GiftId" = g."Id"))
                WHERE g."Mode" = 'split';
                """);
            migrationBuilder.DropColumn(
                name: "JoinOrder",
                table: "Shares");

            migrationBuilder.DropColumn(
                name: "SplitOpen",
                table: "Gifts");
        }
    }
}
