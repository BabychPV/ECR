using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Q163RemoveFormulaDefShadowFk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FormulaDef_TableDef_TableDefId1",
                schema: "cfg",
                table: "FormulaDef");

            migrationBuilder.DropColumn(
                name: "TableDefId1",
                schema: "cfg",
                table: "FormulaDef");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TableDefId1",
                schema: "cfg",
                table: "FormulaDef",
                type: "int",
                nullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_FormulaDef_TableDef_TableDefId1",
                schema: "cfg",
                table: "FormulaDef",
                column: "TableDefId1",
                principalSchema: "cfg",
                principalTable: "TableDef",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
