using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class RiskRegister : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "risks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Category = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Assets = table.Column<string[]>(type: "text[]", nullable: false),
                    Threat = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Likelihood = table.Column<int>(type: "integer", nullable: false),
                    Impact = table.Column<int>(type: "integer", nullable: false),
                    Owner = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Treatment = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Controls = table.Column<string[]>(type: "text[]", nullable: false),
                    Plan = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ResidualLikelihood = table.Column<int>(type: "integer", nullable: true),
                    ResidualImpact = table.Column<int>(type: "integer", nullable: true),
                    AcceptedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    AcceptedWhy = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    AcceptedUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReviewDue = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_risks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "risk_actions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RiskId = table.Column<Guid>(type: "uuid", nullable: false),
                    Text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Owner = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Due = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DoneAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DoneBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_risk_actions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_risk_actions_risks_RiskId",
                        column: x => x.RiskId,
                        principalTable: "risks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "risk_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RiskId = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    By = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_risk_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_risk_events_risks_RiskId",
                        column: x => x.RiskId,
                        principalTable: "risks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_risk_actions_RiskId",
                table: "risk_actions",
                column: "RiskId");

            migrationBuilder.CreateIndex(
                name: "IX_risk_events_RiskId_At",
                table: "risk_events",
                columns: new[] { "RiskId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_risks_ClosedAt_ReviewDue",
                table: "risks",
                columns: new[] { "ClosedAt", "ReviewDue" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "risk_actions");

            migrationBuilder.DropTable(
                name: "risk_events");

            migrationBuilder.DropTable(
                name: "risks");
        }
    }
}
