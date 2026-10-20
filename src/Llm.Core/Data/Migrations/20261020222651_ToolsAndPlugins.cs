using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ToolsAndPlugins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "AskFirstFunctions",
                table: "tool_settings",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<int>(
                name: "CallsPerDay",
                table: "tool_settings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "tool_settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "DisabledFunctions",
                table: "tool_settings",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<string>(
                name: "FunctionsJson",
                table: "tool_settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Instructions",
                table: "tool_settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReplaceInstructions",
                table: "tool_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "tool_settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Plugin",
                table: "safeguard_rules",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Plugin",
                table: "projects",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "PluginPermissions",
                table: "mcp_servers",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<bool>(
                name: "PluginPinned",
                table: "mcp_servers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "custom_tools",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Kind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Title = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    ParametersJson = table.Column<string>(type: "text", nullable: false),
                    Method = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    UrlTemplate = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    HeadersEncrypted = table.Column<string>(type: "text", nullable: true),
                    BodyTemplate = table.Column<string>(type: "text", nullable: true),
                    ResultPath = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AskFirst = table.Column<bool>(type: "boolean", nullable: false),
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PromptTemplate = table.Column<string>(type: "text", nullable: true),
                    SystemPrompt = table.Column<string>(type: "text", nullable: true),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_custom_tools", x => x.Id);
                    table.ForeignKey(
                        name: "FK_custom_tools_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "plugin_releases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    FilesJson = table.Column<string>(type: "text", nullable: false),
                    Permissions = table.Column<List<string>>(type: "text[]", nullable: false),
                    InstalledById = table.Column<Guid>(type: "uuid", nullable: true),
                    InstalledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plugin_releases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_plugin_releases_AspNetUsers_InstalledById",
                        column: x => x.InstalledById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_plugin_releases_mcp_servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "mcp_servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tool_calls",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ToolId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Function = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DurationMs = table.Column<int>(type: "integer", nullable: false),
                    Failed = table.Column<bool>(type: "boolean", nullable: false),
                    Error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tool_calls", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tool_calls_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "tool_group_settings",
                columns: table => new
                {
                    ToolId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    OnByDefault = table.Column<bool>(type: "boolean", nullable: true),
                    AskFirst = table.Column<bool>(type: "boolean", nullable: true),
                    CallsPerDay = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tool_group_settings", x => new { x.ToolId, x.GroupId });
                    table.ForeignKey(
                        name: "FK_tool_group_settings_groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "toolsets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Tools = table.Column<List<string>>(type: "text[]", nullable: false),
                    Groups = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    DefaultFor = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    Plugin = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_toolsets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_custom_tools_CreatedById",
                table: "custom_tools",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_custom_tools_Name",
                table: "custom_tools",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_plugin_releases_InstalledById",
                table: "plugin_releases",
                column: "InstalledById");

            migrationBuilder.CreateIndex(
                name: "IX_plugin_releases_ServerId_InstalledAt",
                table: "plugin_releases",
                columns: new[] { "ServerId", "InstalledAt" });

            migrationBuilder.CreateIndex(
                name: "IX_tool_calls_ToolId_At",
                table: "tool_calls",
                columns: new[] { "ToolId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_tool_calls_UserId_ToolId_At",
                table: "tool_calls",
                columns: new[] { "UserId", "ToolId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_tool_group_settings_GroupId",
                table: "tool_group_settings",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_toolsets_Name",
                table: "toolsets",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "custom_tools");

            migrationBuilder.DropTable(
                name: "plugin_releases");

            migrationBuilder.DropTable(
                name: "tool_calls");

            migrationBuilder.DropTable(
                name: "tool_group_settings");

            migrationBuilder.DropTable(
                name: "toolsets");

            migrationBuilder.DropColumn(
                name: "AskFirstFunctions",
                table: "tool_settings");

            migrationBuilder.DropColumn(
                name: "CallsPerDay",
                table: "tool_settings");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "tool_settings");

            migrationBuilder.DropColumn(
                name: "DisabledFunctions",
                table: "tool_settings");

            migrationBuilder.DropColumn(
                name: "FunctionsJson",
                table: "tool_settings");

            migrationBuilder.DropColumn(
                name: "Instructions",
                table: "tool_settings");

            migrationBuilder.DropColumn(
                name: "ReplaceInstructions",
                table: "tool_settings");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "tool_settings");

            migrationBuilder.DropColumn(
                name: "Plugin",
                table: "safeguard_rules");

            migrationBuilder.DropColumn(
                name: "Plugin",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "PluginPermissions",
                table: "mcp_servers");

            migrationBuilder.DropColumn(
                name: "PluginPinned",
                table: "mcp_servers");
        }
    }
}
