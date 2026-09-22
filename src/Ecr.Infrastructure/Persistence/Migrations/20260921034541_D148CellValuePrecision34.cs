using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class D148CellValuePrecision34 : Migration
    {
        /// <summary>
        /// ⚠ Це ЄДИНА міграція серії, яка справді коштує місця. Ширина
        /// <c>decimal</c> у SQL Server залежить лише від precision, і 28 — межа
        /// категорії: 20–28 → 13 байтів, 29–38 → 17 (виміряно
        /// <c>sys.columns.max_length</c>). Тобто рядок <c>doc.CellValue</c>
        /// +4 Б, а це <b>+12.4 %</b> при 32.2 Б/рядок.
        ///
        /// ⛔ На таблиці з 108.9 млн рядків <c>ALTER COLUMN</c> зі зміною
        /// ширини — не метадані, а перезапис кожної сторінки під блокуванням:
        /// вікно обслуговування, не побічний ефект розгортання.
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "ValueNumeric",
                schema: "doc",
                table: "CellValue",
                type: "decimal(34,16)",
                precision: 34,
                scale: 16,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(28,16)",
                oldPrecision: 28,
                oldScale: 16,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
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
                oldType: "decimal(34,16)",
                oldPrecision: 34,
                oldScale: 16,
                oldNullable: true);
        }
    }
}
