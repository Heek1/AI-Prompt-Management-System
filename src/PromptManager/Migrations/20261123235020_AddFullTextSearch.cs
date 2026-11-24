using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace PromptManager.Migrations
{
    /// <inheritdoc />
    public partial class AddFullTextSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "SearchVector",
                table: "Prompts",
                type: "tsvector",
                nullable: true,
                computedColumnSql: "setweight(to_tsvector('simple', coalesce(\"Title\", '')), 'A') || setweight(to_tsvector('simple', coalesce(\"Description\", '')), 'B') || setweight(to_tsvector('simple', coalesce(\"CurrentText\", '')), 'C')",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "IX_Prompts_SearchVector",
                table: "Prompts",
                column: "SearchVector")
                .Annotation("Npgsql:IndexMethod", "GIN");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Prompts_SearchVector",
                table: "Prompts");

            migrationBuilder.DropColumn(
                name: "SearchVector",
                table: "Prompts");
        }
    }
}
