using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AskYourData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "data_boards",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Reach = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    GroupIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false, defaultValueSql: "'{}'::uuid[]"),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_data_boards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_data_boards_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "data_sources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ConnectionProtected = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    Tables = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Reach = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    GroupIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false, defaultValueSql: "'{}'::uuid[]"),
                    Schema = table.Column<string>(type: "text", nullable: false),
                    SchemaReadAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    MostRows = table.Column<int>(type: "integer", nullable: false),
                    Seconds = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_data_sources", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "data_pins",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Question = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Sql = table.Column<string>(type: "text", nullable: false),
                    Chart = table.Column<string>(type: "text", nullable: false),
                    Refresh = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Result = table.Column<string>(type: "text", nullable: true),
                    RanAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_data_pins", x => x.Id);
                    table.ForeignKey(
                        name: "FK_data_pins_data_boards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "data_boards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_data_pins_data_sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "data_sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "data_queries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Question = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Sql = table.Column<string>(type: "text", nullable: false),
                    Via = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Rows = table.Column<int>(type: "integer", nullable: false),
                    Milliseconds = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_data_queries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_data_queries_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_data_queries_data_sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "data_sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_data_boards_OwnerId",
                table: "data_boards",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_data_pins_BoardId_Position",
                table: "data_pins",
                columns: new[] { "BoardId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_data_pins_Refresh_RanAt",
                table: "data_pins",
                columns: new[] { "Refresh", "RanAt" });

            migrationBuilder.CreateIndex(
                name: "IX_data_pins_SourceId",
                table: "data_pins",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_data_queries_SourceId_At",
                table: "data_queries",
                columns: new[] { "SourceId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_data_queries_UserId_At",
                table: "data_queries",
                columns: new[] { "UserId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_data_sources_Name",
                table: "data_sources",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "data_pins");

            migrationBuilder.DropTable(
                name: "data_queries");

            migrationBuilder.DropTable(
                name: "data_boards");

            migrationBuilder.DropTable(
                name: "data_sources");
        }
    }
}
