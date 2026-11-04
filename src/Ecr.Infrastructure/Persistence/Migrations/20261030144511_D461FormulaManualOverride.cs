using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class D461FormulaManualOverride : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowManualOverride",
                schema: "cfg",
                table: "FormulaDef",
                type: "bit",
                nullable: false,
                defaultValue: false)
                .Annotation("Relational:DefaultConstraintName", "DF_Formula_AllowOverride");

            migrationBuilder.CreateTable(
                name: "CellFormulaOverride",
                schema: "doc",
                columns: table => new
                {
                    PeriodKey = table.Column<int>(type: "int", nullable: false),
                    TableRowId = table.Column<long>(type: "bigint", nullable: false),
                    ColumnDefId = table.Column<int>(type: "int", nullable: false),
                    TableInstanceId = table.Column<long>(type: "bigint", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    SetByUserId = table.Column<int>(type: "int", nullable: false),
                    SetAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CellFormulaOverride", x => new { x.PeriodKey, x.TableRowId, x.ColumnDefId });
                    table.CheckConstraint("CK_CellFormulaOverride_Reason", "LEN(Reason) > 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_CellFormulaOverride_Instance",
                schema: "doc",
                table: "CellFormulaOverride",
                columns: new[] { "PeriodKey", "TableInstanceId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CellFormulaOverride",
                schema: "doc");

            migrationBuilder.DropColumn(
                name: "AllowManualOverride",
                schema: "cfg",
                table: "FormulaDef")
                .Annotation("Relational:DefaultConstraintName", "DF_Formula_AllowOverride");
        }
    }
}
