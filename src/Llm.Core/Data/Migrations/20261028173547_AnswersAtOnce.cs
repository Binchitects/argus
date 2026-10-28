using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AnswersAtOnce : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AnswersAtOnce",
                table: "groups",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AnswersAtOnce",
                table: "AspNetUsers",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AnswersAtOnce",
                table: "groups");

            migrationBuilder.DropColumn(
                name: "AnswersAtOnce",
                table: "AspNetUsers");
        }
    }
}
