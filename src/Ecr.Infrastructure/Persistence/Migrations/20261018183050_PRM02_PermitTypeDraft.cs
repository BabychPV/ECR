using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PRM02_PermitTypeDraft : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DraftJson",
                schema: "cfg",
                table: "PermitTypeDef",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                schema: "cfg",
                table: "PermitTypeDef",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UpdatedByUserId",
                schema: "cfg",
                table: "PermitTypeDef",
                type: "int",
                nullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_PermitType_UpdatedBy",
                schema: "cfg",
                table: "PermitTypeDef",
                column: "UpdatedByUserId",
                principalSchema: "sec",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PermitType_UpdatedBy",
                schema: "cfg",
                table: "PermitTypeDef");

            migrationBuilder.DropColumn(
                name: "DraftJson",
                schema: "cfg",
                table: "PermitTypeDef");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                schema: "cfg",
                table: "PermitTypeDef");

            migrationBuilder.DropColumn(
                name: "UpdatedByUserId",
                schema: "cfg",
                table: "PermitTypeDef");
        }
    }
}
