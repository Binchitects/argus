using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class RunningTheSystem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "control_owners",
                columns: table => new
                {
                    ControlId = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Owner = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SetBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SetAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_control_owners", x => x.ControlId);
                });

            migrationBuilder.CreateTable(
                name: "evidence_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Clause = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RequestedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Answer = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    AnsweredBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    AnsweredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evidence_requests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "management_reviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HeldAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Attendees = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Inputs = table.Column<string>(type: "character varying(100000)", maxLength: 100000, nullable: false),
                    Minutes = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    Decisions = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_management_reviews", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "security_objectives",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Check = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Target = table.Column<int>(type: "integer", nullable: false),
                    Owner = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_objectives", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_evidence_requests_RequestedAt",
                table: "evidence_requests",
                column: "RequestedAt");

            migrationBuilder.CreateIndex(
                name: "IX_management_reviews_HeldAt",
                table: "management_reviews",
                column: "HeldAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "control_owners");

            migrationBuilder.DropTable(
                name: "evidence_requests");

            migrationBuilder.DropTable(
                name: "management_reviews");

            migrationBuilder.DropTable(
                name: "security_objectives");
        }
    }
}
