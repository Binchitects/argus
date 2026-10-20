using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class EngineKinds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Engine",
                table: "local_models",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EngineKind",
                table: "local_models",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "llamacpp");

            migrationBuilder.AddColumn<string>(
                name: "Fallback",
                table: "local_models",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "GpuMemoryUtilization",
                table: "local_models",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "KvCpuGb",
                table: "local_models",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Quantization",
                table: "local_models",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Replicas",
                table: "local_models",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<bool>(
                name: "Sleep",
                table: "local_models",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "SpecDecode",
                table: "local_models",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Engine",
                table: "local_models");

            migrationBuilder.DropColumn(
                name: "EngineKind",
                table: "local_models");

            migrationBuilder.DropColumn(
                name: "Fallback",
                table: "local_models");

            migrationBuilder.DropColumn(
                name: "GpuMemoryUtilization",
                table: "local_models");

            migrationBuilder.DropColumn(
                name: "KvCpuGb",
                table: "local_models");

            migrationBuilder.DropColumn(
                name: "Quantization",
                table: "local_models");

            migrationBuilder.DropColumn(
                name: "Replicas",
                table: "local_models");

            migrationBuilder.DropColumn(
                name: "Sleep",
                table: "local_models");

            migrationBuilder.DropColumn(
                name: "SpecDecode",
                table: "local_models");
        }
    }
}
