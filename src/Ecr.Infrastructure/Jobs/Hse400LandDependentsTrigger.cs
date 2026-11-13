// src/Ecr.Infrastructure/Jobs/Hse400LandDependentsTrigger.cs
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecr.Infrastructure.Jobs;

/// <summary>Що зробити після УСПІШНОГО прогону <see cref="RecalculationJob"/>.</summary>
public interface IRecalculationCompletionHook
{
    /// <summary>Прогін завершився успішно; результати вже актуальні.</summary>
    /// <param name="periodsByDocument">Документи прогону і періоди, які він рахував.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task OnSucceededAsync(IReadOnlyDictionary<long, int[]> periodsByDocument, CancellationToken ct);
}

/// <summary>
/// HSE400 3b: після перерахунку документа HSE400 — ОДНА злита задача «перерахувати документи, методології яких
/// читають <c>LAND_HSE400_COMPOSITION</c>» за рік документа.
/// </summary>
/// <remarks>
/// ⚠ Задача — та сама <see cref="IRegistryImpactRecalculationJob"/>, що й у <c>recalculate-impacted</c>: той самий
/// пошук залежних (<see cref="IRegistryImpactStore"/>: <c>cfg.RegistryUse</c>, лише <c>Open</c>/<c>Grace</c>) і та сама
/// постановка (<see cref="ICalculationTrigger.RequestDependentsAsync"/>: закритий період не ставиться; ✎ B63 — пропускаються
/// лише затверджені аркуші, подані перераховуються з позначкою).
/// Відрізняється лише «застарілий»: кожен актуальний прогін року на момент виконання, а не прогін до <c>DataChangedAt</c>.
/// <para>
/// ⛔ Лише <c>EnqueueCoalescedAsync</c> з ціллю «довідник × рік» (<see cref="RegistryImpactRecalculationTarget.ForYear"/>):
/// повторні перерахунки HSE400, поки задача чекає, зливаються в одну; виконувана не переривається.
/// </para>
/// <para>
/// ⚠ Зміну чисел <c>F_</c> не порівнюємо (дешевого виміру немає): ставиться після кожного успішного прогону
/// документа HSE400; злиття тримає одну задачу на рік.
/// </para>
/// <para>
/// ⚠ Збій постановки не валить перерахунок HSE400 (його результати вже актуальні): помилка — у журнал.
/// </para>
/// </remarks>
public sealed partial class Hse400LandDependentsTrigger(
    EcrDbContext db,
    IBackgroundJobScheduler jobs,
    bool enabled,
    ILogger<Hse400LandDependentsTrigger>? logger = null,
    string registryCode = Hse400CompositionMapping.RegistryCode,
    string templateCode = Hse400CompositionMapping.TemplateCode) : IRecalculationCompletionHook
{
    /// <summary>Ключ налаштування: <c>true</c> — після перерахунку HSE400 ставити перерахунок залежних Land.</summary>
    public const string EnabledKey = "Calculations:Hse400RecalculateLandDependents";

    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>Увімкнено лише разом із читанням складу з документа (<see cref="Hse400CompositionSnapshotLoader"/>).</summary>
    /// <param name="configuration">Конфігурація застосунку.</param>
    /// <returns><c>true</c> лише коли обидва ключі явно <c>true</c>.</returns>
    public static bool IsEnabled(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return Hse400CompositionSnapshotLoader.IsEnabled(configuration)
               && bool.TryParse(configuration[EnabledKey], out var value) && value;
    }

    /// <inheritdoc />
    public async Task OnSucceededAsync(IReadOnlyDictionary<long, int[]> periodsByDocument, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(periodsByDocument);
        if (!enabled || periodsByDocument.Count == 0)
        {
            return;
        }

        try
        {
            var years = await Hse400YearsAsync(periodsByDocument, ct).ConfigureAwait(false);
            if (years.Count == 0)
            {
                return;
            }

            var registryDefId = await db.RegistryDefs.AsNoTracking()
                .Where(r => r.Code == registryCode)
                .Select(r => r.Id)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (registryDefId == 0)
            {
                return;
            }

            foreach (var year in years)
            {
                await jobs
                    .EnqueueCoalescedAsync<IRegistryImpactRecalculationJob>(
                        RegistryImpactRecalculationTarget.ForYear(registryDefId, year),
                        new RegistryImpactRecalculationRequest(
                            registryDefId,
                            [],
                            string.Create(CultureInfo.InvariantCulture, $"HSE400 {year} recalculated"),
                            year),
                        ct,
                        createdByUserId: null)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogEnqueueFailed(_logger, string.Join(", ", periodsByDocument.Keys), ex);
        }
    }

    /// <summary>Роки періодів, у яких перераховано документ шаблону HSE400 (той самий пошук, що в 3a).</summary>
    private async Task<SortedSet<int>> Hse400YearsAsync(
        IReadOnlyDictionary<long, int[]> periodsByDocument, CancellationToken ct)
    {
        var documentIds = periodsByDocument.Keys.ToList();
        var periodKeys = periodsByDocument.Values.SelectMany(p => p).Distinct().ToList();

        var found = await (
                from t in db.Templates.AsNoTracking()
                where t.Code == templateCode
                join v in db.TemplateVersions.AsNoTracking() on t.Id equals v.TemplateId
                join s in db.SheetDefs.AsNoTracking() on v.Id equals s.TemplateVersionId
                join d in db.TableDefs.AsNoTracking() on s.Id equals d.SheetDefId
                join i in db.TableInstances.AsNoTracking() on d.Id equals i.TableDefId
                where documentIds.Contains(i.DocumentId) && periodKeys.Contains(i.PeriodKeyValue) && i.SheetCopyId == null
                select new { i.DocumentId, i.PeriodKeyValue })
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Лише періоди, які прогін справді рахував для ЦЬОГО документа.
        return [.. found
            .Where(f => periodsByDocument.TryGetValue(f.DocumentId, out var periods) && periods.Contains(f.PeriodKeyValue))
            .Select(f => f.PeriodKeyValue / 100)];
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "HSE400: не вдалось поставити перерахунок залежних документів Land після перерахунку документів {DocumentIds}.")]
    private static partial void LogEnqueueFailed(ILogger logger, string documentIds, Exception exception);
}
