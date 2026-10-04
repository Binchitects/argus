using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AnswerCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CacheApiAnswers",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "answer_cache",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    KeyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Response = table.Column<string>(type: "text", nullable: false),
                    Tokens = table.Column<int>(type: "integer", nullable: false),
                    Hits = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_answer_cache", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_answer_cache_ExpiresAt",
                table: "answer_cache",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_answer_cache_Hash",
                table: "answer_cache",
                column: "Hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_answer_cache_KeyHash",
                table: "answer_cache",
                column: "KeyHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "answer_cache");

            migrationBuilder.DropColumn(
                name: "CacheApiAnswers",
                table: "AspNetUsers");
        }
    }
}
