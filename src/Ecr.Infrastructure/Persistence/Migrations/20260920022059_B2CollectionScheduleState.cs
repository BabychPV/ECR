using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class B2CollectionScheduleState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastError",
                schema: "ext",
                table: "CollectionSchedule",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastErrorAt",
                schema: "ext",
                table: "CollectionSchedule",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                schema: "ext",
                table: "CollectionSchedule",
                type: "rowversion",
                rowVersion: true,
                nullable: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastError",
                schema: "ext",
                table: "CollectionSchedule");

            migrationBuilder.DropColumn(
                name: "LastErrorAt",
                schema: "ext",
                table: "CollectionSchedule");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                schema: "ext",
                table: "CollectionSchedule");
        }
    }
}
