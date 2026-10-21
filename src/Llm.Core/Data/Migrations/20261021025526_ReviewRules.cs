using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReviewRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RuleId",
                table: "code_reviews",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Trigger",
                table: "code_reviews",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "review_rules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Project = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    MergeRequests = table.Column<bool>(type: "boolean", nullable: false),
                    TargetBranches = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    SkipDrafts = table.Column<bool>(type: "boolean", nullable: false),
                    CheckMinutes = table.Column<int>(type: "integer", nullable: false),
                    Branch = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Base = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Cron = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TimeZone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RoomId = table.Column<Guid>(type: "uuid", nullable: true),
                    MostPerDay = table.Column<int>(type: "integer", nullable: false),
                    SecretHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CheckedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextRunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_rules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_review_rules_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_review_rules_conversations_RoomId",
                        column: x => x.RoomId,
                        principalTable: "conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_code_reviews_RuleId_CreatedAt",
                table: "code_reviews",
                columns: new[] { "RuleId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_review_rules_Project",
                table: "review_rules",
                column: "Project");

            migrationBuilder.CreateIndex(
                name: "IX_review_rules_RoomId",
                table: "review_rules",
                column: "RoomId");

            migrationBuilder.CreateIndex(
                name: "IX_review_rules_UserId",
                table: "review_rules",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_code_reviews_review_rules_RuleId",
                table: "code_reviews",
                column: "RuleId",
                principalTable: "review_rules",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_code_reviews_review_rules_RuleId",
                table: "code_reviews");

            migrationBuilder.DropTable(
                name: "review_rules");

            migrationBuilder.DropIndex(
                name: "IX_code_reviews_RuleId_CreatedAt",
                table: "code_reviews");

            migrationBuilder.DropColumn(
                name: "RuleId",
                table: "code_reviews");

            migrationBuilder.DropColumn(
                name: "Trigger",
                table: "code_reviews");
        }
    }
}
