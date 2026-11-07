using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace Wishlist.Server.Data.Migrations;

[DbContext(typeof(AppDb))]
[Migration("20261107120000_OhmywishesSync")]
public sealed class OhmywishesSync : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>("OhmywishesAttempt", "Users", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<string>("OhmywishesKey", "Gifts", type: "text", nullable: true);
        migrationBuilder.AddColumn<bool>("OhmywishesHidden", "Gifts", type: "boolean", nullable: false, defaultValue: false);
        migrationBuilder.CreateIndex("IX_Gifts_OwnerId_OhmywishesKey", "Gifts", new[] { "OwnerId", "OhmywishesKey" }, unique: true);
        migrationBuilder.DropIndex("IX_Gifts_OwnerId", "Gifts");
        migrationBuilder.CreateTable(
            name: "OhmywishesConnections",
            columns: table => new
            {
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                Revision = table.Column<Guid>(type: "uuid", nullable: false),
                AccountId = table.Column<string>(type: "text", nullable: false),
                ProtectedTokens = table.Column<string>(type: "text", nullable: true),
                ListId = table.Column<string>(type: "text", nullable: true),
                ListTitle = table.Column<string>(type: "text", nullable: true),
                NextSync = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastSuccess = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Stale = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OhmywishesConnections", x => x.UserId);
                table.ForeignKey("FK_OhmywishesConnections_Users_UserId", x => x.UserId, "Users", "Id",
                    onDelete: ReferentialAction.Cascade);
            });
        migrationBuilder.CreateIndex("IX_OhmywishesConnections_NextSync", "OhmywishesConnections", "NextSync");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""DELETE FROM "Gifts" WHERE "OhmywishesHidden" AND "OhmywishesKey" IS NOT NULL""");
        migrationBuilder.DropTable("OhmywishesConnections");
        migrationBuilder.DropIndex("IX_Gifts_OwnerId_OhmywishesKey", "Gifts");
        migrationBuilder.CreateIndex("IX_Gifts_OwnerId", "Gifts", "OwnerId");
        migrationBuilder.DropColumn("OhmywishesHidden", "Gifts");
        migrationBuilder.DropColumn("OhmywishesKey", "Gifts");
        migrationBuilder.DropColumn("OhmywishesAttempt", "Users");
    }
}
