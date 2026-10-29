using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class PromptLogStates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DurationMs",
                table: "prompt_log",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Error",
                table: "prompt_log",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "State",
                table: "prompt_log",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "done");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DurationMs",
                table: "prompt_log");

            migrationBuilder.DropColumn(
                name: "Error",
                table: "prompt_log");

            migrationBuilder.DropColumn(
                name: "State",
                table: "prompt_log");
        }
    }
}
