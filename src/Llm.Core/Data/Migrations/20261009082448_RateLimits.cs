using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class RateLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RequestsPerMinute",
                table: "groups",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TokensPerMinute",
                table: "groups",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RequestsPerMinute",
                table: "AspNetUsers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TokensPerMinute",
                table: "AspNetUsers",
                type: "integer",
                nullable: true);

            // When the upgrade ran: until the first key check after it, a limit that keys made before it
            // carry (set in LiteLLM's own pages) is kept as their person's own, not lifted (RateLimits.KeepRow).
            migrationBuilder.Sql("""
                INSERT INTO settings (key, value, updated_at)
                VALUES ('gateway.rate_limits_keep', to_char(now() AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS"Z"'), now())
                ON CONFLICT (key) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM settings WHERE key = 'gateway.rate_limits_keep';");

            migrationBuilder.DropColumn(
                name: "RequestsPerMinute",
                table: "groups");

            migrationBuilder.DropColumn(
                name: "TokensPerMinute",
                table: "groups");

            migrationBuilder.DropColumn(
                name: "RequestsPerMinute",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "TokensPerMinute",
                table: "AspNetUsers");
        }
    }
}
