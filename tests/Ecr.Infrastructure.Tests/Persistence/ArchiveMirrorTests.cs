// tests/Ecr.Infrastructure.Tests/Persistence/ArchiveMirrorTests.cs
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Кожна колонка <c>calc.CalculationResult</c> і <c>calc.CalculationStep</c> має
/// дзеркало в <c>arc.*</c> того самого типу й NULL-придатності
/// (<c>12-archive-tables.sql</c>, FEATURE-HSE301-VIEW §7.1, архів <c>D-148</c>).
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
/// ⚠ Лише `calc`-пари. `doc.TableRow.IsOrphaned` у `arc.TableRow` свідомо немає
/// (розархівація ставить 0, `03-archive-proc.sql`), тож загальне правило «архів ⊇
/// джерело» для `doc.*` не діє.
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
    public async Task Кожна_колонка_джерела_має_дзеркало_в_архіві(string source, string archive)
    {
        var sourceColumns = await ColumnsAsync(source);
        var archiveColumns = await ColumnsAsync(archive);

        // Порожній перелік джерела означав би, що звіряти нема з чим, — і сторож
        // був би зелений на будь-якій базі.
        Assert.NotEmpty(sourceColumns);

        Assert.Empty(sourceColumns.Except(archiveColumns, StringComparer.Ordinal).Order(StringComparer.Ordinal));
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
