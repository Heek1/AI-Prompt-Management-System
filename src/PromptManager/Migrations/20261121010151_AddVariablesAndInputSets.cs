using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PromptManager.Migrations
{
    /// <inheritdoc />
    public partial class AddVariablesAndInputSets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "VariablesJson",
                table: "PromptRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PromptInputSets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PromptId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ValuesJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PromptInputSets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PromptInputSets_Prompts_PromptId",
                        column: x => x.PromptId,
                        principalTable: "Prompts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PromptInputSets_PromptId_Name",
                table: "PromptInputSets",
                columns: new[] { "PromptId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PromptInputSets");

            migrationBuilder.DropColumn(
                name: "VariablesJson",
                table: "PromptRuns");
        }
    }
}
