using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AgentStudio : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "workflows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Flow = table.Column<string>(type: "text", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Reach = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    GroupIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false, defaultValueSql: "'{}'::uuid[]"),
                    SecretHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    NextRunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastRunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_workflows_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workflow_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uuid", nullable: false),
                    Flow = table.Column<string>(type: "text", nullable: false),
                    Trigger = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ById = table.Column<Guid>(type: "uuid", nullable: true),
                    Input = table.Column<string>(type: "text", nullable: false),
                    Reply = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_workflow_runs_workflows_WorkflowId",
                        column: x => x.WorkflowId,
                        principalTable: "workflows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workflow_run_steps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    StepId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Input = table.Column<string>(type: "text", nullable: false),
                    Output = table.Column<string>(type: "text", nullable: false),
                    OutputJson = table.Column<string>(type: "text", nullable: true),
                    Link = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_run_steps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_workflow_run_steps_workflow_runs_RunId",
                        column: x => x.RunId,
                        principalTable: "workflow_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_run_steps_RunId_Path",
                table: "workflow_run_steps",
                columns: new[] { "RunId", "Path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_runs_Status",
                table: "workflow_runs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_runs_WorkflowId_StartedAt",
                table: "workflow_runs",
                columns: new[] { "WorkflowId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_workflows_NextRunAt",
                table: "workflows",
                column: "NextRunAt");

            migrationBuilder.CreateIndex(
                name: "IX_workflows_OwnerId",
                table: "workflows",
                column: "OwnerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workflow_run_steps");

            migrationBuilder.DropTable(
                name: "workflow_runs");

            migrationBuilder.DropTable(
                name: "workflows");
        }
    }
}
