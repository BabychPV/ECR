using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UiFieldsTemplateRegistryTimestamps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                schema: "cfg",
                table: "TemplateVersion",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UpdatedByUserId",
                schema: "cfg",
                table: "TemplateVersion",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ArchivedAt",
                schema: "cfg",
                table: "Template",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DefinitionUpdatedAt",
                schema: "cfg",
                table: "RegistryDef",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DefinitionUpdatedByUserId",
                schema: "cfg",
                table: "RegistryDef",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                schema: "cfg",
                table: "TemplateVersion");

            migrationBuilder.DropColumn(
                name: "UpdatedByUserId",
                schema: "cfg",
                table: "TemplateVersion");

            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                schema: "cfg",
                table: "Template");

            migrationBuilder.DropColumn(
                name: "DefinitionUpdatedAt",
                schema: "cfg",
                table: "RegistryDef");

            migrationBuilder.DropColumn(
                name: "DefinitionUpdatedByUserId",
                schema: "cfg",
                table: "RegistryDef");
        }
    }
}
