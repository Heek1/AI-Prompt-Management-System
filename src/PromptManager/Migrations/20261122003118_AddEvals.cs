using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PromptManager.Migrations
{
    /// <inheritdoc />
    public partial class AddEvals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EvalCases",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PromptId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    VariablesJson = table.Column<string>(type: "text", nullable: false),
                    MustContain = table.Column<string>(type: "text", nullable: false),
                    MustNotContain = table.Column<string>(type: "text", nullable: false),
                    Regex = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    JsonSchema = table.Column<string>(type: "text", nullable: true),
                    JudgeRubric = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvalCases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvalCases_Prompts_PromptId",
                        column: x => x.PromptId,
                        principalTable: "Prompts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EvalRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PromptId = table.Column<int>(type: "integer", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    Model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    JudgeModel = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TotalCases = table.Column<int>(type: "integer", nullable: false),
                    CompletedCases = table.Column<int>(type: "integer", nullable: false),
                    PassedCases = table.Column<int>(type: "integer", nullable: false),
                    CostUsd = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true),
                    RunBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RunByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    MayUsePaidModels = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvalRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvalRuns_Prompts_PromptId",
                        column: x => x.PromptId,
                        principalTable: "Prompts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EvalResults",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EvalRunId = table.Column<int>(type: "integer", nullable: false),
                    EvalCaseId = table.Column<int>(type: "integer", nullable: true),
                    CaseName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PromptRunId = table.Column<int>(type: "integer", nullable: true),
                    Passed = table.Column<bool>(type: "boolean", nullable: false),
                    ChecksJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvalResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvalResults_EvalCases_EvalCaseId",
                        column: x => x.EvalCaseId,
                        principalTable: "EvalCases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_EvalResults_EvalRuns_EvalRunId",
                        column: x => x.EvalRunId,
                        principalTable: "EvalRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EvalResults_PromptRuns_PromptRunId",
                        column: x => x.PromptRunId,
                        principalTable: "PromptRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EvalCases_PromptId",
                table: "EvalCases",
                column: "PromptId");

            migrationBuilder.CreateIndex(
                name: "IX_EvalResults_EvalCaseId",
                table: "EvalResults",
                column: "EvalCaseId");

            migrationBuilder.CreateIndex(
                name: "IX_EvalResults_EvalRunId",
                table: "EvalResults",
                column: "EvalRunId");

            migrationBuilder.CreateIndex(
                name: "IX_EvalResults_PromptRunId",
                table: "EvalResults",
                column: "PromptRunId");

            migrationBuilder.CreateIndex(
                name: "IX_EvalRuns_PromptId_Model_VersionNumber",
                table: "EvalRuns",
                columns: new[] { "PromptId", "Model", "VersionNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_EvalRuns_Status",
                table: "EvalRuns",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EvalResults");

            migrationBuilder.DropTable(
                name: "EvalCases");

            migrationBuilder.DropTable(
                name: "EvalRuns");
        }
    }
}
