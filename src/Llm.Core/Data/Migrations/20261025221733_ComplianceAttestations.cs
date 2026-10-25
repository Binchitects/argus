using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ComplianceAttestations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "compliance_attestations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ControlId = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: true),
                    Actor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_compliance_attestations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_compliance_attestations_ControlId_At",
                table: "compliance_attestations",
                columns: new[] { "ControlId", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "compliance_attestations");
        }
    }
}
