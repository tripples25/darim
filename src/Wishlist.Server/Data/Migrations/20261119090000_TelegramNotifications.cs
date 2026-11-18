using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Wishlist.Server.Data.Migrations;

[DbContext(typeof(AppDb))]
[Migration("20261119090000_TelegramNotifications")]
public class TelegramNotifications : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateOnly>("BirthDate", "Users", type: "date", nullable: true);
        migrationBuilder.AddColumn<int>("BirthdayReminderYear", "Users", type: "integer", nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>("TelegramNextAttempt", "Notices", type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<int>("TelegramAttempts", "Notices", type: "integer", nullable: false, defaultValue: 0);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("BirthDate", "Users");
        migrationBuilder.DropColumn("BirthdayReminderYear", "Users");
        migrationBuilder.DropColumn("TelegramNextAttempt", "Notices");
        migrationBuilder.DropColumn("TelegramAttempts", "Notices");
    }
}
