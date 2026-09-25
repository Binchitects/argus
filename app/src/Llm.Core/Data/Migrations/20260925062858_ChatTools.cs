using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChatTools : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "Tools",
                table: "conversations",
                type: "text[]",
                nullable: true);

            // A chat that had Argus off had no tools at all: keep it that way.
            // (Null means "the tools that are on in new chats".)
            migrationBuilder.Sql("""UPDATE conversations SET "Tools" = '{}' WHERE NOT "UseArgus";""");

            migrationBuilder.DropColumn(
                name: "UseArgus",
                table: "conversations");

            migrationBuilder.CreateTable(
                name: "mcp_servers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    HeaderName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    HeaderValueEncrypted = table.Column<string>(type: "text", nullable: true),
                    EmailHeader = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mcp_servers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tool_settings",
                columns: table => new
                {
                    ToolId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Audience = table.Column<int>(type: "integer", nullable: false),
                    Groups = table.Column<List<Guid>>(type: "uuid[]", nullable: false, defaultValueSql: "'{}'::uuid[]"),
                    OnByDefault = table.Column<bool>(type: "boolean", nullable: false),
                    AskFirst = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tool_settings", x => x.ToolId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_mcp_servers_Name",
                table: "mcp_servers",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mcp_servers");

            migrationBuilder.DropTable(
                name: "tool_settings");

            migrationBuilder.DropColumn(
                name: "Tools",
                table: "conversations");

            migrationBuilder.AddColumn<bool>(
                name: "UseArgus",
                table: "conversations",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
