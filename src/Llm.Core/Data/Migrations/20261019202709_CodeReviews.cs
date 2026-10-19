using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class CodeReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "code_reviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Project = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Iid = table.Column<int>(type: "integer", nullable: true),
                    FromRef = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ToRef = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    WebUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    HeadSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PreviousId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Step = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Verdict = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Risk = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Summary = table.Column<string>(type: "text", nullable: true),
                    Ci = table.Column<string>(type: "text", nullable: true),
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Files = table.Column<int>(type: "integer", nullable: false),
                    PromptTokens = table.Column<int>(type: "integer", nullable: false),
                    CompletionTokens = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true),
                    RoomId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_reviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_code_reviews_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_code_reviews_conversations_RoomId",
                        column: x => x.RoomId,
                        principalTable: "conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "review_findings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReviewId = table.Column<Guid>(type: "uuid", nullable: false),
                    Path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Line = table.Column<int>(type: "integer", nullable: true),
                    Severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    Suggestion = table.Column<string>(type: "text", nullable: true),
                    Verified = table.Column<bool>(type: "boolean", nullable: true),
                    VerifyNote = table.Column<string>(type: "text", nullable: true),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DecidedById = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_findings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_review_findings_code_reviews_ReviewId",
                        column: x => x.ReviewId,
                        principalTable: "code_reviews",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_code_reviews_Project_Iid",
                table: "code_reviews",
                columns: new[] { "Project", "Iid" });

            migrationBuilder.CreateIndex(
                name: "IX_code_reviews_RoomId",
                table: "code_reviews",
                column: "RoomId");

            migrationBuilder.CreateIndex(
                name: "IX_code_reviews_UserId_CreatedAt",
                table: "code_reviews",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_review_findings_ReviewId",
                table: "review_findings",
                column: "ReviewId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "review_findings");

            migrationBuilder.DropTable(
                name: "code_reviews");
        }
    }
}
