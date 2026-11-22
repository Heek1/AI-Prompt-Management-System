using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PromptManager.Migrations
{
    /// <inheritdoc />
    public partial class AddVersionStatuses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PromotedAt",
                table: "PromptVersions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PromotedBy",
                table: "PromptVersions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PromotedOverRegression",
                table: "PromptVersions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "PromptVersions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Draft");

            migrationBuilder.AddColumn<int>(
                name: "ProductionVersion",
                table: "Prompts",
                type: "integer",
                nullable: true);

            // Until now the newest version was the one everybody used, so it
            // becomes the production version of every existing prompt.
            migrationBuilder.Sql("""
                UPDATE "Prompts" SET "ProductionVersion" = "CurrentVersion";
                UPDATE "PromptVersions" v SET "Status" = 'Production', "PromotedAt" = p."UpdatedAt", "PromotedBy" = p."Author"
                FROM "Prompts" p
                WHERE v."PromptId" = p."Id" AND v."VersionNumber" = p."CurrentVersion";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PromotedAt",
                table: "PromptVersions");

            migrationBuilder.DropColumn(
                name: "PromotedBy",
                table: "PromptVersions");

            migrationBuilder.DropColumn(
                name: "PromotedOverRegression",
                table: "PromptVersions");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "PromptVersions");

            migrationBuilder.DropColumn(
                name: "ProductionVersion",
                table: "Prompts");
        }
    }
}
