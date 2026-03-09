using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Pagedraft.Api.Data;

#nullable disable

namespace Pagedraft.Api.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260308200000_AddDocumentVersionAnalysisResultId")]
    /// <inheritdoc />
    public class AddDocumentVersionAnalysisResultId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AnalysisResultId",
                table: "DocumentVersions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalText",
                table: "DocumentVersions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SuggestedText",
                table: "DocumentVersions",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AnalysisResultId",
                table: "DocumentVersions");

            migrationBuilder.DropColumn(
                name: "OriginalText",
                table: "DocumentVersions");

            migrationBuilder.DropColumn(
                name: "SuggestedText",
                table: "DocumentVersions");
        }
    }
}
