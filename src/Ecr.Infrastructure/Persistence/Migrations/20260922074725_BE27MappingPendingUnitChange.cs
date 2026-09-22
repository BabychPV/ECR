using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BE27MappingPendingUnitChange : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PendingSourceUnitCode",
                schema: "ext",
                table: "EntityFieldMap",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PendingSourceUnitDetectedAt",
                schema: "ext",
                table: "EntityFieldMap",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PendingSourceUnitId",
                schema: "ext",
                table: "EntityFieldMap",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PendingSourceUnitCode",
                schema: "ext",
                table: "EntityFieldMap");

            migrationBuilder.DropColumn(
                name: "PendingSourceUnitDetectedAt",
                schema: "ext",
                table: "EntityFieldMap");

            migrationBuilder.DropColumn(
                name: "PendingSourceUnitId",
                schema: "ext",
                table: "EntityFieldMap");
        }
    }
}
