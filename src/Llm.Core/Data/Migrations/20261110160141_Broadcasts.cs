using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class Broadcasts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "broadcasts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Level = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Groups = table.Column<Guid[]>(type: "uuid[]", nullable: false, defaultValueSql: "'{}'::uuid[]"),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Sent = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_broadcasts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "broadcast_dismissals",
                columns: table => new
                {
                    BroadcastId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_broadcast_dismissals", x => new { x.BroadcastId, x.UserId });
                    table.ForeignKey(
                        name: "FK_broadcast_dismissals_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_broadcast_dismissals_broadcasts_BroadcastId",
                        column: x => x.BroadcastId,
                        principalTable: "broadcasts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_broadcast_dismissals_UserId",
                table: "broadcast_dismissals",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_broadcasts_CreatedAt",
                table: "broadcasts",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "broadcast_dismissals");

            migrationBuilder.DropTable(
                name: "broadcasts");
        }
    }
}
