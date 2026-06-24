using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pagedraft.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddBookFindingLanguageToDedupIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BookFindings_BookId_DedupKey",
                table: "BookFindings");

            migrationBuilder.CreateIndex(
                name: "IX_BookFindings_BookId_Language_DedupKey",
                table: "BookFindings",
                columns: new[] { "BookId", "Language", "DedupKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BookFindings_BookId_Language_DedupKey",
                table: "BookFindings");

            migrationBuilder.CreateIndex(
                name: "IX_BookFindings_BookId_DedupKey",
                table: "BookFindings",
                columns: new[] { "BookId", "DedupKey" },
                unique: true);
        }
    }
}
