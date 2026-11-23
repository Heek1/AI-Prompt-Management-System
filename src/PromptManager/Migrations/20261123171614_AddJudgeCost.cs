using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PromptManager.Migrations
{
    /// <inheritdoc />
    public partial class AddJudgeCost : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "JudgeCostUsd",
                table: "EvalRuns",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "JudgeCostUsd",
                table: "EvalRuns");
        }
    }
}
