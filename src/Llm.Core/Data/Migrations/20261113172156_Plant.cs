using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class Plant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "plant_sites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    TimeZone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    GroupIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false, defaultValueSql: "'{}'::uuid[]"),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plant_sites", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "plant_areas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plant_areas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_plant_areas_plant_areas_ParentId",
                        column: x => x.ParentId,
                        principalTable: "plant_areas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_plant_areas_plant_sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "plant_sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "plant_assets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    AreaId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Vendor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Serial = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Tags = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    X = table.Column<double>(type: "double precision", nullable: true),
                    Y = table.Column<double>(type: "double precision", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plant_assets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_plant_assets_plant_areas_AreaId",
                        column: x => x.AreaId,
                        principalTable: "plant_areas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_plant_assets_plant_sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "plant_sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "plant_points",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    DataType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Unit = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LowLow = table.Column<double>(type: "double precision", nullable: true),
                    Low = table.Column<double>(type: "double precision", nullable: true),
                    High = table.Column<double>(type: "double precision", nullable: true),
                    HighHigh = table.Column<double>(type: "double precision", nullable: true),
                    Deadband = table.Column<double>(type: "double precision", nullable: true),
                    States = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plant_points", x => x.Id);
                    table.ForeignKey(
                        name: "FK_plant_points_plant_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "plant_assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_plant_areas_ParentId",
                table: "plant_areas",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_plant_areas_SiteId_ParentId_Position",
                table: "plant_areas",
                columns: new[] { "SiteId", "ParentId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_plant_assets_AreaId",
                table: "plant_assets",
                column: "AreaId");

            migrationBuilder.CreateIndex(
                name: "IX_plant_assets_SiteId_Name",
                table: "plant_assets",
                columns: new[] { "SiteId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_plant_points_AssetId_Name",
                table: "plant_points",
                columns: new[] { "AssetId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_plant_points_Source_Address",
                table: "plant_points",
                columns: new[] { "Source", "Address" });

            migrationBuilder.CreateIndex(
                name: "IX_plant_sites_Name",
                table: "plant_sites",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "plant_points");

            migrationBuilder.DropTable(
                name: "plant_assets");

            migrationBuilder.DropTable(
                name: "plant_areas");

            migrationBuilder.DropTable(
                name: "plant_sites");
        }
    }
}
