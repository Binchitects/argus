using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class KvTiers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "KvDiskGb",
                table: "local_models",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KvTier",
                table: "local_models",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KvDiskGb",
                table: "local_models");

            migrationBuilder.DropColumn(
                name: "KvTier",
                table: "local_models");
        }
    }
}
