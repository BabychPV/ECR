using System.Xml.Linq;
using Ecr.Application.Common;
using Ecr.Application.Ports;
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
/// сусідні тести. План береться з кешу за міткою
/// <see cref="RegistryStore.WhereUsedCellsTag"/> — тобто той самий, яким
/// виконався бойовий запит, а не оцінка копії.
/// </para>
/// <para>
/// ⛔ Але й план ЗАЛЕЖИТЬ від обсягу — тут стояло протилежне, і тест через це
/// плавав: 2026-09-28 двічі локально й раз у CI (run 36394757706) він дав
/// <c>["[PK_CellValue]"]</c> на першому повному прогоні й зелень на повторі.
/// У спільній тестовій базі після сусідніх тестів лишаються десятки комірок,
/// розкиданих по 25 розділах, і скан кластерного індексу коштує стільки ж,
/// скільки скан фільтрованого: заміряно на такій базі
/// <c>0.0976953</c> (примусово <c>PK_CellValue</c>) проти <c>0.0976853</c>
/// (<c>IX_CellValue_RegistryEntry</c>) — різниця 0.01 %. Хто з двох виграє,
/// вирішували застаріла статистика й кількість сторінок після чужих вставок і
/// видалень, а не індекс. Тому тест сам задає форму стенду — багато комірок
/// без посилань (<see cref="BackgroundCells"/>), свіжа статистика і план,
/// скомпільований саме зараз, а не успадкований від сусіднього тесту, — і
/// тоді скан кластерного програє з запасом, як і на 2.06 млн комірок.
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

    /// <summary>Скільки комірок без посилань тест кладе перед виміром плану.</summary>
    /// <remarks>
    /// 500 рядків × 40 колонок = 20 000 комірок, ~75 сторінок кластерного
    /// індексу, а у фільтрованому від них — нуль. Заміряно: план із примусовим
    /// <c>PK_CellValue</c> дорожчий за природний на ~80 % (0.163 проти 0.089),
    /// а не на 0.01 %, як без них (див. зауваження до класу); шум від сусідніх
    /// тестів — кілька сторінок, тобто тисячні частки. Менший обсяг (5 000)
    /// давав лише ~16 % запасу. На спільну базу — ~1 МБ.
    /// </remarks>
    private const int BackgroundRows = 500;

    private const int BackgroundColumns = 40;

    private const int BackgroundCells = BackgroundRows * BackgroundColumns;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "R-05")]
    public async Task Комірки_довідника_без_посилань_читаються_індексом_а_не_сканом()
    {
        var fixture = await SeedAsync(referenced: false).ConfigureAwait(true);
        await SeedBackgroundCellsAsync().ConfigureAwait(true);

        // ⚠ Свіжа статистика і порожній кеш плану цього запиту — щоб план
        // компілювався для щойно заданої форми даних, а не залежав від того,
        // що лишили сусідні тести і який план скомпілював попередній тест.
        await PrepareMeasurementAsync().ConfigureAwait(true);

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

    /// <summary>
    /// Форма стенду: окремий документ із <see cref="BackgroundCells"/> введеними
    /// комірками без посилань на довідник — масовим шляхом, як і в бойовому
    /// завантаженні.
    /// </summary>
    private async Task SeedBackgroundCellsAsync()
    {
        var document = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(columnCount: BackgroundColumns, rowCount: BackgroundRows)
            .ConfigureAwait(false);

        var cells = new List<CellRecord>(BackgroundCells);
        foreach (var rowId in document.RowIds)
        {
            foreach (var columnId in document.ColumnDefIds)
            {
                cells.Add(new CellRecord(
                    new CellAddress(document.PeriodKey, rowId, columnId),
                    document.TableDefId,
                    new CellValueData { ValueNumeric = cells.Count }));
            }
        }

        await new BulkCellLoader(sql.ConnectionString, BackgroundCells)
            .LoadAsync(cells, CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Оновлює статистику таблиць запиту і прибирає з кешу ЦІЄЇ бази його
    /// план, щоб бойовий запит скомпілювався заново.
    /// </summary>
    /// <remarks>
    /// ⛔ Саме <c>DATABASE SCOPED</c>, а не <c>DBCC FREEPROCCACHE</c>: сервер
    /// спільний з іншими прогонами й сесіями (так само чистять кеш
    /// <c>WritePathPlanCacheTests</c> і <c>TvpBatchWriteTests</c> у цій
    /// колекції, тобто послідовно з цим тестом).
    /// </remarks>
    private async Task PrepareMeasurementAsync()
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE STATISTICS doc.CellValue;
            UPDATE STATISTICS dic.RegistryEntry;
            ALTER DATABASE SCOPED CONFIGURATION CLEAR PROCEDURE_CACHE;
            """;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
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
