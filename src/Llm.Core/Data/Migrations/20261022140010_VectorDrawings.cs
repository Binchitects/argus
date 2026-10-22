using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class VectorDrawings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "media_vectors",
                columns: table => new
                {
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Still = table.Column<string>(type: "text", nullable: false),
                    Motions = table.Column<string>(type: "text", nullable: false),
                    Seconds = table.Column<double>(type: "double precision", nullable: false),
                    Loops = table.Column<bool>(type: "boolean", nullable: false),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Style = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_vectors", x => x.ItemId);
                    table.ForeignKey(
                        name: "FK_media_vectors_media_items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "media_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "media_vector_versions",
                columns: table => new
                {
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Change = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Still = table.Column<string>(type: "text", nullable: false),
                    Motions = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_vector_versions", x => new { x.ItemId, x.Version });
                    table.ForeignKey(
                        name: "FK_media_vector_versions_media_vectors_ItemId",
                        column: x => x.ItemId,
                        principalTable: "media_vectors",
                        principalColumn: "ItemId",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "media_vector_versions");

            migrationBuilder.DropTable(
                name: "media_vectors");
        }
    }
}
