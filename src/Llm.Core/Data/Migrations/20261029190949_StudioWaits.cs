using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class StudioWaits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ResumeAt",
                table: "workflow_run_steps",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_run_steps_ResumeAt",
                table: "workflow_run_steps",
                column: "ResumeAt",
                filter: "\"Status\" = 'waiting' AND \"ResumeAt\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_workflow_run_steps_ResumeAt",
                table: "workflow_run_steps");

            migrationBuilder.DropColumn(
                name: "ResumeAt",
                table: "workflow_run_steps");
        }
    }
}
