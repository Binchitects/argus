using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class McpCallTimeout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CallTimeoutMinutes",
                table: "mcp_servers",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CallTimeoutMinutes",
                table: "mcp_servers");
        }
    }
}
