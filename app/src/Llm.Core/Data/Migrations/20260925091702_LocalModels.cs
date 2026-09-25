using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class LocalModels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "local_models",
                columns: table => new
                {
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    File = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Projector = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Context = table.Column<int>(type: "integer", nullable: false),
                    MaxOutput = table.Column<int>(type: "integer", nullable: true),
                    GpuLayers = table.Column<int>(type: "integer", nullable: false),
                    CpuMoe = table.Column<int>(type: "integer", nullable: false),
                    KvType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Parallel = table.Column<int>(type: "integer", nullable: false),
                    ExtraPreset = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Thinking = table.Column<bool>(type: "boolean", nullable: false),
                    Tools = table.Column<bool>(type: "boolean", nullable: false),
                    InputPerMtok = table.Column<decimal>(type: "numeric", nullable: true),
                    OutputPerMtok = table.Column<decimal>(type: "numeric", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_models", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "model_access",
                columns: table => new
                {
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Audience = table.Column<int>(type: "integer", nullable: false),
                    Groups = table.Column<List<Guid>>(type: "uuid[]", nullable: false, defaultValueSql: "'{}'::uuid[]"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_access", x => x.Model);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "local_models");

            migrationBuilder.DropTable(
                name: "model_access");
        }
    }
}
