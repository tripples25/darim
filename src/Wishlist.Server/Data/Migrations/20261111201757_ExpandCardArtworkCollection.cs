using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wishlist.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExpandCardArtworkCollection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_CardArtwork",
                table: "CardArtwork");

            migrationBuilder.RenameColumn(
                name: "Slot",
                table: "CardArtwork",
                newName: "Category");

            migrationBuilder.Sql("""
                UPDATE "CardArtwork"
                SET "Category" = CASE
                    WHEN "Category" LIKE 'background-light%' THEN 'light'
                    WHEN "Category" LIKE 'background-dark%' THEN 'dark'
                    WHEN "Category" LIKE 'overlay-%' THEN 'overlay'
                    ELSE "Category"
                END;
                """);

            migrationBuilder.RenameColumn(
                name: "Version",
                table: "CardArtwork",
                newName: "Id");

            migrationBuilder.AlterColumn<byte[]>(
                name: "Data",
                table: "CardArtwork",
                type: "bytea",
                nullable: true,
                oldClrType: typeof(byte[]),
                oldType: "bytea");

            migrationBuilder.AddColumn<string>(
                name: "BuiltIn",
                table: "CardArtwork",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Category",
                table: "CardArtwork",
                type: "character varying(7)",
                maxLength: 7,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);

            migrationBuilder.AddPrimaryKey(
                name: "PK_CardArtwork",
                table: "CardArtwork",
                column: "Id");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CardArtwork_Category",
                table: "CardArtwork",
                sql: "\"Category\" IN ('light','dark','overlay')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CardArtwork_Source",
                table: "CardArtwork",
                sql: "(\"BuiltIn\" IS NULL) <> (\"Data\" IS NULL)");

            migrationBuilder.Sql("""
                INSERT INTO "CardArtwork" ("Id", "Category", "BuiltIn") VALUES
                    ('00000000-0000-0000-0000-000000000001', 'dark', 'background-dark'),
                    ('00000000-0000-0000-0000-000000000002', 'dark', 'background-dark-1'),
                    ('00000000-0000-0000-0000-000000000003', 'dark', 'background-dark-2'),
                    ('00000000-0000-0000-0000-000000000004', 'dark', 'background-dark-3'),
                    ('00000000-0000-0000-0000-000000000005', 'light', 'background-light'),
                    ('00000000-0000-0000-0000-000000000006', 'light', 'background-light-1'),
                    ('00000000-0000-0000-0000-000000000007', 'light', 'background-light-2'),
                    ('00000000-0000-0000-0000-000000000008', 'light', 'background-light-3'),
                    ('00000000-0000-0000-0000-000000000009', 'overlay', 'overlay-1'),
                    ('00000000-0000-0000-0000-000000000010', 'overlay', 'overlay-2'),
                    ('00000000-0000-0000-0000-000000000011', 'overlay', 'overlay-3'),
                    ('00000000-0000-0000-0000-000000000012', 'overlay', 'overlay-4');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM "CardArtwork" WHERE "BuiltIn" IS NULL
                        GROUP BY "Category" HAVING count(*) > 4
                    ) THEN
                        RAISE EXCEPTION 'Cannot roll back card artwork: the previous schema supports four uploads per category.';
                    END IF;
                END $$;
                DELETE FROM "CardArtwork" WHERE "BuiltIn" IS NOT NULL;
                """);

            migrationBuilder.DropPrimaryKey(
                name: "PK_CardArtwork",
                table: "CardArtwork");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CardArtwork_Category",
                table: "CardArtwork");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CardArtwork_Source",
                table: "CardArtwork");

            migrationBuilder.DropColumn(
                name: "BuiltIn",
                table: "CardArtwork");

            migrationBuilder.RenameColumn(
                name: "Category",
                table: "CardArtwork",
                newName: "Slot");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "CardArtwork",
                newName: "Version");

            migrationBuilder.AlterColumn<byte[]>(
                name: "Data",
                table: "CardArtwork",
                type: "bytea",
                nullable: false,
                oldClrType: typeof(byte[]),
                oldType: "bytea",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Slot",
                table: "CardArtwork",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(7)",
                oldMaxLength: 7);

            migrationBuilder.Sql("""
                WITH slots AS (
                    SELECT "Version", "Slot" AS category,
                        row_number() OVER (PARTITION BY "Slot" ORDER BY "Version") AS position
                    FROM "CardArtwork"
                )
                UPDATE "CardArtwork" AS artwork
                SET "Slot" = CASE
                    WHEN slots.category = 'overlay' THEN 'overlay-' || slots.position
                    WHEN slots.position = 1 THEN 'background-' || slots.category
                    ELSE 'background-' || slots.category || '-' || (slots.position - 1)
                END
                FROM slots
                WHERE artwork."Version" = slots."Version";
                """);

            migrationBuilder.AddPrimaryKey(
                name: "PK_CardArtwork",
                table: "CardArtwork",
                column: "Slot");
        }
    }
}
