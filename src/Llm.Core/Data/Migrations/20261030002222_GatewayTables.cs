using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class GatewayTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "gateway");

            migrationBuilder.CreateTable(
                name: "api_keys",
                schema: "gateway",
                columns: table => new
                {
                    hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    alias = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    preview = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: true),
                    requests_per_minute = table.Column<int>(type: "integer", nullable: true),
                    tokens_per_minute = table.Column<int>(type: "integer", nullable: true),
                    parallel = table.Column<int>(type: "integer", nullable: true),
                    monthly_budget = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    spend = table.Column<decimal>(type: "numeric(18,9)", precision: 18, scale: 9, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_keys", x => x.hash);
                    table.ForeignKey(
                        name: "FK_api_keys_AspNetUsers_user_id",
                        column: x => x.user_id,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_api_keys_studio_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "studio_workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "requests",
                schema: "gateway",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    first_token_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    duration_ms = table.Column<int>(type: "integer", nullable: false),
                    surface = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    call = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    protocol = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    credit_kind = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    person = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    key_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    key_alias = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    tag = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    deployment = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    fallback_from = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    attempts = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)1),
                    prompt_tokens = table.Column<int>(type: "integer", nullable: false),
                    cached_tokens = table.Column<int>(type: "integer", nullable: false),
                    completion_tokens = table.Column<int>(type: "integer", nullable: false),
                    reasoning_tokens = table.Column<int>(type: "integer", nullable: true),
                    total_tokens = table.Column<int>(type: "integer", nullable: false, computedColumnSql: "prompt_tokens + completion_tokens", stored: true),
                    usage_estimated = table.Column<bool>(type: "boolean", nullable: false),
                    cost = table.Column<decimal>(type: "numeric(18,9)", precision: 18, scale: 9, nullable: false),
                    seconds = table.Column<double>(type: "double precision", nullable: true),
                    pictures = table.Column<int>(type: "integer", nullable: true),
                    characters = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    http_status = table.Column<short>(type: "smallint", nullable: false),
                    refusal = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    source_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_requests", x => x.id);
                    table.ForeignKey(
                        name: "FK_requests_AspNetUsers_user_id",
                        column: x => x.user_id,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_alias",
                schema: "gateway",
                table: "api_keys",
                column: "alias",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_user_id",
                schema: "gateway",
                table: "api_keys",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_workspace_id",
                schema: "gateway",
                table: "api_keys",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "IX_requests_key_hash_started_at",
                schema: "gateway",
                table: "requests",
                columns: new[] { "key_hash", "started_at" },
                filter: "key_hash IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_requests_person_started_at",
                schema: "gateway",
                table: "requests",
                columns: new[] { "person", "started_at" });

            migrationBuilder.CreateIndex(
                name: "IX_requests_source_id",
                schema: "gateway",
                table: "requests",
                column: "source_id",
                unique: true,
                filter: "source_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_requests_started_at",
                schema: "gateway",
                table: "requests",
                column: "started_at")
                .Annotation("Npgsql:IndexInclude", new[] { "person", "credit_kind", "cost" });

            migrationBuilder.CreateIndex(
                name: "IX_requests_user_id_started_at",
                schema: "gateway",
                table: "requests",
                columns: new[] { "user_id", "started_at" });

            // Each key's requests and tokens a minute: counts, rebuilt in a minute if lost, so not written to the WAL.
            migrationBuilder.Sql("""
                CREATE UNLOGGED TABLE gateway.rate_windows (
                    key_hash char(64) NOT NULL,
                    minute timestamptz NOT NULL,
                    requests int NOT NULL DEFAULT 0,
                    tokens bigint NOT NULL DEFAULT 0,
                    PRIMARY KEY (key_hash, minute));
                """);
            // What the dashboards may read of the app's own tables: a key's owner by email, and this month's spend by person.
            migrationBuilder.Sql("""
                CREATE VIEW gateway.key_owners AS
                    SELECT k.hash, k.alias, k.kind, lower(u."Email") AS email, k.spend, k.created_at
                    FROM gateway.api_keys k JOIN "AspNetUsers" u ON u."Id" = k.user_id;
                CREATE VIEW gateway.month_spend AS
                    SELECT person, credit_kind, sum(cost) AS spend
                    FROM gateway.requests
                    WHERE started_at >= date_trunc('month', now()) AND person IS NOT NULL
                    GROUP BY person, credit_kind;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW IF EXISTS gateway.month_spend; DROP VIEW IF EXISTS gateway.key_owners; DROP TABLE IF EXISTS gateway.rate_windows;");

            migrationBuilder.DropTable(
                name: "api_keys",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "requests",
                schema: "gateway");
        }
    }
}
