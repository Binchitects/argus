using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class CodeReviewScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BatchId",
                table: "code_reviews",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Scope",
                table: "code_reviews",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "change");

            migrationBuilder.CreateTable(
                name: "code_review_batches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Ref = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Cross = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Step = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Report = table.Column<string>(type: "text", nullable: true),
                    Links = table.Column<string>(type: "text", nullable: true),
                    PromptTokens = table.Column<int>(type: "integer", nullable: false),
                    CompletionTokens = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_review_batches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_code_review_batches_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_code_reviews_BatchId",
                table: "code_reviews",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_code_review_batches_UserId_CreatedAt",
                table: "code_review_batches",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_code_reviews_code_review_batches_BatchId",
                table: "code_reviews",
                column: "BatchId",
                principalTable: "code_review_batches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_code_reviews_code_review_batches_BatchId",
                table: "code_reviews");

            migrationBuilder.DropTable(
                name: "code_review_batches");

            migrationBuilder.DropIndex(
                name: "IX_code_reviews_BatchId",
                table: "code_reviews");

            migrationBuilder.DropColumn(
                name: "BatchId",
                table: "code_reviews");

            migrationBuilder.DropColumn(
                name: "Scope",
                table: "code_reviews");
        }
    }
}
