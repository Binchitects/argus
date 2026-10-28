using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReviewSharingChecklists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Reach",
                table: "code_reviews",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<List<Guid>>(
                name: "ShareGroups",
                table: "code_reviews",
                type: "uuid[]",
                nullable: false,
                defaultValueSql: "'{}'::uuid[]");

            migrationBuilder.AddColumn<List<Guid>>(
                name: "SharePeople",
                table: "code_reviews",
                type: "uuid[]",
                nullable: false,
                defaultValueSql: "'{}'::uuid[]");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SharedAt",
                table: "code_reviews",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "checklists",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Items = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_checklists", x => x.Id);
                    table.ForeignKey(
                        name: "FK_checklists_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "checklist_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChecklistId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Items = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_checklist_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_checklist_runs_checklists_ChecklistId",
                        column: x => x.ChecklistId,
                        principalTable: "checklists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_checklist_runs_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_code_reviews_Reach",
                table: "code_reviews",
                column: "Reach");

            migrationBuilder.CreateIndex(
                name: "IX_checklist_runs_ChecklistId",
                table: "checklist_runs",
                column: "ChecklistId");

            migrationBuilder.CreateIndex(
                name: "IX_checklist_runs_ConversationId",
                table: "checklist_runs",
                column: "ConversationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_checklists_UserId_Name",
                table: "checklists",
                columns: new[] { "UserId", "Name" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "checklist_runs");

            migrationBuilder.DropTable(
                name: "checklists");

            migrationBuilder.DropIndex(
                name: "IX_code_reviews_Reach",
                table: "code_reviews");

            migrationBuilder.DropColumn(
                name: "Reach",
                table: "code_reviews");

            migrationBuilder.DropColumn(
                name: "ShareGroups",
                table: "code_reviews");

            migrationBuilder.DropColumn(
                name: "SharePeople",
                table: "code_reviews");

            migrationBuilder.DropColumn(
                name: "SharedAt",
                table: "code_reviews");
        }
    }
}
