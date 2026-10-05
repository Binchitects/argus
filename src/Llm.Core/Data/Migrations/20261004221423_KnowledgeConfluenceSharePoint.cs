using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class KnowledgeConfluenceSharePoint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Account",
                table: "knowledge_sources",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "BlogPosts",
                table: "knowledge_sources",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Cursor",
                table: "knowledge_sources",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Mirror",
                table: "knowledge_sources",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SecretEncrypted",
                table: "knowledge_sources",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SitePages",
                table: "knowledge_sources",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Spaces",
                table: "knowledge_sources",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Tenant",
                table: "knowledge_sources",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "Requires",
                table: "knowledge_documents",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Account",
                table: "knowledge_sources");

            migrationBuilder.DropColumn(
                name: "BlogPosts",
                table: "knowledge_sources");

            migrationBuilder.DropColumn(
                name: "Cursor",
                table: "knowledge_sources");

            migrationBuilder.DropColumn(
                name: "Mirror",
                table: "knowledge_sources");

            migrationBuilder.DropColumn(
                name: "SecretEncrypted",
                table: "knowledge_sources");

            migrationBuilder.DropColumn(
                name: "SitePages",
                table: "knowledge_sources");

            migrationBuilder.DropColumn(
                name: "Spaces",
                table: "knowledge_sources");

            migrationBuilder.DropColumn(
                name: "Tenant",
                table: "knowledge_sources");

            migrationBuilder.DropColumn(
                name: "Requires",
                table: "knowledge_documents");
        }
    }
}
