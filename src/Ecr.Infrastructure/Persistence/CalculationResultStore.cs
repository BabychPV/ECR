using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="ICalculationResultStore"/> над <see cref="EcrDbContext"/>.</summary>
/// <remarks>
/// Пише **тільки** в <c>calc.CalculationResult</c> і <c>calc.CalculationStep</c>.
/// У <c>doc.CellValue</c> результати методологій не потрапляють ніколи (D-69).
/// </remarks>
public sealed class CalculationResultStore(EcrDbContext db, IClock clock) : ICalculationResultStore
{
    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Діапазон береться ОДНИМ викликом на весь пакет, а не по одному
    /// значенню: <c>Id</c> потрібні ДО вставки, щоб завантажити результати і
    /// трейс одним проходом. Звернення до послідовності на кожен рядок
    /// коштувало б мільйонів round-trip на річному перерахунку.
    /// </remarks>
    public async Task<long> ReserveResultIdRangeAsync(int count, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        // sp_sequence_get_range недоступний поза SQL Server (у тестах —
        // SQLite), тому провайдер визначає спосіб. Обидва дають безперервний
        // діапазон, і саме це важливо.
        if (!db.Database.IsSqlServer())
        {
            var last = await db.CalculationResults
                .AsNoTracking()
                .Select(r => (long?)r.Id)
                .MaxAsync(ct)
                .ConfigureAwait(false);

            return (last ?? 0) + 1;
        }

        var range = await db.Database
            .SqlQuery<long>($"""
                DECLARE @first sql_variant;
                EXEC sys.sp_sequence_get_range
                    @sequence_name = N'calc.CalculationResultSeq',
                    @range_size = {count},
                    @range_first_value = @first OUTPUT;
                SELECT CONVERT(bigint, @first) AS Value;
                """)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return range[0];
    }

    /// <inheritdoc />
    public async Task WriteResultsAsync(
        long calculationRunId, IReadOnlyList<CalculationOutput> outputs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(outputs);

        var values = outputs.SelectMany(o => o.Values.Select(v => (Output: o, Value: v))).ToList();
        if (values.Count == 0)
        {
            return;
        }

        var run = await db.CalculationRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == calculationRunId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Прогону {calculationRunId} не існує.");

        var nextId = await ReserveResultIdRangeAsync(values.Count, ct).ConfigureAwait(false);

        // ⚠ PeriodKey прогону, а не рядка: результат живе в партиції свого
        // періоду, і повний рік розкладається по дванадцятьох партиціях
        // окремими прогонами.
        var periodKey = run.PeriodKey ?? 0;

        foreach (var (output, value) in values)
        {
            var result = new CalculationResult(
                calculationRunId,
                value.MethodologyVersionId,
                periodKey,
                output.DocumentId,
                output.SourceRowKey,
                value.OutputCode,
                value.Value,
                value.UnitId);

            typeof(Domain.Abstractions.Entity<long>)
                .GetProperty(nameof(Domain.Abstractions.Entity<long>.Id))!
                .SetValue(result, nextId++);

            result.SetSubstance(value.SubstanceEntryId);
            db.CalculationResults.Add(result);
        }
    }

    /// <inheritdoc />
    public async Task WriteTraceAsync(
        long calculationRunId, IReadOnlyList<CalculationOutput> outputs,
        TraceLevel traceLevel, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(outputs);

        // ⛔ Off не пише нічого. Керуємо тим, ЩО пишемо, а не скільки
        // зберігаємо: політика — «нічого не затирається» (ЗБР-1, ЗБР-3).
        if (traceLevel == TraceLevel.Off)
        {
            return;
        }

        var steps = outputs.SelectMany(o => o.Trace).ToList();
        if (steps.Count == 0)
        {
            return;
        }

        var run = await db.CalculationRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == calculationRunId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Прогону {calculationRunId} не існує.");

        var periodKey = run.PeriodKey ?? 0;
        var nextId = await NextStepIdAsync(ct).ConfigureAwait(false);

        foreach (var step in steps)
        {
            var entity = new CalculationStep(calculationRunId, periodKey, step.StepOrder, step.StepCode);
            typeof(Domain.Abstractions.Entity<long>)
                .GetProperty(nameof(Domain.Abstractions.Entity<long>.Id))!
                .SetValue(entity, nextId++);

            entity.Describe(step.Expression, step.Value, step.TraceJson, resultId: null, step.Masked);
            db.CalculationSteps.Add(entity);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Зрізи <c>rpt.*</c> будуються Етапом 5; поки їх немає, інвалідувати
    /// нічого. Метод існує, щоб послідовність завершення прогону була повною
    /// вже зараз — інакше на Етапі 5 довелося б згадати про неї самому.
    /// </remarks>
    public Task InvalidateReportSnapshotsAsync(long calculationRunId, CancellationToken ct)
        => Task.CompletedTask;

    /// <inheritdoc />
    public async Task SwitchCurrentRunAsync(
        long calculationRunId, string modulesProfileJson, CancellationToken ct)
    {
        var run = await db.CalculationRuns
            .FirstOrDefaultAsync(r => r.Id == calculationRunId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Прогону {calculationRunId} не існує.");

        // ⚠ Обидві половини — в одному наборі змін, який коміт застосує разом
        // (ФВ-9.11). Між зняттям актуальності зі старого прогону і
        // встановленням новому існує стан, у якому актуальних прогонів нуль
        // або два; звіт, побудований у цю мить, не має правильної відповіді.
        var previous = await db.CalculationRuns
            .Where(r => r.ProjectId == run.ProjectId
                        && r.PeriodKey == run.PeriodKey
                        && r.Id != calculationRunId
                        && r.Status == CalculationRun.CurrentStatus)
            .Take(MaxSupersededRuns)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var stale in previous)
        {
            stale.Supersede();
        }

        run.Complete(CalculationRun.CurrentStatus, clock.UtcNow, modulesProfileJson, errorMessage: null);
        run.MakeCurrent();
    }

    /// <summary>
    /// Стеля на кількість прогонів, з яких знімається актуальність.
    /// </summary>
    /// <remarks>
    /// Актуальний прогін мусить бути рівно один; більший список означає
    /// зіпсовані дані. Межа тут не оптимізація, а те, що не дає такій
    /// зіпсованості перетворитися на довгу транзакцію.
    /// </remarks>
    private const int MaxSupersededRuns = 100;

    /// <summary>Стеля вибірки результатів на один документ і період.</summary>
    /// <remarks>
    /// Рядків стільки, скільки виходів × речовин × рядків таблиці; десятки
    /// тисяч — уже ознака того, що прив'язку поставили на не ту таблицю.
    /// </remarks>
    private const int MaxResults = 50_000;

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ Прогін добирається за <c>Status = Current</c>, а не за максимальним
    /// <c>Id</c>. Прогін, який упав, лишає по собі частину рядків, і «останній
    /// за часом» показав би суміш: половина чисел від нової версії методології,
    /// половина від старої, і жодної ознаки на екрані.
    ///
    /// ⚠ Актуальність питається ПІДЗАПИТОМ (<c>EXISTS</c>), а не з'єднанням:
    /// прогонів на період може бути кілька (кожен документ перераховується
    /// окремо), тож «актуальний прогін періоду» — не одне число, а з'єднання з
    /// проєкцією в тип, на полях якого потім сортують, EF перекласти не може
    /// взагалі.
    /// </remarks>
    public async Task<IReadOnlyList<CalculationResultRow>> ReadCurrentAsync(
        long documentId, int periodKey, CancellationToken ct)
    {
        var rows = await db.CalculationResults
            .AsNoTracking()
            .Where(r => r.DocumentId == documentId
                        && r.PeriodKey == periodKey
                        && db.CalculationRuns.Any(
                            run => run.Id == r.CalculationRunId
                                   && run.Status == Domain.Entities.Calculations.CalculationRun.CurrentStatus))
            .OrderBy(r => r.SourceRowKey)
            .ThenBy(r => r.OutputCode)
            .Take(MaxResults)
            .Select(r => new
            {
                r.MethodologyVersionId,
                r.SourceRowKey,
                r.OutputCode,
                r.Value,
                r.UnitId,
                r.SubstanceEntryId,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.ConvertAll(r => new CalculationResultRow(
            r.MethodologyVersionId, r.SourceRowKey, r.OutputCode, r.Value, r.UnitId, r.SubstanceEntryId));
    }

    /// <summary>Наступний ідентифікатор кроку трейсу.</summary>
    private async Task<long> NextStepIdAsync(CancellationToken ct)
    {
        var last = await db.CalculationSteps
            .AsNoTracking()
            .Select(s => (long?)s.Id)
            .MaxAsync(ct)
            .ConfigureAwait(false);

        return (last ?? 0) + 1;
    }
}
