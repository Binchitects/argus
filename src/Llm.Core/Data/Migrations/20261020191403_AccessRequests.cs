using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AccessRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "access_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResourceKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Resource = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DecidedById = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_access_requests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_access_requests_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_access_requests_access_roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "access_roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_access_requests_RoleId",
                table: "access_requests",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_access_requests_Status_CreatedAt",
                table: "access_requests",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_access_requests_UserId",
                table: "access_requests",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "access_requests");
        }
    }
}
