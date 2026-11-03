using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NAE2_NodeTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowsSelfNesting",
                schema: "cfg",
                table: "RegistryLevelDef",
                type: "bit",
                nullable: false,
                defaultValue: false)
                .Annotation("Relational:DefaultConstraintName", "DF_RegLevel_SelfNest");

            migrationBuilder.AddColumn<int>(
                name: "LevelId",
                schema: "cfg",
                table: "RegistryFieldDef",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RegistryFieldDef_Level",
                schema: "cfg",
                table: "RegistryFieldDef",
                column: "LevelId",
                filter: "[LevelId] IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_RegField_Level",
                schema: "cfg",
                table: "RegistryFieldDef",
                column: "LevelId",
                principalSchema: "cfg",
                principalTable: "RegistryLevelDef",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RegField_Level",
                schema: "cfg",
                table: "RegistryFieldDef");

            migrationBuilder.DropIndex(
                name: "IX_RegistryFieldDef_Level",
                schema: "cfg",
                table: "RegistryFieldDef");

            migrationBuilder.DropColumn(
                name: "AllowsSelfNesting",
                schema: "cfg",
                table: "RegistryLevelDef")
                .Annotation("Relational:DefaultConstraintName", "DF_RegLevel_SelfNest");

            migrationBuilder.DropColumn(
                name: "LevelId",
                schema: "cfg",
                table: "RegistryFieldDef");
        }
    }
}
