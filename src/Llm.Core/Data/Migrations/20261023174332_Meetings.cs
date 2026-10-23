using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class Meetings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "meetings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Step = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    RecordingId = table.Column<Guid>(type: "uuid", nullable: true),
                    Seconds = table.Column<double>(type: "double precision", nullable: true),
                    Language = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Transcript = table.Column<string>(type: "jsonb", nullable: false),
                    Speakers = table.Column<string>(type: "jsonb", nullable: false),
                    Summary = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    Decisions = table.Column<string>(type: "jsonb", nullable: false),
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PromptTokens = table.Column<int>(type: "integer", nullable: false),
                    CompletionTokens = table.Column<int>(type: "integer", nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_meetings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_meetings_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "meeting_actions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MeetingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Speaker = table.Column<int>(type: "integer", nullable: true),
                    Owner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PersonId = table.Column<Guid>(type: "uuid", nullable: true),
                    Due = table.Column<DateOnly>(type: "date", nullable: true),
                    Done = table.Column<bool>(type: "boolean", nullable: false),
                    RemindedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_meeting_actions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_meeting_actions_meetings_MeetingId",
                        column: x => x.MeetingId,
                        principalTable: "meetings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_meeting_actions_Due_Done_RemindedAt",
                table: "meeting_actions",
                columns: new[] { "Due", "Done", "RemindedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_meeting_actions_MeetingId",
                table: "meeting_actions",
                column: "MeetingId");

            migrationBuilder.CreateIndex(
                name: "IX_meetings_UserId_CreatedAt",
                table: "meetings",
                columns: new[] { "UserId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "meeting_actions");

            migrationBuilder.DropTable(
                name: "meetings");
        }
    }
}
