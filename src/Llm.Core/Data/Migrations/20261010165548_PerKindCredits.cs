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
            migrationBuilder.RenameColumn(
                name: "Credit",
                table: "groups",
                newName: "VideoCredit");

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
                update groups set "ChatCredit" = "VideoCredit", "ApiCredit" = "VideoCredit", "PictureCredit" = "VideoCredit", "SpeechCredit" = "VideoCredit"
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

            migrationBuilder.RenameColumn(
                name: "VideoCredit",
                table: "groups",
                newName: "Credit");
        }
    }
}
