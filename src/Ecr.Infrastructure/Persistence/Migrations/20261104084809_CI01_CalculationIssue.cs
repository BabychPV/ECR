using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CI01_CalculationIssue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CalculationIssue",
                schema: "calc",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DocumentId = table.Column<long>(type: "bigint", nullable: false),
                    PeriodKey = table.Column<int>(type: "int", nullable: false),
                    TableDefId = table.Column<int>(type: "int", nullable: false),
                    SheetCopyId = table.Column<long>(type: "bigint", nullable: true),
                    RowKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RowNumber = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    MethodologyId = table.Column<int>(type: "int", nullable: true),
                    CalculationRunId = table.Column<long>(type: "bigint", nullable: false),
                    DetectedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalculationIssue", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CalculationIssue_Address",
                schema: "calc",
                table: "CalculationIssue",
                columns: new[] { "DocumentId", "PeriodKey", "TableDefId", "SheetCopyId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CalculationIssue",
                schema: "calc");
        }
    }
}
