using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FE01_FormulaError : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FormulaError",
                schema: "calc",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DocumentId = table.Column<long>(type: "bigint", nullable: false),
                    PeriodKey = table.Column<int>(type: "int", nullable: false),
                    TableInstanceId = table.Column<long>(type: "bigint", nullable: false),
                    TableDefId = table.Column<int>(type: "int", nullable: false),
                    SheetCopyId = table.Column<long>(type: "bigint", nullable: true),
                    TableRowId = table.Column<long>(type: "bigint", nullable: false),
                    RowKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ColumnDefId = table.Column<int>(type: "int", nullable: false),
                    FormulaDefId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ErrorCode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    InputsJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    SourcePeriodKey = table.Column<int>(type: "int", nullable: true),
                    SourceTableDefId = table.Column<int>(type: "int", nullable: true),
                    SourceRowKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SourceColumnDefId = table.Column<int>(type: "int", nullable: true),
                    Since = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FormulaError", x => x.Id);
                    table.CheckConstraint("CK_FormulaError_Kind", "Kind IN (N'Error', N'Blocked')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_FormulaError_Document",
                schema: "calc",
                table: "FormulaError",
                columns: new[] { "DocumentId", "PeriodKey", "SheetCopyId" });

            migrationBuilder.CreateIndex(
                name: "IX_FormulaError_Instance",
                schema: "calc",
                table: "FormulaError",
                columns: new[] { "PeriodKey", "TableInstanceId" });

            migrationBuilder.CreateIndex(
                name: "UX_FormulaError_Cell",
                schema: "calc",
                table: "FormulaError",
                columns: new[] { "PeriodKey", "TableRowId", "ColumnDefId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FormulaError",
                schema: "calc");
        }
    }
}
