using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChatDigests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Digest",
                table: "conversations",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<float[]>(
                name: "DigestEmbedding",
                table: "conversations",
                type: "real[]",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DigestedSequence",
                table: "conversations",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Digest",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "DigestEmbedding",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "DigestedSequence",
                table: "conversations");
        }
    }
}
