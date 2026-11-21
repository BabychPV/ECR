using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// ✎ L1b / D1 (стенд Land 21.11): на всіх індексах <c>calc.CalculationResult</c>, <c>calc.CalculationStep</c>,
    /// <c>calc.CalculationInput</c> — лише рядкові замки (<c>ALLOW_PAGE_LOCKS = OFF</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Одночасні записи результатів того самого періоду різними гілками (<c>SqlBulkCopy</c> у транзакції методології)
    /// ставали жертвами взаємного блокування на X-замках СТОРІНОК різних індексів (кластерний <c>(PeriodKey, Id)</c> ↔
    /// <c>IX_CalculationResult_DocRun</c>): ключі гілок не перетинаються, спільні лише сторінки на межах діапазонів. Із
    /// рядковими замками чекати нема на що. Повне обґрунтування — блок L1b у <c>07-partition-tables.sql</c>.
    /// </para>
    /// <para>
    /// ⚠ Головне місце — <c>07-partition-tables.sql</c>: його перебудови (<c>DROP_EXISTING</c>) скидають опцію, тож він ставить
    /// її сам наприкінці. Міграція — для бази, яку оновлює лише <c>MigrateAsync</c> старту. Ідемпотентно (лише індекси,
    /// де опція ще <c>ON</c>), метадані, усі редакції, SQL Server 2016+; зайнятий індекс (1222 за 5 с) пропускається — його
    /// доробить 07. Модель не змінюється, знімок той самий.
    /// </para>
    /// </remarks>
    public partial class L1bCalcRowLocksOnly : Migration
    {
        /// <summary>T-SQL, що ставить <c>ALLOW_PAGE_LOCKS</c> у <paramref name="value"/> на індексах трьох таблиць розрахунку.</summary>
        /// <param name="value"><c>OFF</c> (Up) або <c>ON</c> (Down).</param>
        /// <returns>Текст T-SQL.</returns>
        public static string SetPageLocksSql(string value)
            => $$"""
                DECLARE @l1bSql nvarchar(max) = (
                    SELECT N'BEGIN TRY ALTER INDEX ' + QUOTENAME(i.name) + N' ON calc.' + QUOTENAME(t.name)
                           + N' SET (ALLOW_PAGE_LOCKS = {{value}}); END TRY BEGIN CATCH IF ERROR_NUMBER() <> 1222 THROW; '
                           + N'PRINT N''L1b: ' + t.name + N'.' + i.name + N' busy (1222), skipped; 07-partition-tables.sql repeats it.''; END CATCH;'
                           + NCHAR(10)
                      FROM sys.indexes AS i
                      JOIN sys.tables AS t ON t.object_id = i.object_id
                     WHERE SCHEMA_NAME(t.schema_id) = N'calc'
                       AND t.name IN (N'CalculationResult', N'CalculationStep', N'CalculationInput')
                       AND i.type IN (1, 2)
                       AND i.allow_page_locks = {{(value == "OFF" ? 1 : 0)}}
                     ORDER BY t.name, i.index_id
                       FOR XML PATH(''), TYPE).value(N'.', N'nvarchar(max)');

                IF @l1bSql IS NOT NULL
                BEGIN
                    SET @l1bSql = N'SET LOCK_TIMEOUT 5000;' + NCHAR(10) + @l1bSql + N'SET LOCK_TIMEOUT -1;';
                    EXEC sys.sp_executesql @l1bSql;
                END
                """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SetPageLocksSql("OFF"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SetPageLocksSql("ON"));
        }
    }
}
