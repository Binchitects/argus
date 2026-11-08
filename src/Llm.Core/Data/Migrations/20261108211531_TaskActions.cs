using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class TaskActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Action",
                table: "scheduled_tasks",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "ask");

            migrationBuilder.AddColumn<bool>(
                name: "Cross",
                table: "scheduled_tasks",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "Ref",
                table: "scheduled_tasks",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "Repositories",
                table: "scheduled_tasks",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<string>(
                name: "Link",
                table: "scheduled_runs",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Action",
                table: "scheduled_tasks");

            migrationBuilder.DropColumn(
                name: "Cross",
                table: "scheduled_tasks");

            migrationBuilder.DropColumn(
                name: "Ref",
                table: "scheduled_tasks");

            migrationBuilder.DropColumn(
                name: "Repositories",
                table: "scheduled_tasks");

            migrationBuilder.DropColumn(
                name: "Link",
                table: "scheduled_runs");
        }
    }
}
