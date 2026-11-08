using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Wishlist.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class MultiCompanyWishlists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DefaultCompanyId",
                table: "OhmywishesConnections",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "CompanyId",
                table: "Gifts",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<bool>(
                name: "AllCompanies",
                table: "Gifts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "GiftPublications",
                columns: table => new
                {
                    GiftId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GiftPublications", x => new { x.GiftId, x.CompanyId });
                    table.ForeignKey(
                        name: "FK_GiftPublications_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GiftPublications_Gifts_GiftId",
                        column: x => x.GiftId,
                        principalTable: "Gifts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Memberships",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Memberships", x => new { x.UserId, x.CompanyId });
                    table.ForeignKey(
                        name: "FK_Memberships_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Memberships_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WishlistLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: true),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Label = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Created = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Revoked = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WishlistLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WishlistLinks_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WishlistLinks_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WishlistReservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkId = table.Column<Guid>(type: "uuid", nullable: false),
                    GiftId = table.Column<Guid>(type: "uuid", nullable: false),
                    GuestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Created = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    Due = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ShareUsername = table.Column<bool>(type: "boolean", nullable: false),
                    InterestRecipientId = table.Column<Guid>(type: "uuid", nullable: true),
                    Notified = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WishlistReservations", x => x.Id);
                    table.CheckConstraint("CK_WishlistReservation_Status", " \"Status\" IN ('waiting','held','confirmed','closed') ");
                    table.ForeignKey(
                        name: "FK_WishlistReservations_Gifts_GiftId",
                        column: x => x.GiftId,
                        principalTable: "Gifts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WishlistReservations_Users_GuestId",
                        column: x => x.GuestId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WishlistReservations_WishlistLinks_LinkId",
                        column: x => x.LinkId,
                        principalTable: "WishlistLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OhmywishesConnections_DefaultCompanyId",
                table: "OhmywishesConnections",
                column: "DefaultCompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_GiftPublications_CompanyId",
                table: "GiftPublications",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_Memberships_CompanyId",
                table: "Memberships",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_WishlistLinks_CompanyId",
                table: "WishlistLinks",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_WishlistLinks_OwnerId_Created",
                table: "WishlistLinks",
                columns: new[] { "OwnerId", "Created" });

            migrationBuilder.CreateIndex(
                name: "IX_WishlistLinks_TokenHash",
                table: "WishlistLinks",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WishlistReservations_GiftId",
                table: "WishlistReservations",
                column: "GiftId",
                unique: true,
                filter: " \"Status\" = 'held' ");

            migrationBuilder.CreateIndex(
                name: "IX_WishlistReservations_GiftId_GuestId",
                table: "WishlistReservations",
                columns: new[] { "GiftId", "GuestId" },
                unique: true,
                filter: " \"Status\" <> 'closed' ");

            migrationBuilder.CreateIndex(
                name: "IX_WishlistReservations_GiftId_Sequence",
                table: "WishlistReservations",
                columns: new[] { "GiftId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_WishlistReservations_GuestId",
                table: "WishlistReservations",
                column: "GuestId");

            migrationBuilder.CreateIndex(
                name: "IX_WishlistReservations_LinkId_Status",
                table: "WishlistReservations",
                columns: new[] { "LinkId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_WishlistReservations_Status_Due",
                table: "WishlistReservations",
                columns: new[] { "Status", "Due" });

            migrationBuilder.AddForeignKey(
                name: "FK_OhmywishesConnections_Companies_DefaultCompanyId",
                table: "OhmywishesConnections",
                column: "DefaultCompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.Sql("""
                INSERT INTO "Memberships" ("UserId", "CompanyId")
                SELECT "Id", "CompanyId" FROM "Users" WHERE "CompanyId" IS NOT NULL;

                INSERT INTO "GiftPublications" ("GiftId", "CompanyId")
                SELECT "Id", "CompanyId" FROM "Gifts" WHERE "CompanyId" IS NOT NULL;

                UPDATE "OhmywishesConnections" AS connection
                SET "DefaultCompanyId" = owner."CompanyId"
                FROM "Users" AS owner WHERE connection."UserId" = owner."Id";

                UPDATE "Gifts" SET "CompanyId" = NULL WHERE "Mode" <> 'split';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            throw new NotSupportedException("Multi-company data cannot be represented by the previous schema.");
    }
}
