using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pagedraft.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddBookAiTaskTier : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BookAiTaskTiers",
                columns: table => new
                {
                    BookId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TaskKey = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Tier = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookAiTaskTiers", x => new { x.BookId, x.TaskKey });
                    table.ForeignKey(
                        name: "FK_BookAiTaskTiers_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BookAiTaskTiers");
        }
    }
}
