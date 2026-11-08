using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class SubAgentsAtOnce : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SubAgentsAtOnce",
                table: "groups",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SubAgentsAtOnce",
                table: "AspNetUsers",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SubAgentsAtOnce",
                table: "groups");

            migrationBuilder.DropColumn(
                name: "SubAgentsAtOnce",
                table: "AspNetUsers");
        }
    }
}
