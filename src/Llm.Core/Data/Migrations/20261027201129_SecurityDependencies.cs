using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class SecurityDependencies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "AdvisoriesAsOf",
                table: "security_scans",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Dependencies",
                table: "security_scans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Sbom",
                table: "security_scans",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Exploited",
                table: "security_findings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "security_findings",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValueSql: "'code'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdvisoriesAsOf",
                table: "security_scans");

            migrationBuilder.DropColumn(
                name: "Dependencies",
                table: "security_scans");

            migrationBuilder.DropColumn(
                name: "Sbom",
                table: "security_scans");

            migrationBuilder.DropColumn(
                name: "Exploited",
                table: "security_findings");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "security_findings");
        }
    }
}
