using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class BuiltInSkills : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "BuiltIn",
                table: "skills",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Off",
                table: "skills",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_skills_Name_BuiltIn",
                table: "skills",
                column: "Name",
                unique: true,
                filter: "\"BuiltIn\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_skills_Name_BuiltIn",
                table: "skills");

            migrationBuilder.DropColumn(
                name: "BuiltIn",
                table: "skills");

            migrationBuilder.DropColumn(
                name: "Off",
                table: "skills");
        }
    }
}
