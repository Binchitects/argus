using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class Projects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ProjectId",
                table: "conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Instructions = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_projects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_projects_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_files",
                columns: table => new
                {
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttachmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_files", x => new { x.ProjectId, x.AttachmentId });
                    table.ForeignKey(
                        name: "FK_project_files_chat_attachments_AttachmentId",
                        column: x => x.AttachmentId,
                        principalTable: "chat_attachments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_project_files_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_conversations_ProjectId",
                table: "conversations",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_project_files_AttachmentId",
                table: "project_files",
                column: "AttachmentId");

            migrationBuilder.CreateIndex(
                name: "IX_projects_UserId_UpdatedAt",
                table: "projects",
                columns: new[] { "UserId", "UpdatedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_conversations_projects_ProjectId",
                table: "conversations",
                column: "ProjectId",
                principalTable: "projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_conversations_projects_ProjectId",
                table: "conversations");

            migrationBuilder.DropTable(
                name: "project_files");

            migrationBuilder.DropTable(
                name: "projects");

            migrationBuilder.DropIndex(
                name: "IX_conversations_ProjectId",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                table: "conversations");
        }
    }
}
