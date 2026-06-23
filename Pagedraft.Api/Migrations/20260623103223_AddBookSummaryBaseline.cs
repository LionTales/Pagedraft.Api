using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pagedraft.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddBookSummaryBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BookSummaryBaselines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Language = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    BookBriefJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    BuiltChapterCount = table.Column<int>(type: "int", nullable: false),
                    BuiltWithModel = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookSummaryBaselines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookSummaryBaselines_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BookSummaryBaselines_BookId_Language",
                table: "BookSummaryBaselines",
                columns: new[] { "BookId", "Language" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BookSummaryBaselines");
        }
    }
}
