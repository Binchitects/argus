using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class SecurityRecheck : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RecheckedAt",
                table: "security_scans",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FoundAt",
                table: "security_findings",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            // Findings made before: found when their review ended.
            migrationBuilder.Sql("""UPDATE security_findings f SET "FoundAt" = COALESCE(s."FinishedAt", s."CreatedAt") FROM security_scans s WHERE f."ScanId" = s."Id";""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RecheckedAt",
                table: "security_scans");

            migrationBuilder.DropColumn(
                name: "FoundAt",
                table: "security_findings");
        }
    }
}
