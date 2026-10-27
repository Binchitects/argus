using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AiSystems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_systems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Source = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Purpose = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    IntendedUse = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Affected = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Class = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ClassWhy = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    HighRiskArea = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Owner = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Oversight = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Evaluation = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ImpactAssessment = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    ImpactAssessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ImpactAssessedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    InUseSince = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReviewDue = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReviewedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    RetiredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_systems", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_systems_Source",
                table: "ai_systems",
                column: "Source");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_systems");
        }
    }
}
