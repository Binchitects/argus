using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class RateLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RequestsPerMinute",
                table: "groups",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TokensPerMinute",
                table: "groups",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RequestsPerMinute",
                table: "AspNetUsers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TokensPerMinute",
                table: "AspNetUsers",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequestsPerMinute",
                table: "groups");

            migrationBuilder.DropColumn(
                name: "TokensPerMinute",
                table: "groups");

            migrationBuilder.DropColumn(
                name: "RequestsPerMinute",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "TokensPerMinute",
                table: "AspNetUsers");
        }
    }
}
