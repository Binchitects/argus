using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProjectWorkFromChats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ChatId",
                table: "work_project_tasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ChatId",
                table: "work_project_goals",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_work_project_tasks_ChatId",
                table: "work_project_tasks",
                column: "ChatId");

            migrationBuilder.CreateIndex(
                name: "IX_work_project_goals_ChatId",
                table: "work_project_goals",
                column: "ChatId");

            migrationBuilder.AddForeignKey(
                name: "FK_work_project_goals_conversations_ChatId",
                table: "work_project_goals",
                column: "ChatId",
                principalTable: "conversations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_work_project_tasks_conversations_ChatId",
                table: "work_project_tasks",
                column: "ChatId",
                principalTable: "conversations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_work_project_goals_conversations_ChatId",
                table: "work_project_goals");

            migrationBuilder.DropForeignKey(
                name: "FK_work_project_tasks_conversations_ChatId",
                table: "work_project_tasks");

            migrationBuilder.DropIndex(
                name: "IX_work_project_tasks_ChatId",
                table: "work_project_tasks");

            migrationBuilder.DropIndex(
                name: "IX_work_project_goals_ChatId",
                table: "work_project_goals");

            migrationBuilder.DropColumn(
                name: "ChatId",
                table: "work_project_tasks");

            migrationBuilder.DropColumn(
                name: "ChatId",
                table: "work_project_goals");
        }
    }
}
