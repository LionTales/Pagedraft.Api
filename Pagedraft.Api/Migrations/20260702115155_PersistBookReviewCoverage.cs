using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pagedraft.Api.Migrations
{
    /// <inheritdoc />
    public partial class PersistBookReviewCoverage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BookReviewCoverages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Language = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    ChaptersReviewed = table.Column<int>(type: "int", nullable: false),
                    ChaptersTotal = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookReviewCoverages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookReviewCoverages_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BookReviewCoverages_BookId_Language",
                table: "BookReviewCoverages",
                columns: new[] { "BookId", "Language" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BookReviewCoverages");
        }
    }
}
