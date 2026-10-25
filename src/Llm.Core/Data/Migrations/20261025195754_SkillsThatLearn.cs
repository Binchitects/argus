using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class SkillsThatLearn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Proposed",
                table: "skills",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "skills",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "typed");

            migrationBuilder.AddColumn<Guid>(
                name: "SourceId",
                table: "skills",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "skills",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "skill_changes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SkillId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Before = table.Column<string>(type: "character varying(40000)", maxLength: 40000, nullable: true),
                    After = table.Column<string>(type: "character varying(40000)", maxLength: 40000, nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: true),
                    Proposed = table.Column<bool>(type: "boolean", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AppliedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UndoneAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skill_changes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_skill_changes_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_skill_changes_skills_SkillId",
                        column: x => x.SkillId,
                        principalTable: "skills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_skill_changes_SkillId_At",
                table: "skill_changes",
                columns: new[] { "SkillId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_skill_changes_UserId_Proposed",
                table: "skill_changes",
                columns: new[] { "UserId", "Proposed" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "skill_changes");

            migrationBuilder.DropColumn(
                name: "Proposed",
                table: "skills");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "skills");

            migrationBuilder.DropColumn(
                name: "SourceId",
                table: "skills");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "skills");
        }
    }
}
