using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class OwnKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "own_keys",
                columns: table => new
                {
                    Token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    RequestsPerMinute = table.Column<int>(type: "integer", nullable: true),
                    TokensPerMinute = table.Column<int>(type: "integer", nullable: true),
                    Parallel = table.Column<int>(type: "integer", nullable: true),
                    MonthlyBudget = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_own_keys", x => x.Token);
                    table.ForeignKey(
                        name: "FK_own_keys_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_own_keys_UserId",
                table: "own_keys",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "own_keys");
        }
    }
}
