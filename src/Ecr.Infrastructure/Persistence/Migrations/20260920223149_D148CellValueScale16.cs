using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class D148CellValueScale16 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "ValueNumeric",
                schema: "doc",
                table: "CellValue",
                type: "decimal(28,16)",
                precision: 28,
                scale: 16,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(28,10)",
                oldPrecision: 28,
                oldScale: 10,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "ValueNumeric",
                schema: "doc",
                table: "CellValue",
                type: "decimal(28,10)",
                precision: 28,
                scale: 10,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(28,16)",
                oldPrecision: 28,
                oldScale: 16,
                oldNullable: true);
        }
    }
}
