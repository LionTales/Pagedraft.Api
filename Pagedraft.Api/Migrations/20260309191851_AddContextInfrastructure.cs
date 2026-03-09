using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pagedraft.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddContextInfrastructure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StructuredJson",
                table: "ChunkSummaries",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BookBibles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StyleProfileJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CharacterRegisterJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ThemesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TimelineJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    WorldBuildingJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookBibles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookBibles_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SceneEmbeddings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SceneId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChapterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmbeddingVector = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Dimensions = table.Column<int>(type: "int", nullable: false),
                    ModelName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SceneEmbeddings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SceneEmbeddings_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SceneEmbeddings_Chapters_ChapterId",
                        column: x => x.ChapterId,
                        principalTable: "Chapters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SceneEmbeddings_Scenes_SceneId",
                        column: x => x.SceneId,
                        principalTable: "Scenes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BookBibles_BookId",
                table: "BookBibles",
                column: "BookId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SceneEmbeddings_BookId",
                table: "SceneEmbeddings",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_SceneEmbeddings_ChapterId",
                table: "SceneEmbeddings",
                column: "ChapterId");

            migrationBuilder.CreateIndex(
                name: "IX_SceneEmbeddings_SceneId",
                table: "SceneEmbeddings",
                column: "SceneId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BookBibles");

            migrationBuilder.DropTable(
                name: "SceneEmbeddings");

            migrationBuilder.DropColumn(
                name: "StructuredJson",
                table: "ChunkSummaries");
        }
    }
}
