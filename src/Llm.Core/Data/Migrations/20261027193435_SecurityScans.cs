using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class SecurityScans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "security_scans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Project = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Ref = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Step = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Candidates = table.Column<int>(type: "integer", nullable: false),
                    Read = table.Column<int>(type: "integer", nullable: false),
                    Dropped = table.Column<int>(type: "integer", nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: true),
                    PromptTokens = table.Column<int>(type: "integer", nullable: false),
                    CompletionTokens = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_scans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_security_scans_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "security_findings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScanId = table.Column<Guid>(type: "uuid", nullable: false),
                    Path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Line = table.Column<int>(type: "integer", nullable: false),
                    SinkCode = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Cwe = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CweTitle = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Source = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Flow = table.Column<string>(type: "text", nullable: false),
                    Exploit = table.Column<string>(type: "text", nullable: false),
                    Fix = table.Column<string>(type: "text", nullable: false),
                    Pages = table.Column<string>(type: "text", nullable: false),
                    Verified = table.Column<bool>(type: "boolean", nullable: true),
                    VerifyNote = table.Column<string>(type: "text", nullable: true),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DecidedById = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Why = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_findings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_security_findings_AspNetUsers_DecidedById",
                        column: x => x.DecidedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_security_findings_security_scans_ScanId",
                        column: x => x.ScanId,
                        principalTable: "security_scans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_security_findings_DecidedById",
                table: "security_findings",
                column: "DecidedById");

            migrationBuilder.CreateIndex(
                name: "IX_security_findings_ScanId_Severity",
                table: "security_findings",
                columns: new[] { "ScanId", "Severity" });

            migrationBuilder.CreateIndex(
                name: "IX_security_scans_Project_CreatedAt",
                table: "security_scans",
                columns: new[] { "Project", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_security_scans_UserId_CreatedAt",
                table: "security_scans",
                columns: new[] { "UserId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "security_findings");

            migrationBuilder.DropTable(
                name: "security_scans");
        }
    }
}
