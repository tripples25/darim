using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Wishlist.Server.Data;

#nullable disable
namespace Wishlist.Server.Data.Migrations;

[DbContext(typeof(AppDb))]
[Migration("20261106190000_InviteRemaining")]
public partial class InviteRemaining : Migration {
 protected override void Up(MigrationBuilder migrationBuilder) =>
  migrationBuilder.AddColumn<int>(name:"InviteRemaining",table:"Companies",type:"integer",nullable:true);
 protected override void Down(MigrationBuilder migrationBuilder) =>
  migrationBuilder.DropColumn(name:"InviteRemaining",table:"Companies");
}
