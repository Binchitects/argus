using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class DataRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ProcessingRestrictedAt",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "data_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Person = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Details = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReceivedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DoneAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DoneBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Outcome = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Certificate = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_data_requests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_data_requests_Status_DueAt",
                table: "data_requests",
                columns: new[] { "Status", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_data_requests_UserId",
                table: "data_requests",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "data_requests");

            migrationBuilder.DropColumn(
                name: "ProcessingRestrictedAt",
                table: "AspNetUsers");
        }
    }
}
