using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScaleOut : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RunningOn",
                table: "scheduled_tasks",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RunningSeenAt",
                table: "scheduled_tasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Priority",
                table: "groups",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "task_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    ReplyProject = table.Column<long>(type: "bigint", nullable: true),
                    ReplyKind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ReplyId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_task_events_scheduled_tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "scheduled_tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_task_events_TaskId_CreatedAt",
                table: "task_events",
                columns: new[] { "TaskId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "task_events");

            migrationBuilder.DropColumn(
                name: "RunningOn",
                table: "scheduled_tasks");

            migrationBuilder.DropColumn(
                name: "RunningSeenAt",
                table: "scheduled_tasks");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "groups");
        }
    }
}
