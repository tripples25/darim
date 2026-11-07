using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace Wishlist.Server.Data.Migrations;

[DbContext(typeof(AppDb))]
[Migration("20261107130000_OhmywishesWebPilot")]
public sealed class OhmywishesWebPilot : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<bool>("WebApi", "OhmywishesConnections", type: "boolean", nullable: false, defaultValue: false);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE "Gifts" SET "OhmywishesHidden" = TRUE
            WHERE "OhmywishesKey" IS NOT NULL AND "OwnerId" IN
                (SELECT "UserId" FROM "OhmywishesConnections" WHERE "WebApi");
            DELETE FROM "OhmywishesConnections" WHERE "WebApi";
            """);
        migrationBuilder.DropColumn("WebApi", "OhmywishesConnections");
    }
}
