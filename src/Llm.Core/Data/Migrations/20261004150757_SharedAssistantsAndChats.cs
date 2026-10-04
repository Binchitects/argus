using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class SharedAssistantsAndChats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ChatsStarted",
                table: "projects",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // A project's chats so far count as chats started with it.
            migrationBuilder.Sql("""UPDATE projects SET "ChatsStarted" = (SELECT count(*) FROM conversations c WHERE c."ProjectId" = projects."Id")""");

            migrationBuilder.AddColumn<string>(
                name: "Color",
                table: "projects",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "blue");

            migrationBuilder.AddColumn<List<Guid>>(
                name: "EditorGroups",
                table: "projects",
                type: "uuid[]",
                nullable: false,
                defaultValueSql: "'{}'::uuid[]");

            migrationBuilder.AddColumn<List<Guid>>(
                name: "EditorPeople",
                table: "projects",
                type: "uuid[]",
                nullable: false,
                defaultValueSql: "'{}'::uuid[]");

            migrationBuilder.AddColumn<List<Guid>>(
                name: "Groups",
                table: "projects",
                type: "uuid[]",
                nullable: false,
                defaultValueSql: "'{}'::uuid[]");

            migrationBuilder.AddColumn<string>(
                name: "Icon",
                table: "projects",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "bot");

            migrationBuilder.AddColumn<string>(
                name: "Model",
                table: "projects",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Reach",
                table: "projects",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<List<string>>(
                name: "Starters",
                table: "projects",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<string>(
                name: "Thinking",
                table: "projects",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "Tools",
                table: "projects",
                type: "text[]",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "chat_shares",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LeafId = table.Column<Guid>(type: "uuid", nullable: true),
                    Reach = table.Column<int>(type: "integer", nullable: false),
                    Groups = table.Column<List<Guid>>(type: "uuid[]", nullable: false, defaultValueSql: "'{}'::uuid[]"),
                    Opens = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_shares", x => x.Id);
                    table.ForeignKey(
                        name: "FK_chat_shares_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_chat_shares_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "chat_share_views",
                columns: table => new
                {
                    ShareId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    FirstAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_share_views", x => new { x.ShareId, x.UserId });
                    table.ForeignKey(
                        name: "FK_chat_share_views_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_chat_share_views_chat_shares_ShareId",
                        column: x => x.ShareId,
                        principalTable: "chat_shares",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_chat_share_views_UserId",
                table: "chat_share_views",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_chat_shares_ConversationId",
                table: "chat_shares",
                column: "ConversationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_chat_shares_UserId",
                table: "chat_shares",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_share_views");

            migrationBuilder.DropTable(
                name: "chat_shares");

            migrationBuilder.DropColumn(
                name: "ChatsStarted",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "Color",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "EditorGroups",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "EditorPeople",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "Groups",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "Icon",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "Model",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "Reach",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "Starters",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "Thinking",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "Tools",
                table: "projects");
        }
    }
}
