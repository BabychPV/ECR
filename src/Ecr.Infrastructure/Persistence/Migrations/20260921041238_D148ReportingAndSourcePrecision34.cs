using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class D148ReportingAndSourcePrecision34 : Migration
    {
        /// <summary>
        /// ⚠ <c>precision: 28, scale: 16</c> в аргументах нижче — фасет моделі
        /// від глобальної конвенції <c>EcrDbContext</c>, не тип стовпця. DDL
        /// генерується з <c>type:</c>, тобто з <c>decimal(34,16)</c>; конвенція
        /// переходить на (34,16) наступним комітом серії.
        ///
        /// ⚠ Жоден із цих чотирьох стовпців не входить до індексу — на відміну
        /// від <c>calc.CalculationResult.Value</c> у
        /// <c>D148CalculationPrecision34</c>, тут вистачає простого
        /// <c>AlterColumn</c>, і зісканований EF код лишився як є.
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "ValueNumeric",
                schema: "rpt",
                table: "ReportRow",
                type: "decimal(34,16)",
                precision: 28,
                scale: 16,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(28,16)",
                oldPrecision: 28,
                oldScale: 16,
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "ValueNumeric",
                schema: "dic",
                table: "RegistryValue",
                type: "decimal(34,16)",
                precision: 28,
                scale: 16,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(28,16)",
                oldPrecision: 28,
                oldScale: 16,
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "ValueNumeric",
                schema: "ext",
                table: "RawDataPoint",
                type: "decimal(34,16)",
                precision: 28,
                scale: 16,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(28,16)",
                oldPrecision: 28,
                oldScale: 16,
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "ValueNumeric",
                schema: "doc",
                table: "DocumentIndexValue",
                type: "decimal(34,16)",
                precision: 28,
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
                schema: "rpt",
                table: "ReportRow",
                type: "decimal(28,16)",
                precision: 28,
                scale: 16,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(34,16)",
                oldPrecision: 28,
                oldScale: 16,
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "ValueNumeric",
                schema: "dic",
                table: "RegistryValue",
                type: "decimal(28,16)",
                precision: 28,
                scale: 16,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(34,16)",
                oldPrecision: 28,
                oldScale: 16,
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "ValueNumeric",
                schema: "ext",
                table: "RawDataPoint",
                type: "decimal(28,16)",
                precision: 28,
                scale: 16,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(34,16)",
                oldPrecision: 28,
                oldScale: 16,
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "ValueNumeric",
                schema: "doc",
                table: "DocumentIndexValue",
                type: "decimal(28,16)",
                precision: 28,
                scale: 16,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(34,16)",
                oldPrecision: 28,
                oldScale: 16,
                oldNullable: true);
        }
    }
}
