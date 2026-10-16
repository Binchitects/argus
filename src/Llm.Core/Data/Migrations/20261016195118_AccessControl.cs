using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AccessControl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "access_changes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Before = table.Column<string>(type: "jsonb", nullable: true),
                    After = table.Column<string>(type: "jsonb", nullable: true),
                    BulkId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_access_changes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "access_meta",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_access_meta", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "access_roles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    BuiltIn = table.Column<bool>(type: "boolean", nullable: false),
                    Permissions = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_access_roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "access_rules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Effect = table.Column<int>(type: "integer", nullable: false),
                    SubjectKind = table.Column<int>(type: "integer", nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResourceKind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Resource = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Action = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_access_rules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "access_assignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectKind = table.Column<int>(type: "integer", nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Scope = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_access_assignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_access_assignments_access_roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "access_roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "access_meta",
                columns: new[] { "Id", "Version" },
                values: new object[] { 1, 1L });

            migrationBuilder.CreateIndex(
                name: "IX_access_assignments_RoleId",
                table: "access_assignments",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_access_assignments_SubjectKind_SubjectId",
                table: "access_assignments",
                columns: new[] { "SubjectKind", "SubjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_access_changes_At",
                table: "access_changes",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_access_changes_BulkId",
                table: "access_changes",
                column: "BulkId");

            migrationBuilder.CreateIndex(
                name: "IX_access_roles_Key",
                table: "access_roles",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_access_roles_Name",
                table: "access_roles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_access_rules_ResourceKind_Resource",
                table: "access_rules",
                columns: new[] { "ResourceKind", "Resource" });

            migrationBuilder.CreateIndex(
                name: "IX_access_rules_SubjectKind_SubjectId",
                table: "access_rules",
                columns: new[] { "SubjectKind", "SubjectId" });

            // Who may use what, moved into roles and rules (docs/design/v5.9-code-review-access.md, 11.5): the same people may do
            // the same things afterwards. The built-in roles get their permissions at start (AccessService.SyncBuiltInRolesAsync).
            migrationBuilder.Sql("""
                INSERT INTO access_roles ("Id", "Key", "Name", "BuiltIn", "Permissions", "CreatedAt") VALUES
                  (gen_random_uuid(), 'owner', 'Owner', true, '{}', now()),
                  (gen_random_uuid(), 'people-admin', 'People admin', true, '{}', now()),
                  (gen_random_uuid(), 'model-admin', 'Model admin', true, '{}', now()),
                  (gen_random_uuid(), 'billing', 'Billing', true, '{}', now()),
                  (gen_random_uuid(), 'auditor', 'Auditor', true, '{}', now()),
                  (gen_random_uuid(), 'argus-admin', 'Argus admin', true, '{}', now()),
                  (gen_random_uuid(), 'room-moderator', 'Room moderator', true, '{}', now()),
                  (gen_random_uuid(), 'review-admin', 'Review admin', true, '{}', now()),
                  (gen_random_uuid(), 'member', 'Member', true, '{}', now());

                -- Everyone holds the features (the Member role); each admin is an Owner.
                INSERT INTO access_assignments ("Id", "RoleId", "SubjectKind", "SubjectId", "CreatedAt")
                SELECT gen_random_uuid(), r."Id", 3, NULL, now() FROM access_roles r WHERE r."Key" = 'member';
                INSERT INTO access_assignments ("Id", "RoleId", "SubjectKind", "SubjectId", "CreatedAt")
                SELECT gen_random_uuid(), o."Id", 0, ur."UserId", now()
                FROM "AspNetUserRoles" ur JOIN "AspNetRoles" ar ON ar."Id" = ur."RoleId", access_roles o
                WHERE ar."NormalizedName" = 'ADMIN' AND o."Key" = 'owner';

                -- A tool's, a model's and a folder's or website's (or Confluence's and SharePoint's fallback) audience: everyone (0),
                -- admins (1: the Owner role) or the listed groups (2; none listed was admins only, as admins always could).
                CREATE TEMP TABLE audiences (kind text, resource text, audience int, groups uuid[]);
                INSERT INTO audiences SELECT 'tool', "ToolId", "Audience", "Groups" FROM tool_settings;
                INSERT INTO audiences SELECT 'model', "Model", "Audience", "Groups" FROM model_access;
                INSERT INTO audiences SELECT 'knowledge_source', "Id"::text, "Audience", "Groups" FROM knowledge_sources WHERE "Kind" <> 'gitlab';
                INSERT INTO access_rules ("Id", "Effect", "SubjectKind", "SubjectId", "ResourceKind", "Resource", "Action", "CreatedAt")
                SELECT gen_random_uuid(), 0, 3, NULL, a.kind, a.resource, CASE a.kind WHEN 'knowledge_source' THEN 'read' ELSE 'use' END, now()
                FROM audiences a WHERE a.audience = 0;
                INSERT INTO access_rules ("Id", "Effect", "SubjectKind", "SubjectId", "ResourceKind", "Resource", "Action", "CreatedAt")
                SELECT gen_random_uuid(), 0, 1, g, a.kind, a.resource, CASE a.kind WHEN 'knowledge_source' THEN 'read' ELSE 'use' END, now()
                FROM audiences a, unnest(a.groups) g WHERE a.audience = 2;
                INSERT INTO access_rules ("Id", "Effect", "SubjectKind", "SubjectId", "ResourceKind", "Resource", "Action", "CreatedAt")
                SELECT gen_random_uuid(), 0, 2, o."Id", a.kind, a.resource, CASE a.kind WHEN 'knowledge_source' THEN 'read' ELSE 'use' END, now()
                FROM audiences a, access_roles o
                WHERE o."Key" = 'owner' AND (a.audience = 1 OR (a.audience = 2 AND cardinality(a.groups) = 0) OR a.audience NOT IN (0, 1, 2));
                DROP TABLE audiences;

                -- A folder's and a website's documents are read by whom the rules on their source allow, decided at each search.
                UPDATE knowledge_documents d SET "Readers" = ARRAY['chosen:' || replace(s."Id"::text, '-', '')]
                FROM knowledge_sources s WHERE d."SourceId" = s."Id" AND s."Kind" IN ('folder', 'website');

                -- API access taken away is the person's own deny of feature.api_keys.
                INSERT INTO access_rules ("Id", "Effect", "SubjectKind", "SubjectId", "ResourceKind", "Resource", "Action", "Note", "CreatedAt")
                SELECT gen_random_uuid(), 1, 0, u."Id", 'feature', 'feature.api_keys', 'use', 'API access off (Admin → People)', now()
                FROM "AspNetUsers" u WHERE u."ApiOff";

                -- The Identity roles are no longer read: Owner is the admin role.
                DELETE FROM "AspNetUserRoles";
                """);

            migrationBuilder.DropTable(
                name: "model_access");

            migrationBuilder.DropColumn(
                name: "Audience",
                table: "tool_settings");

            migrationBuilder.DropColumn(
                name: "Groups",
                table: "tool_settings");

            migrationBuilder.DropColumn(
                name: "Audience",
                table: "knowledge_sources");

            migrationBuilder.DropColumn(
                name: "Groups",
                table: "knowledge_sources");

            migrationBuilder.DropColumn(
                name: "ApiOff",
                table: "AspNetUsers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "access_assignments");

            migrationBuilder.DropTable(
                name: "access_changes");

            migrationBuilder.DropTable(
                name: "access_meta");

            migrationBuilder.DropTable(
                name: "access_rules");

            migrationBuilder.DropTable(
                name: "access_roles");

            migrationBuilder.AddColumn<int>(
                name: "Audience",
                table: "tool_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<List<Guid>>(
                name: "Groups",
                table: "tool_settings",
                type: "uuid[]",
                nullable: false,
                defaultValueSql: "'{}'::uuid[]");

            migrationBuilder.AddColumn<int>(
                name: "Audience",
                table: "knowledge_sources",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<List<Guid>>(
                name: "Groups",
                table: "knowledge_sources",
                type: "uuid[]",
                nullable: false,
                defaultValueSql: "'{}'::uuid[]");

            migrationBuilder.AddColumn<bool>(
                name: "ApiOff",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "model_access",
                columns: table => new
                {
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Audience = table.Column<int>(type: "integer", nullable: false),
                    Groups = table.Column<List<Guid>>(type: "uuid[]", nullable: false, defaultValueSql: "'{}'::uuid[]"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_access", x => x.Model);
                });
        }
    }
}
