// tests/Ecr.Infrastructure.Tests/Jobs/SourceEventSyncAnnualTests.Volume.cs
using System.Diagnostics;
using System.Globalization;
using Ecr.Application.Integration.SourceEvents;
using Ecr.Application.Ports;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Q-3 (людина 06.11: EF факела — «тисячі» на місяць): повна звірка AN-4 на 5 000 подіях одного місяця, на реальному SQL
/// Server і справжньому записі рядків — перший синк, звичайна чистка (300 зниклих + 100 нових) і повторний прогін.
/// </summary>
/// <remarks>
/// Межа часу — 120 с на прогін: стандартний таймаут команди SQL — 30 с на ОДНУ команду, а прогін — сотні команд;
/// межа ловить квадратичну деградацію, а не шум стенда. Виміряні тривалості — у повідомленні збою та в журналі тестів.
/// Мутація: повернути плоску абсолютну стелю 200 у <c>ExceedsRemovalLimit</c> — червоніє (300 зниклих блокуються, рядків 5 100).
/// </remarks>
public sealed partial class SourceEventSyncAnnualTests
{
    private const int MonthlyEvents = 5_000;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-3")]
    public async Task Q3_Тисячі_EF_за_місяць_синк_звірка_і_повтор_у_межах_часу_без_блоку_і_сиріт()
    {
        await using var stand = await ArrangeAsync();
        var monthStart = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var step = TimeSpan.FromTicks(TimeSpan.FromDays(30).Ticks / MonthlyEvents);
        var events = Enumerable.Range(0, MonthlyEvents)
            .Select(i => Ev($"V{i:00000}", monthStart + (step * i), 1m + (i % 7)))
            .ToList();
        var source = new FakeEventSource([.. events]);

        var first = await TimedRunAsync(stand, source);
        Assert.Equal(MonthlyEvents, await EfRowCountAsync(stand));

        // Звичайна чистка: 300 подій (кожна 16-а, 6 %) зникли, 100 нових — у тому ж місяці.
        var gone = events.Where((_, i) => i % 16 == 0).Take(300).ToList();
        var goneIds = gone.Select(e => e.EventId).ToHashSet(StringComparer.Ordinal);
        var added = Enumerable.Range(0, 100)
            .Select(i => Ev($"N{i:000}", monthStart.AddMinutes(3 + (i * 400)), 5m))
            .ToList();
        var goneRowIds = await RowIdsAsync(stand, gone.Select(e => "EF-" + e.EventId));
        var goneLinkIds = (await LinksAsync(stand)).Where(l => goneIds.Contains(l.SourceEventId)).Select(l => l.Id).ToList();
        Assert.Equal(300, goneRowIds.Count);
        Assert.Equal(300, goneLinkIds.Count);

        source.Result = new SourceEventResult([.. events.Where(e => !goneIds.Contains(e.EventId)), .. added], false, null);
        var progress = new CapturingProgress();
        var second = await TimedRunAsync(stand, source, progress);

        Assert.Null(SourceEventSyncOutcome.RemovalBlockOf("Succeeded", progress.Last));
        Assert.Equal(MonthlyEvents - 300 + 100, await EfRowCountAsync(stand));
        Assert.Equal(300, await RemovedCountAsync(stand));
        Assert.Equal(0, await CoverageCountAsync(stand, "eventRemovalLimit"));
        Assert.Equal(0, await CoverageCountAsync(stand, "eventsTruncatedCap"));
        Assert.Equal(0, await CoverageCountAsync(stand, "eventRemovalSourceEmpty"));

        // Нуль сиріт: комірки видалених рядків і значення атрибутів зниклих подій пішли разом із рядком і зв'язком.
        Assert.Equal(0, await CountInAsync("doc.CellValue", "TableRowId", goneRowIds, "PeriodKey = 202601"));
        Assert.Equal(0, await CountInAsync("ext.SourceEventAttributeValue", "LinkId", goneLinkIds, null));

        var third = await TimedRunAsync(stand, source);
        Assert.Equal(MonthlyEvents - 300 + 100, await EfRowCountAsync(stand));
        Assert.Equal(300, await RemovedCountAsync(stand));

        var measured = string.Create(
            CultureInfo.InvariantCulture,
            $"Q-3 {MonthlyEvents} EF/місяць: перший синк {first.TotalSeconds:F1} с, звірка (−300/+100) {second.TotalSeconds:F1} с, повтор {third.TotalSeconds:F1} с");
        Console.WriteLine(measured);
        Assert.True(
            first < RunBudget && second < RunBudget && third < RunBudget,
            measured + $"; межа {RunBudget.TotalSeconds} с на прогін");
    }

    /// <summary>Межа на прогін (див. remarks класу).</summary>
    private static readonly TimeSpan RunBudget = TimeSpan.FromSeconds(120);

    private async Task<TimeSpan> TimedRunAsync(Stand stand, FakeEventSource source, IJobProgress? progress = null)
    {
        var watch = Stopwatch.StartNew();
        await RunAsync(stand, source, progress: progress);
        var elapsed = watch.Elapsed;

        // ⚠ Велика заливка — з CHECKPOINT після кожного прогону (TestDatabaseSizeTests: журнал спільної бази колекції
        // інакше росте до стелі прогону).
        await ExecuteAsync("CHECKPOINT;");
        return elapsed;
    }

    private async Task<int> EfRowCountAsync(Stand stand)
        => Convert.ToInt32(
            await ScalarAsync(
                $"SELECT COUNT(*) FROM doc.TableRow WHERE TableInstanceId = {stand.InstanceId} AND IsDeleted = 0 AND RowKey LIKE N'EF-%'"),
            CultureInfo.InvariantCulture);

    private async Task<List<long>> RowIdsAsync(Stand stand, IEnumerable<string> rowKeys)
    {
        var keys = string.Join(",", rowKeys.Select(k => "N'" + k + "'"));
        var ids = new List<long>();
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT Id FROM doc.TableRow WHERE TableInstanceId = {stand.InstanceId} AND RowKey IN ({keys})";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    private async Task<int> CountInAsync(string table, string column, IReadOnlyCollection<long> ids, string? extra)
        => Convert.ToInt32(
            await ScalarAsync(
                $"SELECT COUNT(*) FROM {table} WHERE {column} IN ({string.Join(",", ids)})" + (extra is null ? string.Empty : " AND " + extra)),
            CultureInfo.InvariantCulture);
}
