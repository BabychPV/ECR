using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class D148CalculationScale16 : Migration
    {
        /// <summary>
        /// ⚠ <c>precision: 28, scale: 10</c> в аргументах нижче — НЕ тип
        /// стовпця. Це фасет моделі, який поки задає глобальна конвенція
        /// <c>EcrDbContext.ConfigureConventions</c> (вона переходить на (28,16)
        /// останнім комітом серії <c>D-148</c>). DDL генерується з
        /// <c>type:</c>, тобто з <c>decimal(28,16)</c>.
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ⛔ `calc.CalculationResult.Value` входить до `IX_CalculationResult_Lookup`
            // INCLUDE-стовпцем, а `ALTER COLUMN` на такому стовпці SQL Server
            // ВІДХИЛЯЄ (5074, «one or more objects access this column»).
            // Перевірено дією на зонді: індекс із `INCLUDE (b)` не дає змінити
            // `b`. EF цього не знає — сам індекс у моделі не змінився, тож він
            // його й не чіпає, і міграція без цього блоку просто падає.
            //
            // ⚠ Індекс ставиться назад на ТЕ САМЕ місце, з якого знятий
            // (`ps_ByPeriodKey`). `CREATE INDEX` без `ON` мовчки поклав би його
            // на `PRIMARY`: індекс лишився б робочим, але НЕвирівняним зі
            // схемою партиціонування — а на невирівняному індексі відпадають
            // партиційні операції по `calc.*`. Місце читається з
            // `sys.data_spaces`, а не задається літералом: у профілі без
            // партиціонування (Express) індекс стоїть на файловій групі, і
            // літерал зламав би саме її.
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
                name: "Tolerance",
                schema: "calc",
                table: "TestCase",
                type: "decimal(28,16)",
                precision: 28,
                scale: 10,
                nullable: false,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "decimal(28,10)",
                oldPrecision: 28,
                oldScale: 10,
                oldDefaultValue: 0m);

            migrationBuilder.AlterColumn<decimal>(
                name: "Value",
                schema: "calc",
                table: "MethodologyConstant",
                type: "decimal(28,16)",
                precision: 28,
                scale: 10,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(28,10)",
                oldPrecision: 28,
                oldScale: 10,
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "Value",
                schema: "calc",
                table: "CalculationStep",
                type: "decimal(28,16)",
                precision: 28,
                scale: 10,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(28,10)",
                oldPrecision: 28,
                oldScale: 10,
                oldNullable: true);

            // ⚠ `calc.CalculationResult.Value` уже переведено блоком вище разом
            // зі зняттям індексу; окремого `AlterColumn` тут НЕМАЄ навмисно —
            // повторний `ALTER COLUMN` після відновлення індексу впав би з тією
            // самою 5074.
            migrationBuilder.AlterColumn<decimal>(
                name: "Value",
                schema: "calc",
                table: "CalculationInput",
                type: "decimal(28,16)",
                precision: 28,
                scale: 10,
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
                name: "Tolerance",
                schema: "calc",
                table: "TestCase",
                type: "decimal(28,10)",
                precision: 28,
                scale: 10,
                nullable: false,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "decimal(28,16)",
                oldPrecision: 28,
                oldScale: 10,
                oldDefaultValue: 0m);

            migrationBuilder.AlterColumn<decimal>(
                name: "Value",
                schema: "calc",
                table: "MethodologyConstant",
                type: "decimal(28,10)",
                precision: 28,
                scale: 10,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(28,16)",
                oldPrecision: 28,
                oldScale: 10,
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "Value",
                schema: "calc",
                table: "CalculationStep",
                type: "decimal(28,10)",
                precision: 28,
                scale: 10,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(28,16)",
                oldPrecision: 28,
                oldScale: 10,
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
                    ALTER COLUMN Value decimal(28,10) NOT NULL;

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
                type: "decimal(28,10)",
                precision: 28,
                scale: 10,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(28,16)",
                oldPrecision: 28,
                oldScale: 10,
                oldNullable: true);
        }
    }
}
