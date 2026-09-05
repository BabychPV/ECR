// src/Ecr.Infrastructure/Jobs/MaterializeCollectedDataJob.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Переносить зібрані точки <c>ext.RawDataPoint</c> у комірки документів
/// (<c>D-118</c>).
/// </summary>
/// <remarks>
/// ⛔ Запис іде через <b>той самий</b> <c>PatchCellsHandler</c>, що й правка
/// людини, під технічним записом <c>svc-integration</c>. Окремий «швидкий»
/// шлях запису не заводиться навмисно: другий шлях запису в комірки і є те,
/// що дало `A7-27` — там мапа колонок будувалася інакше, ніж на основному
/// шляху, і адресація розійшлася.
///
/// ⚠ Задача окрема і ставиться ПІСЛЯ успішного збору інтервалу — не при
/// відкритті документа (бюджет 400 мс) і не при поданні (запізно).
///
/// ⚠ Правила з `D-118`, і кожне з них про те, щоб не втратити дані мовчки:
/// <list type="table">
/// <item><term>період <c>Open</c>/<c>Grace</c></term><description>записати</description></item>
/// <item><term>період закритий</term><description><b>не</b> писати; рядок у журналі покриття зі статусом <c>SkippedPeriodClosed</c></description></item>
/// <item><term>комірка правлена людиною</term><description><b>не</b> перезаписувати; <c>ConflictKeptManual</c></description></item>
/// <item><term>комірка порожня або від інтеграції</term><description>записати</description></item>
/// </list>
/// </remarks>
public sealed class MaterializeCollectedDataJob(
    EcrDbContext db,
    ICellPatcher patcher,
    ICoverageJournal coverage,
    IClock clock) : IMaterializeCollectedDataJob
{
    /// <summary>Код задачі в черзі.</summary>
    public static string Code => "materialize-collected";

    /// <summary>
    /// Стеля точок на прогін.
    /// </summary>
    /// <remarks>
    /// Півмільйона — це вже не «інтервал збору», а наслідок помилки
    /// конфігурації; переносити їх усі означало б покласти запис документів на
    /// час, коли з ними працюють.
    /// </remarks>
    public const int MaxPoints = 500_000;

    /// <inheritdoc />
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var task = MaterializePayload.Parse(payload);
        var periodKey = new PeriodKey(task.PeriodKey);

        await progress.ReportAsync(10, "Читання мапінгів", ct).ConfigureAwait(false);

        // ⚠ Беруться лише МАТЕРІАЛІЗОВАНІ мапінги: `TargetRowKey IS NULL`
        // означає «точки лишаються сирими для звірки», і це легальний стан.
        var maps = await db.EntityFieldMaps
            .AsNoTracking()
            .Where(m => m.SourceEntityId == task.SourceEntityId
                        && m.IsActive
                        && m.TargetRowKey != null
                        && m.TargetColumnDefId != null)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (maps.Count == 0)
        {
            await progress.ReportAsync(100, "Матеріалізованих мапінгів немає", ct).ConfigureAwait(false);
            return;
        }

        // ⛔ Стан періоду перевіряється ОДИН раз і до роботи. Пізній збір за
        // закритий період не втрачається тихо: він лишається сирим, а в
        // журналі покриття з'являється причина.
        var state = await db.Periods
            .AsNoTracking()
            .Where(p => p.ProjectId == task.ProjectId && p.PeriodKeyValue == task.PeriodKey)
            .Select(p => (PeriodState?)p.State)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (state is not (PeriodState.Open or PeriodState.Grace))
        {
            await coverage
                .RecordAsync(task.SourceEntityId, periodKey, "SkippedPeriodClosed",
                    $"Період у стані {state?.ToString() ?? "невідомо"}: пізній збір лишається сирим.", ct)
                .ConfigureAwait(false);

            await progress.ReportAsync(100, "Період закритий: перенесення пропущено", ct).ConfigureAwait(false);
            return;
        }

        await progress.ReportAsync(30, "Згортання точок", ct).ConfigureAwait(false);

        var aggregated = await AggregateAsync(task, maps, ct).ConfigureAwait(false);

        if (aggregated.Count == 0)
        {
            await progress.ReportAsync(100, "Точок за інтервал немає", ct).ConfigureAwait(false);
            return;
        }

        await progress.ReportAsync(60, "Запис у комірки", ct).ConfigureAwait(false);

        var written = await patcher
            .ApplyIntegrationAsync(task.DocumentId, task.TableInstanceId, periodKey, aggregated, ct)
            .ConfigureAwait(false);

        // ⛔ Конфлікт із правкою людини НЕ мовчазний: людина виправила навмисно,
        // і інтеграція не має права це стерти — але й приховати факт теж.
        foreach (var kept in written.KeptManual)
        {
            await coverage
                .RecordAsync(task.SourceEntityId, periodKey, "ConflictKeptManual",
                    $"Комірка {kept} має правку людини: значення збору не застосовано.", ct)
                .ConfigureAwait(false);
        }

        await progress
            .ReportAsync(100,
                $"Записано {written.Applied}; збережено ручних {written.KeptManual.Count}",
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>Згортає сирі точки в одне значення на мапінг.</summary>
    private async Task<IReadOnlyList<IntegrationCellValue>> AggregateAsync(
        MaterializeTask task, List<EntityFieldMap> maps, CancellationToken ct)
    {
        var fields = maps.Select(m => m.SourceField).ToList();

        var points = await db.RawDataPoints
            .AsNoTracking()
            .Where(p => p.SourceEntityId == task.SourceEntityId
                        && p.Timestamp >= task.FromUtc
                        && p.Timestamp < task.ToUtc
                        && fields.Contains(p.SourcePath))
            .OrderBy(p => p.Timestamp)
            .Take(MaxPoints)
            .Select(p => new { p.SourcePath, p.Timestamp, p.ValueNumeric })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var result = new List<IntegrationCellValue>(maps.Count);

        foreach (var map in maps)
        {
            var series = points
                .Where(p => string.Equals(p.SourcePath, map.SourceField, StringComparison.Ordinal)
                            && p.ValueNumeric is not null)
                .ToList();

            if (series.Count == 0)
            {
                continue;
            }

            // ⚠ `Aggregation` не може бути null: пара «рядок + агрегація»
            // нерозривна і на рівні домену, і обмеженням у базі.
            var value = map.Aggregation switch
            {
                AggregationKind.Sum => series.Sum(p => p.ValueNumeric!.Value),
                AggregationKind.Avg => series.Average(p => p.ValueNumeric!.Value),
                AggregationKind.Min => series.Min(p => p.ValueNumeric!.Value),
                AggregationKind.Max => series.Max(p => p.ValueNumeric!.Value),
                AggregationKind.Last => series[^1].ValueNumeric!.Value,
                AggregationKind.First => series[0].ValueNumeric!.Value,
                _ => throw new InvalidOperationException(
                    $"Мапінг {map.Id} не називає способу згортання: конфігурація неповна."),
            };

            result.Add(new IntegrationCellValue(map.TargetRowKey!, map.TargetColumnDefId!.Value, value));
        }

        return result;
    }
}

/// <summary>Розбір завдання матеріалізації.</summary>
internal static class MaterializePayload
{
    private static readonly System.Text.Json.JsonSerializerOptions Options =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>Розбирає завдання черги.</summary>
    public static MaterializeTask Parse(object? payload)
    {
        if (payload is MaterializeTask typed)
        {
            return typed;
        }

        var json = payload as string ?? System.Text.Json.JsonSerializer.Serialize(payload);

        return System.Text.Json.JsonSerializer.Deserialize<MaterializeTask>(json, Options)
               ?? throw new InvalidOperationException(
                   "Завдання матеріалізації не розбирається: невідома форма payload.");
    }
}

/// <summary>Завдання перенесення точок у комірки.</summary>
/// <param name="SourceEntityId">Сутність джерела, з якої зібрано.</param>
/// <param name="ProjectId">Проєкт.</param>
/// <param name="DocumentId">Документ.</param>
/// <param name="TableInstanceId">Екземпляр таблиці.</param>
/// <param name="PeriodKey">Період.</param>
/// <param name="FromUtc">Початок інтервалу збору.</param>
/// <param name="ToUtc">Кінець інтервалу, виключно.</param>
public sealed record MaterializeTask(
    int SourceEntityId,
    int ProjectId,
    long DocumentId,
    long TableInstanceId,
    int PeriodKey,
    DateTime FromUtc,
    DateTime ToUtc);
