using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class FeedbackAndArena : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "answer_feedback",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Up = table.Column<bool>(type: "boolean", nullable: false),
                    Reason = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    Comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Shared = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_answer_feedback", x => x.Id);
                    table.ForeignKey(
                        name: "FK_answer_feedback_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_answer_feedback_chat_messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "chat_messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "arena_matches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    QuestionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AnswerA = table.Column<Guid>(type: "uuid", nullable: true),
                    AnswerB = table.Column<Guid>(type: "uuid", nullable: true),
                    ModelA = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ModelB = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Vote = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    VotedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_arena_matches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_arena_matches_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_arena_matches_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_chat_messages_CreatedAt",
                table: "chat_messages",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_answer_feedback_ConversationId",
                table: "answer_feedback",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_answer_feedback_MessageId_UserId",
                table: "answer_feedback",
                columns: new[] { "MessageId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_answer_feedback_UpdatedAt",
                table: "answer_feedback",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_answer_feedback_UserId",
                table: "answer_feedback",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_arena_matches_ConversationId",
                table: "arena_matches",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_arena_matches_UserId",
                table: "arena_matches",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_arena_matches_VotedAt",
                table: "arena_matches",
                column: "VotedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "answer_feedback");

            migrationBuilder.DropTable(
                name: "arena_matches");

            migrationBuilder.DropIndex(
                name: "IX_chat_messages_CreatedAt",
                table: "chat_messages");
        }
    }
}
