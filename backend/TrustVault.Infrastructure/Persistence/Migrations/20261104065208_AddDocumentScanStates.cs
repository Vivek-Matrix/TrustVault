using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrustVault.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentScanStates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastScanAtUtc",
                table: "documents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScanAttemptCount",
                table: "documents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ScanStatus",
                table: "documents",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "PENDINGSCAN");

            migrationBuilder.AddColumn<DateTime>(
                name: "LastScanAtUtc",
                table: "document_versions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScanAttemptCount",
                table: "document_versions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ScanStatus",
                table: "document_versions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "PENDINGSCAN");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastScanAtUtc",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "ScanAttemptCount",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "ScanStatus",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "LastScanAtUtc",
                table: "document_versions");

            migrationBuilder.DropColumn(
                name: "ScanAttemptCount",
                table: "document_versions");

            migrationBuilder.DropColumn(
                name: "ScanStatus",
                table: "document_versions");
        }
    }
}
