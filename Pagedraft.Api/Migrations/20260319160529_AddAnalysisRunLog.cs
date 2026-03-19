using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pagedraft.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAnalysisRunLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AnalysisRunLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AnalysisResultId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PromptTemplateId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BookId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ChapterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SceneId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Scope = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AnalysisType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ModelName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Language = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    TotalChunks = table.Column<int>(type: "int", nullable: false),
                    SucceededChunks = table.Column<int>(type: "int", nullable: false),
                    FallbackChunks = table.Column<int>(type: "int", nullable: false),
                    InputWordCount = table.Column<int>(type: "int", nullable: false),
                    InputCharCount = table.Column<int>(type: "int", nullable: false),
                    OutputCharCount = table.Column<int>(type: "int", nullable: false),
                    SuggestionCount = table.Column<int>(type: "int", nullable: false),
                    TotalDurationMs = table.Column<long>(type: "bigint", nullable: false),
                    NoChangesHint = table.Column<bool>(type: "bit", nullable: false),
                    ChunkDetailsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnalysisRunLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnalysisRunLogs_AnalysisResults_AnalysisResultId",
                        column: x => x.AnalysisResultId,
                        principalTable: "AnalysisResults",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AnalysisRunLogs_PromptTemplates_PromptTemplateId",
                        column: x => x.PromptTemplateId,
                        principalTable: "PromptTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisRunLogs_AnalysisResultId",
                table: "AnalysisRunLogs",
                column: "AnalysisResultId");

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisRunLogs_JobId",
                table: "AnalysisRunLogs",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisRunLogs_PromptTemplateId",
                table: "AnalysisRunLogs",
                column: "PromptTemplateId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnalysisRunLogs");
        }
    }
}
