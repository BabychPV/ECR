// src/Ecr.Infrastructure/Jobs/FlertSyncDependentsTrigger.cs
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// FLERT F6 (<c>D-476</c>, рішення Q6): синк FLERT змінив рядки дзеркала — ОДНА злита задача «перерахувати залежні
/// документи» на кожен змінений довідник-дзеркало × рік, у якому він має залежних.
/// </summary>
/// <remarks>
/// ⚠ Задача — та сама <see cref="IRegistryImpactRecalculationJob"/>, що й у HSE400 3b (<see cref="Hse400LandDependentsTrigger"/>):
/// той самий пошук залежних (<see cref="IRegistryImpactStore.ListImpactedForYearAsync"/>: <c>cfg.RegistryUse</c>, лише
/// <c>Open</c>/<c>Grace</c>) і та сама постановка (<see cref="ICalculationTrigger.RequestDependentsAsync"/>: ✎ B63 —
/// пропускається лише затверджений аркуш, поданий перераховується з позначкою). Тут — лише які роки: ті, де актуальний прогін методології, що читає довідник, лежить у
/// відкритому періоді. Закритий період не дає року взагалі.
/// <para>
/// ⛔ Лише <c>EnqueueCoalescedAsync</c> з ціллю <see cref="RegistryImpactRecalculationTarget.ForYear"/>: кілька синків
/// (розклад + «Синхронізувати зараз»), поки задача чекає, — одна задача; виконувана не переривається.
/// </para>
/// <para>
/// ⛔ Типово ВИМКНЕНО (<see cref="EnabledKey"/>): перерахунок усіх відкритих документів, що читають склад газу, — дія
/// з ціною; вмикає адміністратор, коли методології HSE30X опубліковано.
/// </para>
/// <para>
/// ⚠ Збій постановки не валить синк (копія вже оновлена): помилка — у журнал.
/// </para>
/// </remarks>
public sealed partial class FlertSyncDependentsTrigger(
    EcrDbContext db,
    IBackgroundJobScheduler jobs,
    bool enabled,
    ILogger<FlertSyncDependentsTrigger>? logger = null)
{
    /// <summary>Ключ налаштування: <c>true</c> — після синку зі змінами ставити перерахунок залежних документів.</summary>
    public const string EnabledKey = "Flert:Sync:RecalculateDependents";

    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>Чи ввімкнено (лише явне <c>true</c>).</summary>
    /// <param name="configuration">Конфігурація застосунку.</param>
    /// <returns><c>true</c> лише коли ключ явно <c>true</c>.</returns>
    public static bool IsEnabled(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return bool.TryParse(configuration[EnabledKey], out var value) && value;
    }

    /// <summary>Синк завершився; ставить перерахунок залежних від змінених довідників.</summary>
    /// <param name="outcome">Підсумок синку.</param>
    /// <param name="codes">Коди довідників дзеркала.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Скільки задач поставлено (довідник × рік).</returns>
    public async Task<int> OnSyncedAsync(FlertSyncOutcome outcome, FlertRegistryCodes codes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(codes);

        // Нічого не записано (вимкнено, відмова, збій) або записано без змін — перераховувати нема чого.
        if (!enabled || outcome.Counts is not { AnyChange: true } counts)
        {
            return 0;
        }

        // Лічильники синку ключовані назвою кроку (`FlertCompositionSyncJob.ApplyAsync`), а не кодом довідника.
        var changed = new[] { ("streams", codes.Stream), ("cases", codes.Case), ("compositions", codes.Composition) }
            .Where(step => Changed(counts, step.Item1))
            .Select(step => step.Item2)
            .ToList();
        if (changed.Count == 0)
        {
            return 0;
        }

        var queued = 0;
        try
        {
            var registries = await db.RegistryDefs.AsNoTracking()
                .Where(r => changed.Contains(r.Code))
                .Select(r => new { r.Id, r.Code })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var registry in registries.OrderBy(r => r.Id))
            {
                foreach (var year in await AffectedYearsAsync(registry.Id, ct).ConfigureAwait(false))
                {
                    await jobs
                        .EnqueueCoalescedAsync<IRegistryImpactRecalculationJob>(
                            RegistryImpactRecalculationTarget.ForYear(registry.Id, year),
                            new RegistryImpactRecalculationRequest(
                                registry.Id,
                                [],
                                string.Create(CultureInfo.InvariantCulture, $"FLERT sync changed {registry.Code}, {year}"),
                                year),
                            ct,
                            createdByUserId: null)
                        .ConfigureAwait(false);
                    queued++;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogEnqueueFailed(_logger, string.Join(", ", changed), ex);
        }

        return queued;
    }

    private static bool Changed(FlertSyncCounts counts, string step)
        => counts.Added.GetValueOrDefault(step) + counts.Updated.GetValueOrDefault(step)
           + counts.Deactivated.GetValueOrDefault(step) + counts.Reactivated.GetValueOrDefault(step) > 0;

    /// <summary>
    /// Роки, у яких довідник має залежних: актуальний прогін методології, що його читає (<c>cfg.RegistryUse</c>), у
    /// відкритому чи пільговому періоді — той самий вимір, що <see cref="RegistryImpactStore"/>.
    /// </summary>
    private async Task<SortedSet<int>> AffectedYearsAsync(int registryDefId, CancellationToken ct)
    {
        var periodKeys = await (
                from use in db.RegistryUses.AsNoTracking()
                where use.SourceKind == RegistryUse.MethodologyVersionSource && use.RegistryDefId == registryDefId
                join result in db.CalculationResults.AsNoTracking() on use.SourceId equals result.MethodologyVersionId
                join run in db.CalculationRuns.AsNoTracking() on result.CalculationRunId equals run.Id
                where run.Status == CalculationRun.CurrentStatus
                join document in db.Documents.AsNoTracking() on result.DocumentId equals document.Id
                join period in db.Periods.AsNoTracking()
                    on new { document.ProjectId, result.PeriodKey } equals new { period.ProjectId, PeriodKey = period.PeriodKeyValue }
                where period.State == PeriodState.Open || period.State == PeriodState.Grace
                select result.PeriodKey)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return [.. periodKeys.Select(key => key / 100)];
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "FLERT: не вдалось поставити перерахунок залежних документів після синку (змінені довідники: {Registries}).")]
    private static partial void LogEnqueueFailed(ILogger logger, string registries, Exception exception);
}
