// src/Ecr.Infrastructure/Persistence/D148ScalePrecheck.cs
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Передперевірка перед <c>D148*Scale16</c> (аудит D1): значення, яке
/// <c>decimal(28,16)</c> не вміщує, зупиняє оновлення ДО зміни схеми —
/// з іменем стовпця, кількістю рядків і максимумом, а не SQL-помилкою 8115.
/// </summary>
/// <remarks>
/// ⚠ Три міграції <c>D148CellValueScale16</c>, <c>D148CalculationScale16</c>,
/// <c>D148ReportingAndSourceScale16</c> переводять десять стовпців із
/// <c>decimal(28,10)</c> у <c>decimal(28,16)</c>. Ціла частина при цьому
/// ТИМЧАСОВО скорочується з 18 розрядів до 12: наступні
/// <c>D148*Precision34</c> повертають 18 (<c>decimal(34,16)</c>). Тобто
/// <c>|x| ≥ 1e12</c> — не межа домену, а артефакт проміжного кроку: кінцевий
/// тип такі значення вміщує. Саме тому передперевірка відмовляє, а не
/// «обрізає»: дані цілі, треба лише пройти проміжний крок (процедура —
/// <c>docs/admin/operations-runbook.md</c> §8.1).
///
/// ⛔ Застосовані міграції не переписуються: їхні тіла й Designer лишаються як
/// є. Перевірку вставляє генератор SQL (<see cref="EcrMigrationsSqlGenerator"/>)
/// у SQL кожної з трьох міграцій першою командою, тож вона діє однаково і для
/// <c>MigrateAsync</c> (режим <c>StartupMode=Migrate</c>, тести), і для
/// <c>dotnet ef migrations script --idempotent</c>, яким розгортають прод
/// (<c>tools/deploy-ecr.ps1</c>, пакет <c>tools/build-installer.ps1</c>,
/// <c>tools/setup-dev-db.ps1</c>). На базі, де міграцію вже застосовано,
/// ідемпотентний скрипт її не виконує — і перевірку разом із нею.
///
/// ⚠ Перевіряються ВСІ десять стовпців, а не лише стовпці поточної міграції:
/// відмова має прийти до ПЕРШОЇ зміни схеми, інакше <c>D148CellValueScale16</c>
/// встигла б закомітитись, а впала б друга. Стовпець, уже переведений
/// (не <c>decimal(28,10)</c>), пропускається — тому та сама перевірка
/// коректна й на базі, де попередня спроба дійшла до середини серії.
///
/// ⚠ Ціна — повний прохід кожної таблиці (індексу за значенням немає). На
/// 108.9 млн рядків <c>doc.CellValue</c> це хвилини, але сам
/// <c>ALTER COLUMN</c> зі зміною масштабу так само проходить кожен рядок.
/// </remarks>
public static class D148ScalePrecheck
{
    /// <summary>Номер помилки <c>THROW</c>: «D148 не вміщує значення».</summary>
    public const int ErrorNumber = 50148;

    /// <summary>Тип, з якого серія звужує.</summary>
    private const string FromType = "decimal(28,10)";

    /// <summary>Тип, у який звужують <c>*Scale16</c>.</summary>
    private const string ToType = "decimal(28,16)";

    /// <summary>Десять стовпців, які звужують три міграції <c>D148*Scale16</c>.</summary>
    /// <remarks>
    /// ⚠ <c>calc.CalculationResult.Value</c> звужується сирим SQL (через індекс
    /// з <c>INCLUDE</c>), а не <c>AlterColumnOperation</c>, — тому перелік
    /// явний, а не зібраний з операцій.
    /// </remarks>
    public static IReadOnlyList<(string Schema, string Table, string Column)> Columns { get; } =
    [
        ("doc", "CellValue", "ValueNumeric"),
        ("calc", "CalculationResult", "Value"),
        ("calc", "TestCase", "Tolerance"),
        ("calc", "MethodologyConstant", "Value"),
        ("calc", "CalculationStep", "Value"),
        ("calc", "CalculationInput", "Value"),
        ("rpt", "ReportRow", "ValueNumeric"),
        ("dic", "RegistryValue", "ValueNumeric"),
        ("ext", "RawDataPoint", "ValueNumeric"),
        ("doc", "DocumentIndexValue", "ValueNumeric"),
    ];

    /// <summary>
    /// Чи несе набір операцій міграції звуження <c>decimal(28,10) → decimal(28,16)</c>,
    /// тобто чи це одна з трьох <c>D148*Scale16</c>.
    /// </summary>
    /// <param name="operations">Операції <c>Up</c> однієї міграції.</param>
    /// <returns><c>true</c>, якщо перед ними потрібна передперевірка.</returns>
    public static bool Applies(IEnumerable<MigrationOperation> operations)
        => operations.Any(op => op is AlterColumnOperation alter
                                && string.Equals(alter.ColumnType, ToType, StringComparison.OrdinalIgnoreCase)
                                && string.Equals(alter.OldColumn.ColumnType, FromType, StringComparison.OrdinalIgnoreCase));

    /// <summary>T-SQL передперевірки: <c>THROW 50148</c> з переліком, якщо є що звітувати.</summary>
    public static string Sql { get; } = BuildSql();

    private static string BuildSql()
    {
        var values = string.Join(
            ",\n        ",
            Columns.Select(c => $"(N'{c.Schema}', N'{c.Table}', N'{c.Column}')"));

        return $"""
            DECLARE @d148Report nvarchar(max) = N'';
            DECLARE @d148Schema sysname, @d148Table sysname, @d148Column sysname;
            DECLARE @d148Count bigint, @d148Max decimal(28,10), @d148Query nvarchar(max);

            DECLARE d148Columns CURSOR LOCAL FAST_FORWARD FOR
                SELECT v.SchemaName, v.TableName, v.ColumnName
                FROM (VALUES
                    {values}
                ) AS v (SchemaName, TableName, ColumnName)
                JOIN sys.columns AS c
                  ON c.object_id = OBJECT_ID(QUOTENAME(v.SchemaName) + N'.' + QUOTENAME(v.TableName))
                 AND c.name = v.ColumnName
                WHERE c.precision = 28 AND c.scale = 10;

            OPEN d148Columns;
            FETCH NEXT FROM d148Columns INTO @d148Schema, @d148Table, @d148Column;
            WHILE @@FETCH_STATUS = 0
            BEGIN
                SET @d148Query =
                      N'SELECT @n = COUNT_BIG(*), @m = MAX(ABS(' + QUOTENAME(@d148Column) + N'))'
                    + N' FROM ' + QUOTENAME(@d148Schema) + N'.' + QUOTENAME(@d148Table)
                    + N' WHERE ' + QUOTENAME(@d148Column) + N' >= 1000000000000'
                    + N' OR ' + QUOTENAME(@d148Column) + N' <= -1000000000000;';

                EXEC sys.sp_executesql @d148Query,
                    N'@n bigint OUTPUT, @m decimal(28,10) OUTPUT',
                    @n = @d148Count OUTPUT, @m = @d148Max OUTPUT;

                IF @d148Count > 0
                    SET @d148Report = @d148Report + NCHAR(10) + N'  '
                        + @d148Schema + N'.' + @d148Table + N'.' + @d148Column
                        + N': рядків ' + CAST(@d148Count AS nvarchar(20))
                        + N', max |x| = ' + CAST(@d148Max AS nvarchar(40));

                FETCH NEXT FROM d148Columns INTO @d148Schema, @d148Table, @d148Column;
            END
            CLOSE d148Columns;
            DEALLOCATE d148Columns;

            IF @d148Report <> N''
            BEGIN
                DECLARE @d148Message nvarchar(2048) = LEFT(
                      N'Передперевірка D148: оновлення зупинено ДО зміни схеми. '
                    + N'Міграції D148*Scale16 переводять стовпці з decimal(28,10) у decimal(28,16) '
                    + N'(ціла частина тимчасово 12 розрядів замість 18; D148*Precision34 повертає 18). '
                    + N'Значення з |x| >= 1e12 не вміщуються, і SQL Server дав би 8115 без імені стовпця. '
                    + N'Поза межею:' + @d148Report + NCHAR(10)
                    + N'Схему й дані не змінено. Що робити: docs/admin/operations-runbook.md, п. 8.1.',
                    2048);
                THROW {ErrorNumber}, @d148Message, 1;
            END
            """;
    }
}
