using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class Plugins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Manifest",
                table: "mcp_servers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PersonAuth",
                table: "mcp_servers",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Plugin",
                table: "mcp_servers",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PluginSettings",
                table: "mcp_servers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PluginVersion",
                table: "mcp_servers",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "Writes",
                table: "mcp_servers",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.CreateTable(
                name: "person_credentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToolId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SecretEncrypted = table.Column<string>(type: "text", nullable: false),
                    RefreshEncrypted = table.Column<string>(type: "text", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Account = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_person_credentials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_person_credentials_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_person_credentials_UserId_ToolId",
                table: "person_credentials",
                columns: new[] { "UserId", "ToolId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "person_credentials");

            migrationBuilder.DropColumn(
                name: "Manifest",
                table: "mcp_servers");

            migrationBuilder.DropColumn(
                name: "PersonAuth",
                table: "mcp_servers");

            migrationBuilder.DropColumn(
                name: "Plugin",
                table: "mcp_servers");

            migrationBuilder.DropColumn(
                name: "PluginSettings",
                table: "mcp_servers");

            migrationBuilder.DropColumn(
                name: "PluginVersion",
                table: "mcp_servers");

            migrationBuilder.DropColumn(
                name: "Writes",
                table: "mcp_servers");
        }
    }
}
