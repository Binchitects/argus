using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class Governance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "BlockedPatterns",
                table: "groups",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CostCentre",
                table: "groups",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Credit",
                table: "groups",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CreditPerMember",
                table: "groups",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Moderation",
                table: "groups",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RedactPii",
                table: "groups",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetentionDays",
                table: "groups",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SecretScanning",
                table: "groups",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                table: "conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LegalHoldReason",
                table: "AspNetUsers",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LegalHoldSince",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BlockedPatterns",
                table: "groups");

            migrationBuilder.DropColumn(
                name: "CostCentre",
                table: "groups");

            migrationBuilder.DropColumn(
                name: "Credit",
                table: "groups");

            migrationBuilder.DropColumn(
                name: "CreditPerMember",
                table: "groups");

            migrationBuilder.DropColumn(
                name: "Moderation",
                table: "groups");

            migrationBuilder.DropColumn(
                name: "RedactPii",
                table: "groups");

            migrationBuilder.DropColumn(
                name: "RetentionDays",
                table: "groups");

            migrationBuilder.DropColumn(
                name: "SecretScanning",
                table: "groups");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "LegalHoldReason",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "LegalHoldSince",
                table: "AspNetUsers");
        }
    }
}
