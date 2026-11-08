using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class SecurityBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BatchId",
                table: "security_scans",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "security_batches",
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
                    PromptTokens = table.Column<int>(type: "integer", nullable: false),
                    CompletionTokens = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_batches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_security_batches_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_security_scans_BatchId",
                table: "security_scans",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_security_batches_UserId_CreatedAt",
                table: "security_batches",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_security_scans_security_batches_BatchId",
                table: "security_scans",
                column: "BatchId",
                principalTable: "security_batches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_security_scans_security_batches_BatchId",
                table: "security_scans");

            migrationBuilder.DropTable(
                name: "security_batches");

            migrationBuilder.DropIndex(
                name: "IX_security_scans_BatchId",
                table: "security_scans");

            migrationBuilder.DropColumn(
                name: "BatchId",
                table: "security_scans");
        }
    }
}
