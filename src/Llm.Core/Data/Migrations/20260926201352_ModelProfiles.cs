using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ModelProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DraftHead",
                table: "local_models",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DraftMax",
                table: "local_models",
                type: "integer",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<double>(
                name: "MinP",
                table: "local_models",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Mtp",
                table: "local_models",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Placement",
                table: "local_models",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "auto");

            migrationBuilder.AddColumn<double>(
                name: "PresencePenalty",
                table: "local_models",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Temperature",
                table: "local_models",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TopK",
                table: "local_models",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "TopP",
                table: "local_models",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Ubatch",
                table: "local_models",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Yarn",
                table: "local_models",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Models placed by hand before: kept so. The rest (everything on the GPU) is now placed by llama.cpp's fit.
            migrationBuilder.Sql("UPDATE local_models SET \"Placement\" = 'manual' WHERE \"GpuLayers\" < 99 OR \"CpuMoe\" > 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DraftHead",
                table: "local_models");

            migrationBuilder.DropColumn(
                name: "DraftMax",
                table: "local_models");

            migrationBuilder.DropColumn(
                name: "MinP",
                table: "local_models");

            migrationBuilder.DropColumn(
                name: "Mtp",
                table: "local_models");

            migrationBuilder.DropColumn(
                name: "Placement",
                table: "local_models");

            migrationBuilder.DropColumn(
                name: "PresencePenalty",
                table: "local_models");

            migrationBuilder.DropColumn(
                name: "Temperature",
                table: "local_models");

            migrationBuilder.DropColumn(
                name: "TopK",
                table: "local_models");

            migrationBuilder.DropColumn(
                name: "TopP",
                table: "local_models");

            migrationBuilder.DropColumn(
                name: "Ubatch",
                table: "local_models");

            migrationBuilder.DropColumn(
                name: "Yarn",
                table: "local_models");
        }
    }
}
