using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class MemoryScopes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<float[]>(
                name: "Embedding",
                table: "memories",
                type: "real[]",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "memories",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "fact");

            migrationBuilder.AddColumn<bool>(
                name: "Pinned",
                table: "memories",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ProjectKey",
                table: "memories",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReplacedBy",
                table: "memories",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Scope",
                table: "memories",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "person");

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "memories",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "typed");

            migrationBuilder.AddColumn<Guid>(
                name: "SourceId",
                table: "memories",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UsedAt",
                table: "memories",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Uses",
                table: "memories",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_memories_Scope",
                table: "memories",
                column: "Scope",
                filter: "\"Scope\" = 'company'");

            migrationBuilder.CreateIndex(
                name: "IX_memories_UserId_Scope_ProjectKey",
                table: "memories",
                columns: new[] { "UserId", "Scope", "ProjectKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_memories_Scope",
                table: "memories");

            migrationBuilder.DropIndex(
                name: "IX_memories_UserId_Scope_ProjectKey",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "Embedding",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "Pinned",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "ProjectKey",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "ReplacedBy",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "Scope",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "SourceId",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "UsedAt",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "Uses",
                table: "memories");
        }
    }
}
