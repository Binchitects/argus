using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class SecurityScanners : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Commit",
                table: "security_scans",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Scanners",
                table: "security_scans",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Commit",
                table: "security_findings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string[]>(
                name: "FoundBy",
                table: "security_findings",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<string>(
                name: "Rule",
                table: "security_findings",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Commit",
                table: "security_scans");

            migrationBuilder.DropColumn(
                name: "Scanners",
                table: "security_scans");

            migrationBuilder.DropColumn(
                name: "Commit",
                table: "security_findings");

            migrationBuilder.DropColumn(
                name: "FoundBy",
                table: "security_findings");

            migrationBuilder.DropColumn(
                name: "Rule",
                table: "security_findings");
        }
    }
}
