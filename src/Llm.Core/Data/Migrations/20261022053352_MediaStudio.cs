using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class MediaStudio : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "media_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttachmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Action = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Prompt = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Width = table.Column<int>(type: "integer", nullable: true),
                    Height = table.Column<int>(type: "integer", nullable: true),
                    Seconds = table.Column<double>(type: "double precision", nullable: true),
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Thumb = table.Column<byte[]>(type: "bytea", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_media_items_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_media_items_chat_attachments_AttachmentId",
                        column: x => x.AttachmentId,
                        principalTable: "chat_attachments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_media_items_media_items_ParentId",
                        column: x => x.ParentId,
                        principalTable: "media_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "media_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Prompt = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: true),
                    Count = table.Column<int>(type: "integer", nullable: false),
                    Options = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Phase = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Items = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_media_jobs_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_media_items_AttachmentId",
                table: "media_items",
                column: "AttachmentId");

            migrationBuilder.CreateIndex(
                name: "IX_media_items_OwnerId_Kind_CreatedAt",
                table: "media_items",
                columns: new[] { "OwnerId", "Kind", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_media_items_ParentId",
                table: "media_items",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_media_jobs_OwnerId_Status",
                table: "media_jobs",
                columns: new[] { "OwnerId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "media_items");

            migrationBuilder.DropTable(
                name: "media_jobs");
        }
    }
}
