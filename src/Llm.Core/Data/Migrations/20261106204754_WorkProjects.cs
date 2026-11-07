using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <summary>
    /// Projects (work_projects and their people, repositories, things, goals and tasks), a chat's project, and a request's
    /// project in the ledger. Both new columns may be null: chats and requests from before are in no project.
    /// </summary>
    public partial class WorkProjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "project_id",
                schema: "gateway",
                table: "requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WorkProjectId",
                table: "conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "work_projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Global = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    StartsOn = table.Column<DateOnly>(type: "date", nullable: true),
                    DueOn = table.Column<DateOnly>(type: "date", nullable: true),
                    MonthlyBudget = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    AlertPercent = table.Column<int>(type: "integer", nullable: false),
                    StopAtBudget = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_projects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_work_projects_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "work_project_goals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    DueOn = table.Column<DateOnly>(type: "date", nullable: true),
                    DoneAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_project_goals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_work_project_goals_work_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "work_projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "work_project_items",
                columns: table => new
                {
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    AddedById = table.Column<Guid>(type: "uuid", nullable: true),
                    AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_project_items", x => new { x.ProjectId, x.Kind, x.ItemId });
                    table.ForeignKey(
                        name: "FK_work_project_items_work_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "work_projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "work_project_members",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: true),
                    Role = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    AddedById = table.Column<Guid>(type: "uuid", nullable: true),
                    AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_project_members", x => x.Id);
                    table.CheckConstraint("CK_work_project_members_one", "(\"UserId\" IS NULL) <> (\"GroupId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_work_project_members_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_work_project_members_groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_work_project_members_work_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "work_projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "work_project_repositories",
                columns: table => new
                {
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Path = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Host = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AddedById = table.Column<Guid>(type: "uuid", nullable: true),
                    AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_project_repositories", x => new { x.ProjectId, x.Path });
                    table.ForeignKey(
                        name: "FK_work_project_repositories_work_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "work_projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "work_project_tasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    GoalId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    AssigneeId = table.Column<Guid>(type: "uuid", nullable: true),
                    Done = table.Column<bool>(type: "boolean", nullable: false),
                    DoneAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_project_tasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_work_project_tasks_AspNetUsers_AssigneeId",
                        column: x => x.AssigneeId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_work_project_tasks_work_project_goals_GoalId",
                        column: x => x.GoalId,
                        principalTable: "work_project_goals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_work_project_tasks_work_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "work_projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_requests_conversation_id",
                schema: "gateway",
                table: "requests",
                column: "conversation_id",
                filter: "conversation_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_requests_project_id_started_at",
                schema: "gateway",
                table: "requests",
                columns: new[] { "project_id", "started_at" },
                filter: "project_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_conversations_WorkProjectId",
                table: "conversations",
                column: "WorkProjectId",
                filter: "\"WorkProjectId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_work_project_goals_ProjectId_Position",
                table: "work_project_goals",
                columns: new[] { "ProjectId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_work_project_items_Kind_ItemId",
                table: "work_project_items",
                columns: new[] { "Kind", "ItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_work_project_members_GroupId",
                table: "work_project_members",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_work_project_members_ProjectId_GroupId",
                table: "work_project_members",
                columns: new[] { "ProjectId", "GroupId" },
                unique: true,
                filter: "\"GroupId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_work_project_members_ProjectId_UserId",
                table: "work_project_members",
                columns: new[] { "ProjectId", "UserId" },
                unique: true,
                filter: "\"UserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_work_project_members_UserId",
                table: "work_project_members",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_work_project_repositories_Path",
                table: "work_project_repositories",
                column: "Path");

            migrationBuilder.CreateIndex(
                name: "IX_work_project_tasks_AssigneeId",
                table: "work_project_tasks",
                column: "AssigneeId");

            migrationBuilder.CreateIndex(
                name: "IX_work_project_tasks_GoalId",
                table: "work_project_tasks",
                column: "GoalId");

            migrationBuilder.CreateIndex(
                name: "IX_work_project_tasks_ProjectId_CreatedAt",
                table: "work_project_tasks",
                columns: new[] { "ProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_work_projects_Global",
                table: "work_projects",
                column: "Global",
                filter: "\"Global\"");

            migrationBuilder.CreateIndex(
                name: "IX_work_projects_OwnerId",
                table: "work_projects",
                column: "OwnerId");

            migrationBuilder.AddForeignKey(
                name: "FK_conversations_work_projects_WorkProjectId",
                table: "conversations",
                column: "WorkProjectId",
                principalTable: "work_projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_conversations_work_projects_WorkProjectId",
                table: "conversations");

            migrationBuilder.DropTable(
                name: "work_project_items");

            migrationBuilder.DropTable(
                name: "work_project_members");

            migrationBuilder.DropTable(
                name: "work_project_repositories");

            migrationBuilder.DropTable(
                name: "work_project_tasks");

            migrationBuilder.DropTable(
                name: "work_project_goals");

            migrationBuilder.DropTable(
                name: "work_projects");

            migrationBuilder.DropIndex(
                name: "IX_requests_conversation_id",
                schema: "gateway",
                table: "requests");

            migrationBuilder.DropIndex(
                name: "IX_requests_project_id_started_at",
                schema: "gateway",
                table: "requests");

            migrationBuilder.DropIndex(
                name: "IX_conversations_WorkProjectId",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "project_id",
                schema: "gateway",
                table: "requests");

            migrationBuilder.DropColumn(
                name: "WorkProjectId",
                table: "conversations");
        }
    }
}
