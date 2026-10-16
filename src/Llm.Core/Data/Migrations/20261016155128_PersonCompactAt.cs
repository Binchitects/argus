using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class PersonCompactAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CompactAt",
                table: "AspNetUsers",
                type: "integer",
                nullable: true);

            // The company's setting is renamed (Chat:AutoCompactPercent became Chat:CompactAt), and its 0 now
            // means "only when a message would not fit"; from 1 to 19 it is no longer a choice: 20 instead.
            migrationBuilder.Sql("""
                UPDATE settings
                SET key = 'Chat:CompactAt',
                    value = CASE WHEN value !~ '^[0-9]{1,9}$' THEN value WHEN value::int BETWEEN 1 AND 19 THEN '20' ELSE value END
                WHERE key = 'Chat:AutoCompactPercent'
                  AND NOT EXISTS (SELECT 1 FROM settings WHERE key = 'Chat:CompactAt');
                DELETE FROM settings WHERE key = 'Chat:AutoCompactPercent';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE settings SET key = 'Chat:AutoCompactPercent' WHERE key = 'Chat:CompactAt';");

            migrationBuilder.DropColumn(
                name: "CompactAt",
                table: "AspNetUsers");
        }
    }
}
