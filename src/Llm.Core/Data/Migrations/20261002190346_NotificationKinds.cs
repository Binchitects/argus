using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class NotificationKinds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Key",
                table: "notifications",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "notifications",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "task");

            migrationBuilder.CreateIndex(
                name: "IX_notifications_UserId_Key",
                table: "notifications",
                columns: new[] { "UserId", "Key" },
                unique: true,
                filter: "\"Key\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_notifications_UserId_Key",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "Key",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "notifications");
        }
    }
}
