using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <summary>The groups new people join at their first sign-in: none at first, so an upgrade changes nobody's groups.</summary>
    public partial class DefaultGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "JoinsNewPeople",
                table: "groups",
                type: "boolean",
                nullable: false,
                defaultValueSql: "false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "JoinsNewPeople",
                table: "groups");
        }
    }
}
