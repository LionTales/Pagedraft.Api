using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pagedraft.Api.Migrations
{
    /// <inheritdoc />
    public partial class AnalysisRunLogAnalysisResult_SetNullOnDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AnalysisRunLogs_AnalysisResults_AnalysisResultId",
                table: "AnalysisRunLogs");

            migrationBuilder.AddForeignKey(
                name: "FK_AnalysisRunLogs_AnalysisResults_AnalysisResultId",
                table: "AnalysisRunLogs",
                column: "AnalysisResultId",
                principalTable: "AnalysisResults",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AnalysisRunLogs_AnalysisResults_AnalysisResultId",
                table: "AnalysisRunLogs");

            migrationBuilder.AddForeignKey(
                name: "FK_AnalysisRunLogs_AnalysisResults_AnalysisResultId",
                table: "AnalysisRunLogs",
                column: "AnalysisResultId",
                principalTable: "AnalysisResults",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
