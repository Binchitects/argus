using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ModelWindows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "model_windows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Days = table.Column<List<int>>(type: "integer[]", nullable: false, defaultValueSql: "'{}'::integer[]"),
                    Start = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    End = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    Keep = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    DefaultModel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_windows", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_model_windows_Name",
                table: "model_windows",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "model_windows");
        }
    }
}
