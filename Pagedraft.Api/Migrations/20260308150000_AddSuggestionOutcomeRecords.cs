using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pagedraft.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSuggestionOutcomeRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SuggestionOutcomeRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AnalysisResultId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OriginalText = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    SuggestedText = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SuggestionOutcomeRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SuggestionOutcomeRecords_AnalysisResults_AnalysisResultId",
                        column: x => x.AnalysisResultId,
                        principalTable: "AnalysisResults",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SuggestionOutcomeRecords_AnalysisResultId",
                table: "SuggestionOutcomeRecords",
                column: "AnalysisResultId");

            migrationBuilder.CreateIndex(
                name: "IX_SuggestionOutcomeRecords_AnalysisResultId_OriginalText_SuggestedText",
                table: "SuggestionOutcomeRecords",
                columns: new[] { "AnalysisResultId", "OriginalText", "SuggestedText" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SuggestionOutcomeRecords");
        }
    }
}
