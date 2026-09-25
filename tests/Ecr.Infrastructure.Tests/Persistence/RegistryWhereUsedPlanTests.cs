using System.Xml.Linq;
using Ecr.Application.Common;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// <c>R-05</c>: «Where used» довідника читає комірки ІНДЕКСОМ посилань, а не
/// сканом усієї <c>doc.CellValue</c>.
/// </summary>
/// <remarks>
/// ⛔ На стенді (2.06 млн комірок) запит «чи є комірки з посиланням на цей
/// довідник» ішов 7.4 с — скан усіх комірок і 3 млн пошуків у
/// <c>dic.RegistryEntry</c>, — і саме він робив екран «Where used» шестисекундним
/// навіть для довідника без жодного посилання.
/// <para>
/// ⚠ Чому план, а не секунди чи логічні читання. Тестова база мала, і скан
/// кількох сторінок тут дешевий так само, як seek: поріг на читаннях чи часі
/// або хибно зеленів би, або залежав би від того, скільки комірок лишили
/// сусідні тести. План же не залежить від обсягу: або в ньому є доступ до
/// <c>doc.CellValue</c> іншим індексом (скан кластерного чи
/// <c>IX_CellValue_Fill</c>), або немає. План береться з кешу за міткою
/// <see cref="RegistryStore.WhereUsedCellsTag"/> — тобто той самий, яким
/// виконався бойовий запит, а не оцінка копії.
/// </para>
/// <para>
/// ⛔ Мутації, що валять тест (перевірено перезбіркою): прибрати
/// <c>IX_CellValue_RegistryEntry</c> з міграції <c>B18HotPathIndexes</c> — у
/// плані з'являється <c>PK_CellValue</c>; повернути стару форму запиту — теж
/// червоний, бо тег зникає разом із нею (запит не знайдено в кеші).
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryWhereUsedPlanTests(SqlServerFixture sql)
{
    private const string ShowPlanNs = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "R-05")]
    public async Task Комірки_довідника_без_посилань_читаються_індексом_а_не_сканом()
    {
        var fixture = await SeedAsync(referenced: false).ConfigureAwait(true);

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var usage = await new RegistryStore(db)
            .FindDefinitionUsageAsync(fixture.RegistryDefId, 50, CancellationToken.None)
            .ConfigureAwait(true);

        // Жодна комірка на записи довідника не посилається, хоч комірки в
        // базі є (фікстура щойно їх завела).
        Assert.DoesNotContain(usage.Items, i => i.Kind == UsageKinds.Data);

        var indexes = await CellValueIndexesInCachedPlanAsync().ConfigureAwait(true);

        Assert.True(
            indexes.Count > 0,
            "У кеші немає плану запиту з міткою R-05, або в ньому немає doc.CellValue.");
        Assert.Equal(["[IX_CellValue_RegistryEntry]"], indexes);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "R-05")]
    public async Task Комірка_з_посиланням_на_запис_довідника_дає_рядок_даних()
    {
        // ⚠ Нова форма запиту (від записів до комірок) мусить бачити те саме,
        // що стара: тест, який перевіряв би лише план, пропустив би запит, що
        // завжди відповідає «ні».
        var fixture = await SeedAsync(referenced: true).ConfigureAwait(true);

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var usage = await new RegistryStore(db)
            .FindDefinitionUsageAsync(fixture.RegistryDefId, 50, CancellationToken.None)
            .ConfigureAwait(true);

        var data = Assert.Single(usage.Items, i => i.Kind == UsageKinds.Data);
        Assert.Equal("cells", data.Id);
        Assert.Equal(1, usage.Total);
    }

    /// <summary>Довідник із двома записами і документ із комірками; на другий запис посилається одна комірка, якщо <paramref name="referenced"/>.</summary>
    private async Task<(int RegistryDefId, long SecondEntryId)> SeedAsync(bool referenced)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(columnCount: 2, rowCount: 3).ConfigureAwait(false);
        var tag = $"{Guid.NewGuid():N}"[..8].ToUpperInvariant();

        await using var db = builder.CreateContext();

        var registry = new RegistryDef(EcrCode.Create($"R05{tag}"), Name($"R-05 {tag}"), isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var first = new RegistryEntry(registry.Id, EcrCode.Create($"A{tag}"), Name("A"));
        var second = new RegistryEntry(registry.Id, EcrCode.Create($"B{tag}"), Name("B"));
        db.RegistryEntries.AddRange(first, second);
        await db.SaveChangesAsync().ConfigureAwait(false);

        // Звичайні комірки без посилань: таблиця комірок не порожня, тож скан
        // мав би що читати.
        foreach (var rowId in document.RowIds)
        {
            db.CellValues.Add(new CellValue(
                new CellAddress(document.PeriodKey, rowId, document.ColumnDefIds[0]),
                document.TableDefId,
                new CellValueData { ValueString = $"R-05 {rowId}" }));
        }

        if (referenced)
        {
            db.CellValues.Add(new CellValue(
                new CellAddress(document.PeriodKey, document.RowIds[0], document.ColumnDefIds[1]),
                document.TableDefId,
                new CellValueData { ValueRegistryEntryId = second.Id }));
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        return (registry.Id, second.Id);
    }

    /// <summary>Імена індексів, якими план запиту з міткою R-05 читає <c>doc.CellValue</c>.</summary>
    private async Task<IReadOnlyList<string>> CellValueIndexesInCachedPlanAsync()
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();

        // ⚠ dbid — з атрибутів плану: для параметризованих запитів
        // dm_exec_sql_text.dbid порожній, а мітка однакова в базах усіх
        // worktree на тому самому сервері.
        command.CommandText = """
            SELECT CAST(qp.query_plan AS nvarchar(max))
            FROM sys.dm_exec_query_stats AS qs
            CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) AS st
            CROSS APPLY sys.dm_exec_query_plan(qs.plan_handle) AS qp
            CROSS APPLY sys.dm_exec_plan_attributes(qs.plan_handle) AS pa
            WHERE pa.attribute = N'dbid' AND CAST(pa.value AS int) = DB_ID()
              AND st.text LIKE N'%' + @tag + N'%'
              AND st.text NOT LIKE N'%dm_exec_query_stats%';
            """;
        command.Parameters.AddWithValue("@tag", RegistryStore.WhereUsedCellsTag);

        var indexes = new SortedSet<string>(StringComparer.Ordinal);

        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (reader.IsDBNull(0))
            {
                continue;
            }

            var plan = XDocument.Parse(reader.GetString(0));
            foreach (var target in plan.Descendants(XName.Get("Object", ShowPlanNs)))
            {
                if ((string?)target.Attribute("Table") == "[CellValue]")
                {
                    indexes.Add((string?)target.Attribute("Index") ?? "(heap)");
                }
            }
        }

        return [.. indexes];
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
