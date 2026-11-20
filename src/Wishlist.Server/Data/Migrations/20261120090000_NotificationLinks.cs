using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Wishlist.Server.Data.Migrations;

[DbContext(typeof(AppDb))]
[Migration("20261120090000_NotificationLinks")]
public class NotificationLinks : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>("GiftId", "Notices", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<Guid>("PersonId", "Notices", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<bool>("OpenOwnerBoard", "Notices", type: "boolean", defaultValue: false);
        migrationBuilder.AddColumn<bool>("IncludesSplit", "Notices", type: "boolean", defaultValue: false);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("GiftId", "Notices");
        migrationBuilder.DropColumn("PersonId", "Notices");
        migrationBuilder.DropColumn("OpenOwnerBoard", "Notices");
        migrationBuilder.DropColumn("IncludesSplit", "Notices");
    }
}
