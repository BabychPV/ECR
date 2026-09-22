using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class D148CalculationPrecision34 : Migration
    {
        /// <summary>
        /// ⚠ <c>precision: 28, scale: 16</c> в аргументах нижче — НЕ тип
        /// стовпця. Це фасет моделі, який поки задає глобальна конвенція
        /// <c>EcrDbContext.ConfigureConventions</c> (вона переходить на (34,16)
        /// останнім комітом серії). DDL генерується з <c>type:</c>, тобто з
        /// <c>decimal(34,16)</c>.
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "Tolerance",
                schema: "calc",
                table: "TestCase",
                type: "decimal(34,16)",
                precision: 28,
                scale: 16,
                nullable: false,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "decimal(28,16)",
                oldPrecision: 28,
                oldScale: 16,
                oldDefaultValue: 0m);

            migrationBuilder.AlterColumn<decimal>(
                name: "Value",
                schema: "calc",
                table: "MethodologyConstant",
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
                name: "Value",
                schema: "calc",
                table: "CalculationStep",
                type: "decimal(34,16)",
                precision: 28,
                scale: 16,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(28,16)",
                oldPrecision: 28,
                oldScale: 16,
                oldNullable: true);

            // ⛔ `calc.CalculationResult.Value` входить до
            // `IX_CalculationResult_Lookup` INCLUDE-стовпцем, а `ALTER COLUMN`
            // на такому стовпці SQL Server ВІДХИЛЯЄ (5074). EF цього не знає —
            // сам індекс у моделі не змінився, тож зісканований `AlterColumn`
            // (він був тут і його замінено) просто падав би. Блок той самий,
            // що в `D148CalculationScale16`: зняти індекс → змінити →
            // повернути на те саме місце, прочитане з `sys.data_spaces`.
            //
            // ⚠ Пояснення «без `ON` індекс ляже на `PRIMARY`», успадковане від
            // `D148CalculationScale16`, НЕПРАВДИВЕ — перевірено дією: блок без
            // `ON` на вже партиційованій базі лишив індекс на
            // `ps_ByPeriodKey`. Некластерний індекс без `ON` успадковує схему
            // БАЗОВОЇ таблиці, а вона партиційована. Тобто `@place` тут —
            // захист від випадку, коли індекс стоїть НЕ там, де таблиця, а не
            // від «мовчки на PRIMARY». Блок лишається (він правильний і
            // явний), але причина названа та, що є.
            migrationBuilder.Sql("""
                DECLARE @place nvarchar(300) =
                (
                    SELECT QUOTENAME(ds.name)
                         + CASE WHEN ds.type = 'PS' THEN N'(PeriodKey)' ELSE N'' END
                    FROM sys.indexes AS i
                    JOIN sys.data_spaces AS ds ON ds.data_space_id = i.data_space_id
                    WHERE i.object_id = OBJECT_ID(N'calc.CalculationResult')
                      AND i.name = N'IX_CalculationResult_Lookup'
                );

                DROP INDEX IX_CalculationResult_Lookup ON calc.CalculationResult;

                ALTER TABLE calc.CalculationResult
                    ALTER COLUMN Value decimal(34,16) NOT NULL;

                DECLARE @create nvarchar(max) =
                    N'CREATE NONCLUSTERED INDEX IX_CalculationResult_Lookup
                        ON calc.CalculationResult (PeriodKey, DocumentId, MethodologyVersionId, OutputCode)
                        INCLUDE (Value, UnitId, SubstanceEntryId, SourceRowKey)'
                    + ISNULL(N' ON ' + @place, N'') + N';';

                EXEC sys.sp_executesql @create;
                """);

            migrationBuilder.AlterColumn<decimal>(
                name: "Value",
                schema: "calc",
                table: "CalculationInput",
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
                name: "Tolerance",
                schema: "calc",
                table: "TestCase",
                type: "decimal(28,16)",
                precision: 28,
                scale: 16,
                nullable: false,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "decimal(34,16)",
                oldPrecision: 28,
                oldScale: 16,
                oldDefaultValue: 0m);

            migrationBuilder.AlterColumn<decimal>(
                name: "Value",
                schema: "calc",
                table: "MethodologyConstant",
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
                name: "Value",
                schema: "calc",
                table: "CalculationStep",
                type: "decimal(28,16)",
                precision: 28,
                scale: 16,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(34,16)",
                oldPrecision: 28,
                oldScale: 16,
                oldNullable: true);

            // Дзеркало блоку з `Up`: індекс знімається, стовпець звужується,
            // індекс повертається на те саме місце.
            migrationBuilder.Sql("""
                DECLARE @place nvarchar(300) =
                (
                    SELECT QUOTENAME(ds.name)
                         + CASE WHEN ds.type = 'PS' THEN N'(PeriodKey)' ELSE N'' END
                    FROM sys.indexes AS i
                    JOIN sys.data_spaces AS ds ON ds.data_space_id = i.data_space_id
                    WHERE i.object_id = OBJECT_ID(N'calc.CalculationResult')
                      AND i.name = N'IX_CalculationResult_Lookup'
                );

                DROP INDEX IX_CalculationResult_Lookup ON calc.CalculationResult;

                ALTER TABLE calc.CalculationResult
                    ALTER COLUMN Value decimal(28,16) NOT NULL;

                DECLARE @create nvarchar(max) =
                    N'CREATE NONCLUSTERED INDEX IX_CalculationResult_Lookup
                        ON calc.CalculationResult (PeriodKey, DocumentId, MethodologyVersionId, OutputCode)
                        INCLUDE (Value, UnitId, SubstanceEntryId, SourceRowKey)'
                    + ISNULL(N' ON ' + @place, N'') + N';';

                EXEC sys.sp_executesql @create;
                """);

            migrationBuilder.AlterColumn<decimal>(
                name: "Value",
                schema: "calc",
                table: "CalculationInput",
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
