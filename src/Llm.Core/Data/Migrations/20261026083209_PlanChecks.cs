using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class PlanChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Place",
                table: "plans",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "plans",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "chat");

            migrationBuilder.AddColumn<string>(
                name: "Check",
                table: "plan_steps",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CheckOutput",
                table: "plan_steps",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CheckResult",
                table: "plan_steps",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CheckedAt",
                table: "plan_steps",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "Files",
                table: "plan_steps",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Place",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "Check",
                table: "plan_steps");

            migrationBuilder.DropColumn(
                name: "CheckOutput",
                table: "plan_steps");

            migrationBuilder.DropColumn(
                name: "CheckResult",
                table: "plan_steps");

            migrationBuilder.DropColumn(
                name: "CheckedAt",
                table: "plan_steps");

            migrationBuilder.DropColumn(
                name: "Files",
                table: "plan_steps");
        }
    }
}
