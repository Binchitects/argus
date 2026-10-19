using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class Rooms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ForPeople",
                table: "queued_messages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<List<Guid>>(
                name: "Mentions",
                table: "queued_messages",
                type: "uuid[]",
                nullable: false,
                defaultValueSql: "'{}'::uuid[]");

            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                table: "queued_messages",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "AnswerMode",
                table: "conversations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "conversations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MembersInvite",
                table: "conversations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "AuthorId",
                table: "chat_messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ForPeople",
                table: "chat_messages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<List<Guid>>(
                name: "Mentions",
                table: "chat_messages",
                type: "uuid[]",
                nullable: false,
                defaultValueSql: "'{}'::uuid[]");

            migrationBuilder.CreateTable(
                name: "room_groups",
                columns: table => new
                {
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_room_groups", x => new { x.ConversationId, x.GroupId });
                    table.ForeignKey(
                        name: "FK_room_groups_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_room_groups_groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "room_members",
                columns: table => new
                {
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AddedById = table.Column<Guid>(type: "uuid", nullable: true),
                    AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastReadAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Notify = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_room_members", x => new { x.ConversationId, x.UserId });
                    table.ForeignKey(
                        name: "FK_room_members_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_room_members_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Every message so far is its chat owner's (or answers them); queued ones too.
            migrationBuilder.Sql("""UPDATE chat_messages m SET "AuthorId" = c."UserId" FROM conversations c WHERE m."ConversationId" = c."Id";""");
            migrationBuilder.Sql("""UPDATE queued_messages q SET "UserId" = c."UserId" FROM conversations c WHERE q."ConversationId" = c."Id";""");

            migrationBuilder.CreateIndex(
                name: "IX_queued_messages_UserId",
                table: "queued_messages",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_chat_messages_AuthorId_CreatedAt",
                table: "chat_messages",
                columns: new[] { "AuthorId", "CreatedAt" },
                filter: "\"Role\" = 'user'");

            migrationBuilder.CreateIndex(
                name: "IX_room_groups_GroupId",
                table: "room_groups",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_room_members_UserId",
                table: "room_members",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_chat_messages_AspNetUsers_AuthorId",
                table: "chat_messages",
                column: "AuthorId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_queued_messages_AspNetUsers_UserId",
                table: "queued_messages",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_chat_messages_AspNetUsers_AuthorId",
                table: "chat_messages");

            migrationBuilder.DropForeignKey(
                name: "FK_queued_messages_AspNetUsers_UserId",
                table: "queued_messages");

            migrationBuilder.DropTable(
                name: "room_groups");

            migrationBuilder.DropTable(
                name: "room_members");

            migrationBuilder.DropIndex(
                name: "IX_queued_messages_UserId",
                table: "queued_messages");

            migrationBuilder.DropIndex(
                name: "IX_chat_messages_AuthorId_CreatedAt",
                table: "chat_messages");

            migrationBuilder.DropColumn(
                name: "ForPeople",
                table: "queued_messages");

            migrationBuilder.DropColumn(
                name: "Mentions",
                table: "queued_messages");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "queued_messages");

            migrationBuilder.DropColumn(
                name: "AnswerMode",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "MembersInvite",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "AuthorId",
                table: "chat_messages");

            migrationBuilder.DropColumn(
                name: "ForPeople",
                table: "chat_messages");

            migrationBuilder.DropColumn(
                name: "Mentions",
                table: "chat_messages");
        }
    }
}
