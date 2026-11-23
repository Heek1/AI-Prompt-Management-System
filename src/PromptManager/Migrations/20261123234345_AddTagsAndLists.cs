using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace PromptManager.Migrations
{
    /// <inheritdoc />
    public partial class AddTagsAndLists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LookupValues",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LookupValues", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Slug = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tags", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PromptTags",
                columns: table => new
                {
                    PromptId = table.Column<int>(type: "integer", nullable: false),
                    TagId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PromptTags", x => new { x.PromptId, x.TagId });
                    table.ForeignKey(
                        name: "FK_PromptTags_Prompts_PromptId",
                        column: x => x.PromptId,
                        principalTable: "Prompts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PromptTags_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "LookupValues",
                columns: new[] { "Id", "Kind", "Name" },
                values: new object[,]
                {
                    { 1, "Category", "Code Generation" },
                    { 2, "Category", "Bug Fixing" },
                    { 3, "Category", "Testing" },
                    { 4, "Category", "Documentation" },
                    { 5, "Category", "UI Design" },
                    { 6, "Category", "Business Analysis" },
                    { 7, "Category", "Learning" },
                    { 8, "Tool", "ChatGPT" },
                    { 9, "Tool", "Copilot" },
                    { 10, "Tool", "Claude" },
                    { 11, "Tool", "Gemini" },
                    { 12, "Tool", "Grok" },
                    { 13, "Tool", "Mistral" }
                });

            // The seed rows carry explicit ids, which do not advance the identity
            // sequence; move it past them so the first admin-added entry gets 14.
            migrationBuilder.Sql(
                "SELECT setval(pg_get_serial_sequence('\"LookupValues\"', 'Id'), (SELECT MAX(\"Id\") FROM \"LookupValues\"));");

            migrationBuilder.CreateIndex(
                name: "IX_LookupValues_Kind_Name",
                table: "LookupValues",
                columns: new[] { "Kind", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PromptTags_TagId",
                table: "PromptTags",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_Tags_Slug",
                table: "Tags",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LookupValues");

            migrationBuilder.DropTable(
                name: "PromptTags");

            migrationBuilder.DropTable(
                name: "Tags");
        }
    }
}
