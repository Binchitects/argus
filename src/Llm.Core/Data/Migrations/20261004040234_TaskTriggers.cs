using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class TaskTriggers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "Events",
                table: "scheduled_tasks",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<bool>(
                name: "ReplyInGitLab",
                table: "scheduled_tasks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Trigger",
                table: "scheduled_tasks",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "schedule");

            migrationBuilder.AddColumn<string>(
                name: "TriggerSecretHash",
                table: "scheduled_tasks",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Events",
                table: "scheduled_tasks");

            migrationBuilder.DropColumn(
                name: "ReplyInGitLab",
                table: "scheduled_tasks");

            migrationBuilder.DropColumn(
                name: "Trigger",
                table: "scheduled_tasks");

            migrationBuilder.DropColumn(
                name: "TriggerSecretHash",
                table: "scheduled_tasks");
        }
    }
}
