using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XafRag.Module.Migrations
{
    /// <inheritdoc />
    public partial class InitialRagSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "knowledge_chunks",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    content = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    embedding = table.Column<SqlVector<float>>(type: "vector(1536)", nullable: true),
                    token_count = table.Column<int>(type: "int", nullable: false),
                    chunk_index = table.Column<int>(type: "int", nullable: false),
                    source_type = table.Column<int>(type: "int", nullable: false),
                    knowledge_article_id = table.Column<int>(type: "int", nullable: true),
                    document_id = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledge_chunks", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_chunks_document_id",
                table: "knowledge_chunks",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_chunks_knowledge_article_id",
                table: "knowledge_chunks",
                column: "knowledge_article_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "knowledge_chunks");
        }
    }
}
