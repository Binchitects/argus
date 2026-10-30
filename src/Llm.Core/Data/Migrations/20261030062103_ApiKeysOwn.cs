using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ApiKeysOwn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The keys people made for themselves, with their names and limits, into the app's keys: a key the mirror of
            // LiteLLM's keys brought over takes them (and is one of the person's own from now on); one it has not yet is kept
            // with what the app knows, and the mirror gives it LiteLLM's alias, preview and spend at its next look.
            migrationBuilder.Sql("""
                update gateway.api_keys k set kind = 'own', name = o."Name", requests_per_minute = o."RequestsPerMinute",
                  tokens_per_minute = o."TokensPerMinute", parallel = o."Parallel", monthly_budget = o."MonthlyBudget", created_at = o."CreatedAt"
                from own_keys o where k.hash = o."Token";
                insert into gateway.api_keys (hash, user_id, kind, alias, name, preview, requests_per_minute, tokens_per_minute, parallel, monthly_budget, created_at, spend)
                select o."Token", o."UserId", 'own', 'own-' || left(o."Token", 16), o."Name", 'sk-...', o."RequestsPerMinute", o."TokensPerMinute", o."Parallel",
                  o."MonthlyBudget", o."CreatedAt", 0
                from own_keys o where length(o."Token") = 64 and not exists (select 1 from gateway.api_keys k where k.hash = o."Token")
                on conflict do nothing;
                """);
            migrationBuilder.DropTable(
                name: "own_keys");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "own_keys",
                columns: table => new
                {
                    Token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MonthlyBudget = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Parallel = table.Column<int>(type: "integer", nullable: true),
                    RequestsPerMinute = table.Column<int>(type: "integer", nullable: true),
                    TokensPerMinute = table.Column<int>(type: "integer", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_own_keys", x => x.Token);
                    table.ForeignKey(
                        name: "FK_own_keys_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_own_keys_UserId",
                table: "own_keys",
                column: "UserId");

            migrationBuilder.Sql("""
                insert into own_keys ("Token", "UserId", "Name", "RequestsPerMinute", "TokensPerMinute", "Parallel", "MonthlyBudget", "CreatedAt")
                select hash, user_id, coalesce(name, alias), requests_per_minute, tokens_per_minute, parallel, monthly_budget, created_at
                from gateway.api_keys where kind = 'own';
                """);
        }
    }
}
