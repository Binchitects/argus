using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <summary>
    /// Code Arena's goal (goal mode) kept with the chat its session syncs to, one per chat; and the skills an assistant
    /// uses, none for those made before.
    /// </summary>
    public partial class CodeArenaGoals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "Skills",
                table: "projects",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.CreateTable(
                name: "chat_goals",
                columns: table => new
                {
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    CheckKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    CheckText = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CheckBy = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    BudgetSteps = table.Column<int>(type: "integer", nullable: false),
                    BudgetMinutes = table.Column<int>(type: "integer", nullable: false),
                    BudgetTokens = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Turns = table.Column<int>(type: "integer", nullable: false),
                    Steps = table.Column<int>(type: "integer", nullable: false),
                    Tokens = table.Column<long>(type: "bigint", nullable: false),
                    WorkedMs = table.Column<long>(type: "bigint", nullable: false),
                    Passed = table.Column<bool>(type: "boolean", nullable: true),
                    Said = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CheckedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_goals", x => x.ConversationId);
                    table.ForeignKey(
                        name: "FK_chat_goals_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_chat_goals_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_chat_goals_UserId",
                table: "chat_goals",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_goals");

            migrationBuilder.DropColumn(
                name: "Skills",
                table: "projects");
        }
    }
}
