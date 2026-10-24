// tests/Ecr.Infrastructure.Tests/Persistence/ArchiveMirrorTests.cs
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Кожна колонка <c>calc.CalculationResult</c> і <c>calc.CalculationStep</c> має
/// дзеркало в <c>arc.*</c> того самого типу й NULL-придатності
/// (<c>12-archive-tables.sql</c>, FEATURE-HSE301-VIEW §7.1, архів <c>D-148</c>).
/// ✎ D-447: те саме для журналу комірок <c>aud.CellChange</c> — і архіву <c>arc.CellChange</c>, і цілі
/// SWITCH <c>arc.AuditCellChange</c>.
/// </summary>
/// <remarks>
/// ⛔ Звіряється РОЗГОРНУТА база, а не текст скрипту: `arc.*` створює скрипт
/// поза моделлю EF, і нова колонка в `calc.*` (міграцією) нічим не змушує
/// оновити архів. Колонка без дзеркала губиться при архівації мовчки — рік
/// повертається з архіву без неї, і ніщо не падає.
///
/// ⚠ Знайдено цим сторожем у кроці F6: `calc.CalculationStep.MaskedZero`
/// (<c>H-24d-1</c>) дзеркала не мав.
///
/// ⚠ Лише `calc`-пари. Для `doc.*` загальне правило «архів ⊇ джерело» не діє
/// (архів свідомо без `rowversion` тощо). `doc.TableRow.IsOrphaned` з D4 аудиту
/// дзеркало вже має — його перевіряє `RestoreYearTests` наскрізним сценарієм.
///
/// Мутаційний доказ (F6): прибрати `ALTER TABLE arc.CalculationResult ADD Kind` зі
/// скрипту — червоніє рядок <c>calc.CalculationResult</c>; прибрати
/// `arc.CalculationStep.SourceRowKey` — рядок <c>calc.CalculationStep</c>.
/// </remarks>
[Collection("SqlServer")]
public sealed class ArchiveMirrorTests(SqlServerFixture sql)
{
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-F6")]
    [InlineData("calc.CalculationResult", "arc.CalculationResult")]
    [InlineData("calc.CalculationStep", "arc.CalculationStep")]
    [InlineData("aud.CellChange", "arc.CellChange")]
    [InlineData("aud.CellChange", "arc.AuditCellChange")]
    public async Task Кожна_колонка_джерела_має_дзеркало_в_архіві(string source, string archive)
    {
        var sourceColumns = await ColumnsAsync(source);
        var archiveColumns = await ColumnsAsync(archive);

        // Порожній перелік джерела означав би, що звіряти нема з чим, — і сторож
        // був би зелений на будь-якій базі.
        Assert.NotEmpty(sourceColumns);

        Assert.Empty(sourceColumns.Except(archiveColumns, StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// ✎ D-447: прапорець «перезаписав чужу правку» (<c>IsOverwrite bit NOT NULL DEFAULT(0)</c>) є в журналі
    /// й в обох дзеркалах, а в цілі SWITCH стоїть на ТІЙ САМІЙ позиції, що й у джерелі.
    /// </summary>
    /// <remarks>
    /// ⛔ Мутація: прибрати з <c>12-archive-tables.sql</c> <c>ALTER TABLE arc.AuditCellChange ADD IsOverwrite</c>
    /// і колонку з її <c>CREATE TABLE</c> — червоніє рядок <c>arc.AuditCellChange</c> (а з ним і SWITCH
    /// партиції журналу в архів, Msg 4943). Позиція звіряється окремо: SWITCH вимагає того самого порядку
    /// колонок, а перелік імен порядку не бачить.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-447")]
    [InlineData("aud.CellChange")]
    [InlineData("arc.CellChange")]
    [InlineData("arc.AuditCellChange")]
    public async Task D447_IsOverwrite_є_в_журналі_й_дзеркалах_з_DEFAULT_0(string table)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(true);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CONCAT(TYPE_NAME(c.user_type_id), ' ', CASE c.is_nullable WHEN 1 THEN 'NULL' ELSE 'NOT NULL' END, ' ',
                          dc.definition)
            FROM sys.columns AS c
            LEFT JOIN sys.default_constraints AS dc ON dc.object_id = c.default_object_id
            WHERE c.object_id = OBJECT_ID(@table) AND c.name = N'IsOverwrite';
            """;
        command.Parameters.AddWithValue("@table", table);

        Assert.Equal("bit NOT NULL ((0))", await command.ExecuteScalarAsync().ConfigureAwait(true) as string);

        if (table == "arc.AuditCellChange")
        {
            Assert.Equal(
                await ColumnOrdinalAsync(connection, "aud.CellChange", "IsOverwrite"),
                await ColumnOrdinalAsync(connection, table, "IsOverwrite"));
        }
    }

    /// <summary>Порядковий номер колонки серед колонок таблиці (за <c>column_id</c>, без дір від видалених).</summary>
    private static async Task<long> ColumnOrdinalAsync(SqlConnection connection, string table, string column)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT x.n
            FROM (SELECT c.name, ROW_NUMBER() OVER (ORDER BY c.column_id) AS n
                  FROM sys.columns AS c WHERE c.object_id = OBJECT_ID(@table)) AS x
            WHERE x.name = @column;
            """;
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@column", column);

        return (long)(await command.ExecuteScalarAsync().ConfigureAwait(true))!;
    }

    private async Task<List<string>> ColumnsAsync(string table)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(true);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CONCAT(c.name, ' ', TYPE_NAME(c.user_type_id), '(', c.max_length, ',', c.precision, ',', c.scale, ') ',
                          CASE c.is_nullable WHEN 1 THEN 'NULL' ELSE 'NOT NULL' END)
            FROM sys.columns AS c
            WHERE c.object_id = OBJECT_ID(@table);
            """;
        command.Parameters.AddWithValue("@table", table);

        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(true);
        while (await reader.ReadAsync().ConfigureAwait(true))
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }
}
