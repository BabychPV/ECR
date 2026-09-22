using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BE08JobProgressCreatedErrorDocument : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                schema: "itg",
                table: "JobProgress",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DocumentId",
                schema: "itg",
                table: "JobProgress",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErrorCode",
                schema: "itg",
                table: "JobProgress",
                type: "varchar(32)",
                unicode: false,
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedAt",
                schema: "itg",
                table: "JobProgress");

            migrationBuilder.DropColumn(
                name: "DocumentId",
                schema: "itg",
                table: "JobProgress");

            migrationBuilder.DropColumn(
                name: "ErrorCode",
                schema: "itg",
                table: "JobProgress");
        }
    }
}
