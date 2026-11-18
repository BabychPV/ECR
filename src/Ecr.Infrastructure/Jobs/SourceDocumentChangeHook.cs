// src/Ecr.Infrastructure/Jobs/SourceDocumentChangeHook.cs
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecr.Infrastructure.Jobs;

/// <summary>Що зробити після УСПІШНОГО прогону <see cref="RecalculationJob"/> чи <see cref="FormulaRecalculationJob"/>.</summary>
public interface IRecalculationCompletionHook
{
    /// <summary>Прогін завершився успішно; його записи вже закомічено.</summary>
    /// <param name="periodsByDocument">Документи прогону і періоди, які він рахував (чи в які записала правка).</param>
    /// <param name="sourceHop">
    /// B179 L2: ланка ланцюжка джерел, з якою поставлено прогін (0 — правка людини чи власний перерахунок документа).
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    public Task OnSucceededAsync(IReadOnlyDictionary<long, int[]> periodsByDocument, int sourceHop, CancellationToken ct);

    /// <summary>
    /// B179 L2b: успішний прохід <see cref="FormulaRecalculationJob"/> з НАСІННЯМ — правка комірок (PATCH, імпорт,
    /// видалення рядка, пермітів…). На відміну від <see cref="OnSucceededAsync"/>, результати методологій документа ще
    /// старі: документ-джерело спершу має перерахувати ВЛАСНІ методології.
    /// </summary>
    /// <param name="periodsByDocument">Документ правки і періоди, в які вона записала.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task OnCellsEditedAsync(IReadOnlyDictionary<long, int[]> periodsByDocument, CancellationToken ct);
}

/// <summary>
/// B179 L2 (perf): документи й періоди прогону РАЗОМ із версіями шаблонів їхніх екземплярів, уже прочитаними прогоном
/// (<see cref="FormulaRecalculationJob"/>): хук відсіює не-джерело без звернення до бази.
/// </summary>
/// <remarks>Без версій (звичайний словник, <see cref="RecalculationJob"/>) хук читає шаблони документа сам, як доти.</remarks>
public sealed class ChangedPeriodsDictionary : Dictionary<long, int[]>
{
    /// <summary>Версії шаблонів екземплярів документів у змінених періодах; <c>null</c> — невідомо.</summary>
    public IReadOnlySet<int>? TemplateVersionIds { get; init; }
}

/// <summary>
/// B179 L2 (рішення людини P-0118-33, Q1/Q2): після успішного перерахунку документа, шаблон якого — ДЖЕРЕЛО зв'язки
/// <c>cfg.DocumentSourceBinding</c> у стані <see cref="DocumentSourceState.Any"/> з бітом <c>RecalculateDependents</c>,
/// — одна злита задача <see cref="IRegistryImpactRecalculationJob"/> на (зв'язку, рік, місяць); споживачів вона бере з
/// графа залежностей у момент виконання.
/// </summary>
/// <remarks>
/// <para>
/// Замінює <c>Hse400LandDependentsTrigger</c> (HSE400 3b): той був лише для коду HSE400, лише після повного
/// перерахунку і за ключем <c>Calculations:Hse400RecalculateLandDependents</c>, який постачався <c>false</c> і вимикав
/// біт зв'язки (корінь дефекту live, B179 §1). Тепер — будь-яка зв'язка, обидві задачі перерахунку (правка комірки дає
/// лише <see cref="FormulaRecalculationJob"/>), без ключа; аварійний вимикач — лише біт зв'язки.
/// </para>
/// <para>
/// ⛔ Злиття: ціль <see cref="RegistryImpactRecalculationTarget.ForSourceChange"/> — сплеск правок одного місяця, поки
/// задача чекає, дає ОДНУ задачу; виконувана не переривається (нова — позаду неї).
/// </para>
/// <para>
/// ⚠ За <see cref="DocumentSourceState.ApprovedOnly"/> мовчить: споживачі читають лише затверджені місяці, а
/// затверджений аркуш перерахунком не змінюється — тригер там Approve/Reopen/видалення (<see cref="SourceApprovalChangeTrigger"/>).
/// Читання з документа вимкнене (біт зв'язки <c>ReadFromDocument</c>; ключ конфігурації виведено, P-0118-31/33) —
/// споживачі джерела не бачать, ставити нічого.
/// </para>
/// <para>
/// ⚠ Збій постановки не валить перерахунок джерела (його записи вже закомічено): помилка — у журнал.
/// </para>
/// <para>
/// ✎ B179 L2b (рішення людини Q1: «тригер на кожну правку джерела»): правка комірки дає лише задачу формул, а споживачі
/// (Land) читають і РЕЗУЛЬТАТИ МЕТОДОЛОГІЙ джерела (рядки складу <c>F_&lt;X&gt;</c>, <c>Hse400CompositionSnapshotLoader</c>).
/// Тому для правки (<see cref="OnCellsEditedAsync"/>) за тими самими воротами хук ставить ВЛАСНИЙ перерахунок
/// документа-джерела за період (<see cref="ICalculationTrigger.RequestAsync"/>: злита ціль <c>doc{id}-p{період}-auto</c>
/// — 50 правок місяця дають одну задачу; затверджений/закритий період — відмова), а не батьківську задачу споживачів:
/// споживачі зі старими <c>F_</c> рахувалися б марно. Успіх того перерахунку кличе <see cref="OnSucceededAsync"/> —
/// звідти вже батьківська задача. Петлі немає: <see cref="OnSucceededAsync"/> (шлях <see cref="RecalculationJob"/>)
/// власного перерахунку джерела не ставить ніколи. Період, де тригер відмовив, — батьківська задача одразу (як у L2).
/// </para>
/// </remarks>
public sealed partial class SourceDocumentChangeHook(
    EcrDbContext db,
    IBackgroundJobScheduler jobs,
    // B179 L2b: у DI завжди є; без нього (ручна збірка в тестах L2) правка поводиться як у L2 — одразу батьківська задача.
    ICalculationTrigger? sourceRecalculation = null,
    IDocumentSourceFlagOverrides? overrides = null,
    ILogger<SourceDocumentChangeHook>? logger = null,
    // B179 L2 (perf): у DI завжди є (singleton); без нього (ручна збірка в тестах) ворота читаються з бази щоразу.
    SourceBindingGateCache? gateCache = null) : IRecalculationCompletionHook
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <inheritdoc />
    public Task OnSucceededAsync(IReadOnlyDictionary<long, int[]> periodsByDocument, int sourceHop, CancellationToken ct)
        => HandleAsync(periodsByDocument, sourceHop, cellsEdited: false, ct);

    /// <inheritdoc />
    public Task OnCellsEditedAsync(IReadOnlyDictionary<long, int[]> periodsByDocument, CancellationToken ct)
        => HandleAsync(periodsByDocument, sourceHop: 0, cellsEdited: true, ct);

    private async Task HandleAsync(
        IReadOnlyDictionary<long, int[]> periodsByDocument, int sourceHop, bool cellsEdited, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(periodsByDocument);
        if (periodsByDocument.Count == 0)
        {
            return;
        }

        try
        {
            // ⛔ perf («perf max», FormulaJobFixedCostTests): ворота — з процесного кешу (0 звернень); задача формул
            // НЕ-джерела далі не звертається до бази взагалі, джерела — одне звернення (екземпляри оригіналу нижче).
            var versions = (periodsByDocument as ChangedPeriodsDictionary)?.TemplateVersionIds;
            var gate = gateCache is null
                ? await SourceBindingGate.LoadAsync(db, ct).ConfigureAwait(false)
                : await gateCache.GetAsync(db, ct).ConfigureAwait(false);
            if (gateCache is not null && gate.Bindings.Count > 0 && versions is not null
                && versions.Any(v => v > gate.MaxVersionId))
            {
                // Нова версія шаблону після знімка (могла бути версією джерела) — перечитати один раз.
                gate = await gateCache.ReloadAsync(db, ct).ConfigureAwait(false);
            }

            var bindings = gate.Bindings.Where(b => overrides?.ReadFromDocument ?? b.ReadFromDocument).ToList();
            if (bindings.Count == 0)
            {
                return;
            }

            var templates = bindings.Select(b => b.SourceTemplateId).Distinct().ToList();

            // Версії шаблонів, уже прочитані прогоном: жодна не належить шаблону-джерелу — документ не джерело.
            if (versions is not null
                && !versions.Any(v => gate.SourceTemplateOfVersion.TryGetValue(v, out var t) && templates.Contains(t)))
            {
                return;
            }

            if (sourceHop >= RegistryImpactRecalculationRequest.MaxHops)
            {
                LogHopCap(_logger, string.Join(", ", periodsByDocument.Keys), sourceHop);
                return;
            }

            var documentIds = periodsByDocument.Keys.ToList();
            var periodKeys = periodsByDocument.Values.SelectMany(p => p).Distinct().ToList();

            // Документ × період × шаблон: екземпляри оригіналу аркуша (як у завантажувачі складу).
            var found = await (
                    from i in db.TableInstances.AsNoTracking()
                    where documentIds.Contains(i.DocumentId) && periodKeys.Contains(i.PeriodKeyValue) && i.SheetCopyId == null
                    join t in db.TableDefs.AsNoTracking() on i.TableDefId equals t.Id
                    join s in db.SheetDefs.AsNoTracking() on t.SheetDefId equals s.Id
                    join v in db.TemplateVersions.AsNoTracking() on s.TemplateVersionId equals v.Id
                    where templates.Contains(v.TemplateId)
                    join d in db.Documents.AsNoTracking() on i.DocumentId equals d.Id
                    join p in db.Projects.AsNoTracking() on d.ProjectId equals p.Id
                    select new { i.DocumentId, i.PeriodKeyValue, v.TemplateId, p.PeriodKind })
                .Distinct()
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var matched = found.Where(f => periodsByDocument[f.DocumentId].Contains(f.PeriodKeyValue)).ToList();

            // B179 L2b: правка джерела — спершу ВЛАСНИЙ перерахунок документа за період (злита ціль), споживачі — після
            // його успіху. Лишаються лише періоди, де тригер відмовив (затверджено все, закритий, архів): там одразу як у L2.
            if (cellsEdited && sourceRecalculation is not null)
            {
                var declined = new HashSet<(long DocumentId, int PeriodKey)>();
                foreach (var (documentId, periodKey) in matched.Select(f => (f.DocumentId, f.PeriodKeyValue)).Distinct())
                {
                    if (await sourceRecalculation.RequestAsync(documentId, new PeriodKey(periodKey), ct).ConfigureAwait(false)
                        is null)
                    {
                        declined.Add((documentId, periodKey));
                    }
                }

                matched = [.. matched.Where(f => declined.Contains((f.DocumentId, f.PeriodKeyValue)))];
            }

            var targets = new SortedSet<(int BindingId, int RegistryDefId, int Year, int Month)>();
            foreach (var f in matched)
            {
                // Місяць — лише місячного проєкту (0 — увесь рік): період іншої довжини володіє кількома місяцями.
                var month = f.PeriodKind == PeriodKind.Monthly ? f.PeriodKeyValue % 100 : 0;
                foreach (var binding in bindings.Where(b => b.SourceTemplateId == f.TemplateId))
                {
                    targets.Add((binding.Id, binding.RegistryDefId, f.PeriodKeyValue / 100, month));
                }
            }

            foreach (var (bindingId, registryDefId, year, month) in targets)
            {
                int? targetMonth = month == 0 ? null : month;
                await jobs
                    .EnqueueCoalescedAsync<IRegistryImpactRecalculationJob>(
                        RegistryImpactRecalculationTarget.ForSourceChange(bindingId, year, targetMonth),
                        new RegistryImpactRecalculationRequest(
                            registryDefId,
                            [],
                            string.Create(CultureInfo.InvariantCulture, $"source binding {bindingId}: {year}/{month} changed"),
                            year,
                            bindingId,
                            targetMonth,
                            sourceHop),
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

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "B179: не вдалось поставити перерахунок залежних після перерахунку документів-джерел {DocumentIds}.")]
    private static partial void LogEnqueueFailed(ILogger logger, string documentIds, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "B179: ланцюжок документів-джерел дійшов ланки {Hop} (документи {DocumentIds}) — стеля, залежних не поставлено.")]
    private static partial void LogHopCap(ILogger logger, string documentIds, int hop);
}
