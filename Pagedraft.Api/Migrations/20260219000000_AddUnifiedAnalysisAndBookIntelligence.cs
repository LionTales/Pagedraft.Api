using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pagedraft.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddUnifiedAnalysisAndBookIntelligence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AnalysisResult: add unified columns
            migrationBuilder.AddColumn<string>(
                name: "Scope",
                table: "AnalysisResults",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Chapter");

            migrationBuilder.AddColumn<string>(
                name: "AnalysisType",
                table: "AnalysisResults",
                type: "TEXT",
                maxLength: 30,
                nullable: false,
                defaultValue: "Custom");

            migrationBuilder.AddColumn<Guid>(
                name: "SceneId",
                table: "AnalysisResults",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BookId",
                table: "AnalysisResults",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StructuredResult",
                table: "AnalysisResults",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "AnalysisResults",
                type: "TEXT",
                maxLength: 10,
                nullable: false,
                defaultValue: "he");

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisResults_BookId_Scope_AnalysisType",
                table: "AnalysisResults",
                columns: new[] { "BookId", "Scope", "AnalysisType" });

            // ChunkSummaries table
            migrationBuilder.CreateTable(
                name: "ChunkSummaries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BookId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChapterId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SummaryText = table.Column<string>(type: "TEXT", nullable: false),
                    Language = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChunkSummaries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChunkSummaries_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChunkSummaries_Chapters_ChapterId",
                        column: x => x.ChapterId,
                        principalTable: "Chapters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChunkSummaries_BookId_ChapterId",
                table: "ChunkSummaries",
                columns: new[] { "BookId", "ChapterId" },
                unique: true);

            // BookProfiles table
            migrationBuilder.CreateTable(
                name: "BookProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BookId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Genre = table.Column<string>(type: "TEXT", nullable: true),
                    SubGenre = table.Column<string>(type: "TEXT", nullable: true),
                    Synopsis = table.Column<string>(type: "TEXT", nullable: true),
                    TargetAudience = table.Column<string>(type: "TEXT", nullable: true),
                    LiteratureLevel = table.Column<int>(type: "INTEGER", nullable: true),
                    LanguageRegister = table.Column<string>(type: "TEXT", nullable: true),
                    CharactersJson = table.Column<string>(type: "TEXT", nullable: true),
                    StoryStructureJson = table.Column<string>(type: "TEXT", nullable: true),
                    Language = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookProfiles_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BookProfiles_BookId",
                table: "BookProfiles",
                column: "BookId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AnalysisResults_BookId_Scope_AnalysisType",
                table: "AnalysisResults");

            migrationBuilder.DropColumn(
                name: "Scope",
                table: "AnalysisResults");

            migrationBuilder.DropColumn(
                name: "AnalysisType",
                table: "AnalysisResults");

            migrationBuilder.DropColumn(
                name: "SceneId",
                table: "AnalysisResults");

            migrationBuilder.DropColumn(
                name: "BookId",
                table: "AnalysisResults");

            migrationBuilder.DropColumn(
                name: "StructuredResult",
                table: "AnalysisResults");

            migrationBuilder.DropColumn(
                name: "Language",
                table: "AnalysisResults");

            migrationBuilder.DropTable(
                name: "ChunkSummaries");

            migrationBuilder.DropTable(
                name: "BookProfiles");
        }
    }
}
