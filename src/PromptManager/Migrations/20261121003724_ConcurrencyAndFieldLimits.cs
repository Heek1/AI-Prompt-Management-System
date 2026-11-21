using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PromptManager.Migrations
{
    /// <inheritdoc />
    public partial class ConcurrencyAndFieldLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PromptVersions_PromptId",
                table: "PromptVersions");

            migrationBuilder.AddColumn<string>(
                name: "ConcurrencyStamp",
                table: "Prompts",
                type: "TEXT",
                maxLength: 36,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_PromptVersions_PromptId_VersionNumber",
                table: "PromptVersions",
                columns: new[] { "PromptId", "VersionNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PromptVersions_PromptId_VersionNumber",
                table: "PromptVersions");

            migrationBuilder.DropColumn(
                name: "ConcurrencyStamp",
                table: "Prompts");

            migrationBuilder.CreateIndex(
                name: "IX_PromptVersions_PromptId",
                table: "PromptVersions",
                column: "PromptId");
        }
    }
}
