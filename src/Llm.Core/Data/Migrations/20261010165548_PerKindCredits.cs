using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class PerKindCredits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Columns are only added: the release before reads groups."Credit" still, so it can run on this database
            // until the backup taken before the upgrade is restored.
            migrationBuilder.AddColumn<decimal>(
                name: "VideoCredit",
                table: "groups",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ApiCredit",
                table: "groups",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ChatCredit",
                table: "groups",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PictureCredit",
                table: "groups",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SpeechCredit",
                table: "groups",
                type: "numeric",
                nullable: true);

            // A group's one credit (over the chat and API keys) becomes its credit of every kind.
            migrationBuilder.Sql("""
                update groups set "ChatCredit" = "Credit", "ApiCredit" = "Credit", "PictureCredit" = "Credit", "SpeechCredit" = "Credit", "VideoCredit" = "Credit"
                """);

            migrationBuilder.AddColumn<decimal>(
                name: "ApiCredit",
                table: "AspNetUsers",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ApiOff",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "ChatCredit",
                table: "AspNetUsers",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PictureCredit",
                table: "AspNetUsers",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SpeechCredit",
                table: "AspNetUsers",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "VideoCredit",
                table: "AspNetUsers",
                type: "numeric",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApiCredit",
                table: "groups");

            migrationBuilder.DropColumn(
                name: "ChatCredit",
                table: "groups");

            migrationBuilder.DropColumn(
                name: "PictureCredit",
                table: "groups");

            migrationBuilder.DropColumn(
                name: "SpeechCredit",
                table: "groups");

            migrationBuilder.DropColumn(
                name: "ApiCredit",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "ApiOff",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "ChatCredit",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "PictureCredit",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "SpeechCredit",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "VideoCredit",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "VideoCredit",
                table: "groups");
        }
    }
}
