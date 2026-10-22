using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class Skills : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The prompt library becomes the skills: every prompt kept, its text its instructions.
            migrationBuilder.DropForeignKey(name: "FK_prompts_AspNetUsers_UserId", table: "prompts");
            migrationBuilder.DropForeignKey(name: "FK_prompts_mcp_servers_ServerId", table: "prompts");
            migrationBuilder.DropPrimaryKey(name: "PK_prompts", table: "prompts");
            migrationBuilder.RenameTable(name: "prompts", newName: "skills");
            migrationBuilder.RenameColumn(name: "Text", table: "skills", newName: "Instructions");
            migrationBuilder.RenameIndex(name: "IX_prompts_ServerId", table: "skills", newName: "IX_skills_ServerId");
            migrationBuilder.RenameIndex(name: "IX_prompts_UserId", table: "skills", newName: "IX_skills_UserId");
            migrationBuilder.AddPrimaryKey(name: "PK_skills", table: "skills", column: "Id");
            migrationBuilder.AddForeignKey(name: "FK_skills_AspNetUsers_UserId", table: "skills", column: "UserId", principalTable: "AspNetUsers", principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
            migrationBuilder.AddForeignKey(name: "FK_skills_mcp_servers_ServerId", table: "skills", column: "ServerId", principalTable: "mcp_servers", principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
            migrationBuilder.AddColumn<string>(name: "Description", table: "skills", type: "character varying(1000)", maxLength: 1000, nullable: false, defaultValue: "");
            migrationBuilder.AddColumn<string>(name: "Parameters", table: "skills", type: "character varying(10000)", maxLength: 10000, nullable: false, defaultValue: "[]");
            migrationBuilder.AddColumn<bool>(name: "AutoUse", table: "skills", type: "boolean", nullable: false, defaultValue: true);

            migrationBuilder.AddColumn<string>(name: "Skill", table: "chat_messages", type: "text", nullable: true);
            migrationBuilder.AddColumn<string>(name: "Skill", table: "queued_messages", type: "text", nullable: true);

            // Who could save prompts makes skills: the permission's key, in the roles and the rules that name it.
            migrationBuilder.Sql("""UPDATE access_roles SET "Permissions" = array_replace("Permissions", 'feature.prompts', 'feature.skills');""");
            migrationBuilder.Sql("""UPDATE access_rules SET "Action" = 'feature.skills' WHERE "Action" = 'feature.prompts';""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""UPDATE access_rules SET "Action" = 'feature.prompts' WHERE "Action" = 'feature.skills';""");
            migrationBuilder.Sql("""UPDATE access_roles SET "Permissions" = array_replace("Permissions", 'feature.skills', 'feature.prompts');""");
            migrationBuilder.DropColumn(name: "Skill", table: "queued_messages");
            migrationBuilder.DropColumn(name: "Skill", table: "chat_messages");
            migrationBuilder.DropColumn(name: "AutoUse", table: "skills");
            migrationBuilder.DropColumn(name: "Parameters", table: "skills");
            migrationBuilder.DropColumn(name: "Description", table: "skills");
            migrationBuilder.DropForeignKey(name: "FK_skills_AspNetUsers_UserId", table: "skills");
            migrationBuilder.DropForeignKey(name: "FK_skills_mcp_servers_ServerId", table: "skills");
            migrationBuilder.DropPrimaryKey(name: "PK_skills", table: "skills");
            migrationBuilder.RenameColumn(name: "Instructions", table: "skills", newName: "Text");
            migrationBuilder.RenameIndex(name: "IX_skills_ServerId", table: "skills", newName: "IX_prompts_ServerId");
            migrationBuilder.RenameIndex(name: "IX_skills_UserId", table: "skills", newName: "IX_prompts_UserId");
            migrationBuilder.RenameTable(name: "skills", newName: "prompts");
            migrationBuilder.AddPrimaryKey(name: "PK_prompts", table: "prompts", column: "Id");
            migrationBuilder.AddForeignKey(name: "FK_prompts_AspNetUsers_UserId", table: "prompts", column: "UserId", principalTable: "AspNetUsers", principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
            migrationBuilder.AddForeignKey(name: "FK_prompts_mcp_servers_ServerId", table: "prompts", column: "ServerId", principalTable: "mcp_servers", principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
