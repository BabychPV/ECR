// src/Ecr.Infrastructure/Persistence/SourceImpactAssessor.cs
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ecr.Application.DocumentDependencies;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// B80 N5 (D-554): реалізація <see cref="ISourceImpactAssessor"/> — вплив затвердження, повернення із затвердженого чи правки
/// аркуша документа-джерела на залежні документи.
/// </summary>
/// <remarks>
/// <para>
/// Зв'язка діє, коли шаблон документа — її джерело, стан <c>ApprovedOnly</c> (правка — <c>Any</c>), читання з документа ввімкнено,
/// аркуш містить таблиці зв'язки, а документ — той, який завантажувач обрав би за рік (<see cref="DocumentSourceBindingStore"/>).
/// </para>
/// <para>
/// ✎ Залежні періоди — ТЕ САМЕ, що порахує каскад (людина бачила «змінила січень — перерахується ще 2 місяці»: оцінка брала всі
/// місяці річного періоду джерела й лише періоди з рядком стану, а каскад PERF-06b — лише місяці правки й усі відкриті періоди):
/// (1) місяці зміни — те саме рішення, що хук правки й тригер затвердження (<see cref="CascadeMonthsAsync"/>); (2) періоди
/// споживачів — той самий код відображення (<see cref="Jobs.SourceCascadeRouting"/>); (3) план каскаду кожного документа — той
/// самий код задачі (<see cref="Jobs.ISourceCascadePlanner"/>: пізніші читачі, закриті якорі, проходи читачів). Кожен період
/// несе причину (<see cref="SourceImpactConsumer.Reason"/>), щоб людина розуміла, звідки він. Період, у який писати не можна
/// (затверджено все), лишається в переліку з <c>SkipReason</c> — політика зв'язки захищає саме його.
/// </para>
/// <para>
/// Відбиток — SHA-256 від дії, адреси джерела, політики й відсортованого переліку залежних (зв'язка, документ, період,
/// затверджений, буде перераховано, відсортовані <c>SheetDefId</c> затверджених аркушів і кількість аркушів): змінилось
/// що-небудь із цього — підтвердження застаріло.
/// </para>
/// </remarks>
/// <param name="db">Контекст.</param>
/// <param name="graph">Граф залежностей документів: споживачі зв'язки.</param>
/// <param name="planner">
/// ✎ Прогноз плану каскаду споживача тим самим кодом, що його виконує (<see cref="Jobs.RecalculationJob"/>); <c>null</c> (ручна
/// збірка) — лише періоди відображення зв'язки, без пізніших читачів, і місяці правки не звужуються.
/// </param>
/// <param name="trigger">Тригер перерахунку: чи черга несе місяці правки (<c>D-572</c>); <c>null</c> — не несе.</param>
/// <param name="logger">Журнал (збій прогнозу — запасний шлях «більше», не 500).</param>
internal sealed partial class SourceImpactAssessor(
    EcrDbContext db,
    IDependencyGraphProvider graph,
    Jobs.ISourceCascadePlanner? planner = null,
    ICalculationTrigger? trigger = null,
    ILogger<SourceImpactAssessor>? logger = null) : ISourceImpactAssessor
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <inheritdoc />
    /// <remarks>
    /// ✎ D-581 (огляд M1): шлях транзакції Approve/Reopen (<c>SourceImpactCheck</c>) — ЛИШЕ факти політики: періоди
    /// відображення (будь-якого стану) і затверджені/закриті пізніші читачі, без плану каскаду (план — у
    /// <see cref="AssessAsync(long, int, int, SourceApprovalChange, IReadOnlyCollection{int}?, CancellationToken)"/>). Відбиток
    /// той самий: він рахується лише з фактів політики.
    /// </remarks>
    public Task<SourceImpact> AssessAsync(
        long documentId, int sheetDefId, int periodKey, SourceApprovalChange change, CancellationToken ct)
        => CoreAsync(documentId, sheetDefId, periodKey, change, editedMonths: null, withPlan: false, ct);

    /// <inheritdoc />
    public Task<SourceImpact> AssessAsync(
        long documentId, int sheetDefId, int periodKey, SourceApprovalChange change, IReadOnlyCollection<int>? editedMonths,
        CancellationToken ct)
        => CoreAsync(documentId, sheetDefId, periodKey, change, editedMonths, withPlan: true, ct);

    private async Task<SourceImpact> CoreAsync(
        long documentId, int sheetDefId, int periodKey, SourceApprovalChange change, IReadOnlyCollection<int>? editedMonths,
        bool withPlan, CancellationToken ct)
    {
        var year = periodKey / 100;
        var source = await (
                from d in db.Documents.AsNoTracking()
                where d.Id == documentId
                join p in db.Projects.AsNoTracking() on d.ProjectId equals p.Id
                from pv in db.TemplateVersions.AsNoTracking()
                where pv.Id == (d.TemplateId == null ? EcrDbContext.VersionOf(d.ProjectId, periodKey) : EcrDbContext.TemplateVersionOf(d.ProjectId, d.TemplateId.Value, periodKey))
                select new { TemplateId = d.TemplateId ?? pv.TemplateId, p.PeriodKind })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (source is null)
        {
            return Build(change, documentId, sheetDefId, periodKey, DocumentSourcePolicy.None, [], []);
        }

        var months = DocumentSourceGate.OwnedMonths(source.PeriodKind, periodKey % 100).Select(m => (year * 100) + m).ToList();
        var bindings = await db.DocumentSourceBindings.AsNoTracking()
            .Where(b => b.SourceTemplateId == source.TemplateId
                        // Правка комірки: залежні бачать джерело одразу лише за `any`; Approve/Reopen — за `approvedOnly`.
                        && b.SourceState == (change == SourceApprovalChange.Edited ? DocumentSourceState.Any : DocumentSourceState.ApprovedOnly)
                        && b.ReadFromDocument
                        && b.MappingKind == DocumentSourceBinding.Hse400CompositionMapping)
            .OrderBy(b => b.Id)
            .Select(b => new { b.Id, b.Code, b.Policy, b.RecalculateDependents })
            .ToListAsync(ct).ConfigureAwait(false);
        if (bindings.Count == 0)
        {
            return Build(change, documentId, sheetDefId, periodKey, DocumentSourcePolicy.None, months, []);
        }

        // ✎ Місяці, які зміна донесе до каскаду, — те саме рішення, що ухвалять хук правки й тригер затвердження (D-572).
        var (cascadeMonths, wholeYearReason) = await CascadeMonthsAsync(
                documentId, periodKey, source.PeriodKind, change, editedMonths, ct)
            .ConfigureAwait(false);

        var snapshot = await graph.GetAsync(ct).ConfigureAwait(false);
        var templateCodes = snapshot.Nodes
            .Where(n => n.Ref.Kind == DependencyNodeKind.Template)
            .ToDictionary(n => n.Ref.Id, n => n.Code);
        var store = new DocumentSourceBindingStore(db, graph);
        var impact = new RegistryImpactStore(db);
        var consumers = new List<SourceImpactConsumer>();
        var policies = new List<(DocumentSourcePolicy Policy, bool HasApproved)>();
        foreach (var binding in bindings)
        {
            var tableIds = await store.BoundTableIdsAsync(binding.Id, ct).ConfigureAwait(false);
            if (tableIds.Count == 0
                || !await db.TableDefs.AsNoTracking().AnyAsync(t => tableIds.Contains(t.Id) && t.SheetDefId == sheetDefId, ct).ConfigureAwait(false))
            {
                continue;
            }

            // Споживачі читають лише документ, який обрав би завантажувач; інший документ того ж шаблону їх не зачіпає.
            var chosen = await store.ChosenSourceDocumentAsync(tableIds, year, ct).ConfigureAwait(false);
            if (chosen?.DocumentId != documentId)
            {
                continue;
            }

            // ⛔ Те саме відображення «місяць джерела → періоди споживачів», що виконує RegistryImpactRecalculationJob (задача на
            // кожен місяць; без місяців — одна задача року).
            // ✎ D-581 (огляд H2): періоди будь-якого стану — затверджений закритий місяць зміна теж робить застарілим (D-554).
            var targets = new Dictionary<(long DocumentId, int PeriodKey), Target>();
            foreach (var month in cascadeMonths?.Select(m => (int?)m) ?? [null])
            {
                var routed = await Jobs.SourceCascadeRouting
                    .ConsumersAsync(snapshot, impact, binding.Id, year, month, onCycle: null, ct, anyPeriodState: true)
                    .ConfigureAwait(false);
                foreach (var (consumer, row) in routed.SelectMany(r => r.Rows.Select(row => (r, row))))
                {
                    var key = (row.DocumentId, row.PeriodKey);
                    var monthScope = consumer.Scope.Kind == Ecr.Expressions.Binding.PeriodScopeKind.Month;
                    // Період, до якого ведуть кілька місяців правки, — за власним місяцем (точніша причина).
                    if (!targets.TryGetValue(key, out var seen) || (!seen.MonthScope && month == row.PeriodKey % 100))
                    {
                        targets[key] = new Target(row, month, monthScope);
                    }
                }
            }

            var found = await ConsumersAsync(
                    binding.Id, binding.Code, binding.RecalculateDependents, targets, templateCodes, withPlan, ct)
                .ConfigureAwait(false);
            consumers.AddRange(found);
            // D-554: політика захищає затверджені дані — вмикає її період, де затверджений ХОЧ ОДИН аркуш (він не перерахується й застаріє).
            policies.Add((binding.Policy, found.Exists(c => c.PolicyRelevant && c.HasApprovedSheets)));
        }

        // Політика — найсуворіша серед зв'язок із затвердженими залежними; без таких — найсуворіша серед тих, що діють.
        var policy = policies.Any(p => p.HasApproved)
            ? policies.Where(p => p.HasApproved).Max(p => p.Policy)
            : policies.Count == 0 ? DocumentSourcePolicy.None : policies.Max(p => p.Policy);
        IReadOnlyList<int> sourceMonths = cascadeMonths is null
            ? source.PeriodKind == PeriodKind.Monthly ? months : [.. Enumerable.Range(1, 12).Select(m => (year * 100) + m)]
            : [.. cascadeMonths.Select(m => (year * 100) + m)];
        return Build(change, documentId, sheetDefId, periodKey, policy, sourceMonths, consumers) with
        {
            CascadeMonths = cascadeMonths,
            WholeYearReason = wholeYearReason,
        };
    }

    /// <summary>Період відображення зв'язки: місяць зміни (<c>null</c> — увесь рік) і чи обсяг ребра — «місяць».</summary>
    private sealed record Target(RegistryImpactRow Row, int? Month, bool MonthScope);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "D-581: прогноз каскаду джерела (зв'язка {BindingId}) не вдався — запасний шлях: {Fallback}.")]
    private static partial void LogPlannerFailed(ILogger logger, int bindingId, string fallback, Exception exception);

    /// <summary>
    /// ✎ PERF-06b (<c>D-572</c>): місяці (1–12), які зміна джерела донесе до каскаду, і чому увесь рік (<c>null</c> місяців).
    /// </summary>
    /// <remarks>
    /// Місячне джерело — свій місяць (хук і тригер ставлять задачу місяця). Річне/квартальне: затвердження/повернення — увесь рік
    /// (<see cref="Jobs.SourceApprovalChangeTrigger"/>: місяць лише в місячного); правка — місяці МІСЯЧНИХ колонок правки, якщо
    /// черга несе місяці (<see cref="ICalculationTrigger.CarriesSourceMonths"/>), прохід формул не змінить жодної іншої колонки
    /// (<see cref="WritesOtherColumnAsync"/>, інакше <c>SourceDocumentChangeHook.EditedMonthsAsync</c> місяців не дасть) і доказ
    /// <c>RecalculationJob.SourceKeepsMonthsAsync</c> пройде. Сумнів — увесь рік (більше, не менше).
    /// </remarks>
    private async Task<(IReadOnlyList<int>? Months, string? WholeYearReason)> CascadeMonthsAsync(
        long documentId, int periodKey, PeriodKind kind, SourceApprovalChange change, IReadOnlyCollection<int>? editedMonths,
        CancellationToken ct)
    {
        if (kind == PeriodKind.Monthly)
        {
            return ([periodKey % 100], null);
        }

        if (change != SourceApprovalChange.Edited)
        {
            return (null, SourceImpactReasons.WholeYearPeriodApproval);
        }

        var owned = DocumentSourceGate.OwnedMonths(kind, periodKey % 100).ToHashSet();
        if (editedMonths is not { Count: > 0 } || !editedMonths.All(owned.Contains))
        {
            return (null, SourceImpactReasons.WholeYearEditedMonthsUnknown);
        }

        if (trigger?.CarriesSourceMonths != true || planner is null)
        {
            return (null, SourceImpactReasons.WholeYearQueueWithoutMonths);
        }

        if (await WritesOtherColumnAsync(documentId, periodKey, editedMonths, ct).ConfigureAwait(false))
        {
            return (null, SourceImpactReasons.WholeYearAnnualColumn);
        }

        if (!await planner.SourceKeepsMonthsAsync(documentId, ct).ConfigureAwait(false))
        {
            return (null, SourceImpactReasons.WholeYearSourceSpreadsMonths);
        }

        return ([.. editedMonths.Distinct().Order()], null);
    }

    /// <summary>
    /// Чи прохід формул правки місячних колонок <paramref name="months"/> змінить колонку, що НЕ є місячною колонкою цих місяців
    /// (річний підсумок, формула рядка, інший місяць), — транзитивно за залежностями формул версій документа періоду (насіння —
    /// усі місячні колонки цих місяців: більше, не менше).
    /// </summary>
    private async Task<bool> WritesOtherColumnAsync(
        long documentId, int periodKey, IReadOnlyCollection<int> months, CancellationToken ct)
    {
        var versionIds = await (
                from i in db.TableInstances.AsNoTracking()
                where i.DocumentId == documentId && i.PeriodKeyValue == periodKey && i.SheetCopyId == null
                join t in db.TableDefs.AsNoTracking() on i.TableDefId equals t.Id
                join sh in db.SheetDefs.AsNoTracking() on t.SheetDefId equals sh.Id
                select sh.TemplateVersionId)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        var edges = await (
                from d in db.FormulaDependencies.AsNoTracking()
                join f in db.FormulaDefs.AsNoTracking() on d.FormulaDefId equals f.Id
                join t in db.TableDefs.AsNoTracking() on f.TableDefId equals t.Id
                join sh in db.SheetDefs.AsNoTracking() on t.SheetDefId equals sh.Id
                where versionIds.Contains(sh.TemplateVersionId) && !f.IsDeleted
                join tc in db.ColumnDefs.AsNoTracking() on f.ColumnDefId equals (int?)tc.Id into tcs
                from tc in tcs.DefaultIfEmpty()
                select new
                {
                    d.ColumnDefId,
                    d.TableDefId,
                    TargetTableId = f.TableDefId,
                    TargetId = tc == null ? (int?)null : tc.Id,
                    TargetMonth = tc != null && tc.IsMonthColumn ? (int?)tc.MonthNumber : null,
                })
            .ToListAsync(ct).ConfigureAwait(false);
        if (edges.Count == 0)
        {
            return false;
        }

        var wanted = months.ToHashSet();
        var seeds = (await (
                    from c in db.ColumnDefs.AsNoTracking()
                    join t in db.TableDefs.AsNoTracking() on c.TableDefId equals t.Id
                    join sh in db.SheetDefs.AsNoTracking() on t.SheetDefId equals sh.Id
                    where versionIds.Contains(sh.TemplateVersionId) && c.IsMonthColumn && c.MonthNumber != null
                    select new { c.Id, c.TableDefId, Month = (int)c.MonthNumber!.Value })
                .ToListAsync(ct).ConfigureAwait(false))
            .Where(c => wanted.Contains(c.Month))
            .ToList();
        var changed = seeds.Select(c => c.Id).ToHashSet();
        var tables = seeds.Select(c => c.TableDefId).ToHashSet();
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var edge in edges.Where(e => (e.ColumnDefId is { } c && changed.Contains(c))
                                                  || (e.ColumnDefId is null && e.TableDefId is { } t && tables.Contains(t))))
            {
                if (edge.TargetId is not { } target || edge.TargetMonth is not { } month || !wanted.Contains(month))
                {
                    return true;
                }

                // ✎ Огляд M3: змінена похідна колонка змінює й свою таблицю — ребра «уся таблиця» на неї теж ідуть далі.
                grew |= changed.Add(target) | tables.Add(edge.TargetTableId);
            }
        }

        return false;
    }

    private async Task<List<SourceImpactConsumer>> ConsumersAsync(
        int bindingId,
        string bindingCode,
        bool recalculatesDependents,
        Dictionary<(long DocumentId, int PeriodKey), Target> targets,
        Dictionary<int, string> templateCodes,
        bool withPlan,
        CancellationToken ct)
    {
        if (targets.Count == 0)
        {
            return [];
        }

        var groups = targets.Values
            .GroupBy(t => (t.Row.DocumentId, t.Row.ProjectId, Year: t.Row.PeriodKey / 100))
            .ToList();

        // (1) Політика D-554 (огляд H2): пізніші читачі обчислених чисел змінених періодів — по ВСІХ періодах документа
        // (затверджений чи закритий такий читач каскад не чіпає — він застаріє). Збій — «кожен пізніший читає кожен раніший».
        IReadOnlyDictionary<(long DocumentId, int PeriodKey), int> stale;
        try
        {
            stale = planner is null
                ? new Dictionary<(long DocumentId, int PeriodKey), int>()
                : await planner
                    .StaleReadersAsync(
                        [.. groups.Select(g => (g.Key.DocumentId, g.Key.Year, (IReadOnlyCollection<int>)[.. g.Select(t => t.Row.PeriodKey)]))],
                        ct)
                    .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPlannerFailed(_logger, bindingId, "every later period reads every earlier", ex);
            stale = await EveryLaterPeriodAsync(groups.Select(g => (g.Key.DocumentId, g.Key.Year, g.Min(t => t.Row.PeriodKey))), ct)
                .ConfigureAwait(false);
        }

        // (2) План каскаду (лише GET і «перерахувати затверджені»; транзакція Approve/Reopen — без нього, огляд M1): той самий
        // код, що виконає задача. Біт зв'язки «перераховувати залежних» вимкнено — каскаду немає зовсім.
        var plan = new Dictionary<(long DocumentId, int PeriodKey), Jobs.CascadePlannedPeriod>();
        if (withPlan && recalculatesDependents)
        {
            foreach (var group in groups)
            {
                var open = group.Where(t => t.Row.PeriodState is PeriodState.Open or PeriodState.Grace).Select(t => t.Row.PeriodKey).ToList();
                if (open.Count == 0)
                {
                    continue;
                }

                IReadOnlyList<Jobs.CascadePlannedPeriod> planned;
                try
                {
                    planned = planner is null
                        ? [.. open.Select(p => new Jobs.CascadePlannedPeriod(p, Jobs.CascadePlanReason.Target, null))]
                        : await planner.PlanSourceCascadeAsync(group.Key.ProjectId, group.Key.DocumentId, group.Key.Year, open, ct)
                            .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // ✎ Огляд M2: прогноз не валить дію — цілі відображення (більше, не менше), пізніших читачів показує (1).
                    LogPlannerFailed(_logger, bindingId, "routing targets", ex);
                    planned = [.. open.Select(p => new Jobs.CascadePlannedPeriod(p, Jobs.CascadePlanReason.Target, null))];
                }

                foreach (var p in planned)
                {
                    plan[(group.Key.DocumentId, p.PeriodKey)] = p;
                }
            }
        }

        var keys = targets.Keys.Concat(stale.Keys).Concat(plan.Keys).Distinct().ToList();
        var documentIds = keys.Select(k => k.DocumentId).Distinct().ToList();
        var periodKeys = keys.Select(k => k.PeriodKey).Distinct().ToList();
        var facts = await (
                from d in db.Documents.AsNoTracking()
                where documentIds.Contains(d.Id)
                join p in db.Projects.AsNoTracking() on d.ProjectId equals p.Id
                select new { d.Id, d.ProjectId, d.TemplateId, ProjectCode = p.Code, d.BusinessKey, p.PeriodKind })
            .ToDictionaryAsync(d => d.Id, ct).ConfigureAwait(false);
        var projectIds = facts.Values.Select(f => f.ProjectId).Distinct().ToList();
        var states = await db.Periods.AsNoTracking()
            .Where(p => projectIds.Contains(p.ProjectId) && periodKeys.Contains(p.PeriodKeyValue))
            .Select(p => new { p.ProjectId, p.PeriodKeyValue, p.State })
            .ToDictionaryAsync(p => (p.ProjectId, p.PeriodKeyValue), p => p.State, ct).ConfigureAwait(false);
        var versions = (await (
                    from i in db.TableInstances.AsNoTracking()
                    where documentIds.Contains(i.DocumentId) && periodKeys.Contains(i.PeriodKeyValue) && i.SheetCopyId == null
                    join d in db.Documents.AsNoTracking() on i.DocumentId equals d.Id
                    from pv in db.TemplateVersions.AsNoTracking()
                    where pv.Id == (d.TemplateId == null ? EcrDbContext.VersionOf(d.ProjectId, i.PeriodKeyValue) : EcrDbContext.TemplateVersionOf(d.ProjectId, d.TemplateId.Value, i.PeriodKeyValue))
                    select new { i.DocumentId, i.PeriodKeyValue, VersionId = pv.Id, pv.TemplateId })
                .Distinct()
                .ToListAsync(ct).ConfigureAwait(false))
            .GroupBy(v => (v.DocumentId, v.PeriodKeyValue))
            .ToDictionary(g => g.Key, g => g.First());
        var (minKey, maxKey) = (periodKeys.Min(), periodKeys.Max());
        var approvedStates = (await (
                    from st in db.ApprovalStates.AsNoTracking()
                    where st.SheetCopyId == null && documentIds.Contains(st.DocumentId) && st.PeriodKey >= minKey && st.PeriodKey <= maxKey
                          && st.Status == DocumentStatus.Approved
                    join sd in db.SheetDefs.AsNoTracking() on st.SheetDefId equals sd.Id
                    select new { st.DocumentId, st.PeriodKey, SheetCode = sd.Code })
                .ToListAsync(ct).ConfigureAwait(false))
            .ToLookup(st => (st.DocumentId, st.PeriodKey), st => st.SheetCode);

        // Рядки стану створюються ліниво (лише для аркушів, яких торкнулися), тож «усі затверджені» по існуючих рядках
        // хибно затверджує період із одним затвердженим аркушем із двадцяти. Перелік аркушів — СКЛАД документа за тим
        // самим правилом, що й у процесі затвердження (див. ApprovalSheetScope); аркуш без рядка стану — Draft.
        var composition = (await ApprovalSheetScope.RowsAsync(db, documentIds, ct).ConfigureAwait(false))
            .ToLookup(r => r.DocumentId);
        // Історичний документ без складу (рядків `doc.DocumentSheet` немає) — аркуші версії шаблону періоду, не видалені.
        var versionIds = versions.Values.Select(v => v.VersionId).Distinct().ToList();
        var versionSheets = (await db.SheetDefs.AsNoTracking()
                .Where(sd => versionIds.Contains(sd.TemplateVersionId) && !sd.IsDeleted)
                .OrderBy(sd => sd.Ordinal).ThenBy(sd => sd.Id)
                .Select(sd => new { sd.Id, sd.Code, sd.TemplateVersionId })
                .ToListAsync(ct).ConfigureAwait(false))
            .ToLookup(sd => sd.TemplateVersionId);
        var approvedCopies = (await db.ApprovalStates.AsNoTracking()
                .Where(st => st.SheetCopyId != null && documentIds.Contains(st.DocumentId)
                             && st.PeriodKey >= minKey && st.PeriodKey <= maxKey && st.Status == DocumentStatus.Approved)
                .Select(st => new { st.DocumentId, st.PeriodKey, CopyId = st.SheetCopyId!.Value })
                .ToListAsync(ct).ConfigureAwait(false))
            .Select(st => (st.DocumentId, st.PeriodKey, st.CopyId))
            .ToHashSet();

        var result = new List<SourceImpactConsumer>();
        foreach (var key in keys.OrderBy(k => k.DocumentId).ThenBy(k => k.PeriodKey))
        {
            var (documentId, periodKey) = key;
            if (!facts.TryGetValue(documentId, out var document) || !versions.TryGetValue(key, out var version))
            {
                continue;
            }

            // Аркуш (за кодом: склад тримає чинну версію, а період міг лишитися на старій) затверджений, коли затверджено
            // оригінал і кожну включену копію (стан копії свій, RS) — як у `WorkflowStore.AllSheetsApprovedAsync`.
            var approvedCodes = approvedStates[key].ToHashSet(StringComparer.Ordinal);
            var rows = composition[documentId].ToList();
            if (rows.Count == 0)
            {
                rows = [.. versionSheets[version.VersionId].Select(sd => new ApprovalSheetRow(0, documentId, sd.Id, sd.Code, null))];
            }

            var sheets = rows
                .Where(r => r.CopyOfId == null)
                .Select(r => new SourceImpactSheet(
                    r.SheetDefId,
                    r.SheetCode,
                    approvedCodes.Contains(r.SheetCode)
                    && rows.Where(c => c.CopyOfId == r.Id).All(c => approvedCopies.Contains((documentId, periodKey, c.Id)))))
                .ToList();
            var approved = sheets.Count > 0 && sheets.TrueForAll(sh => sh.Approved);
            var state = states.TryGetValue((document.ProjectId, periodKey), out var known) ? known : PeriodState.Closed;
            var open = state is PeriodState.Open or PeriodState.Grace;

            // Політика: ціль відображення — завжди; пізніший читач — лише той, чиї затверджені аркуші (чи закритий період)
            // каскад не перерахує. Відкритий незатверджений читач у політиці не важить (лише в плані, коли той його рахує).
            var isTarget = targets.TryGetValue(key, out var target);
            var staleReads = stale.TryGetValue(key, out var reads) ? reads : (int?)null;
            var policy = isTarget || (staleReads is not null && (!open || sheets.Exists(sh => sh.Approved)));
            var planned = plan.TryGetValue(key, out var p) ? p : null;
            if (!policy && planned is null)
            {
                continue;
            }

            var willRecalculate = withPlan
                ? planned is not null
                : isTarget && open && !approved && recalculatesDependents;
            var reason = planned?.Reason switch
            {
                Jobs.CascadePlanReason.CrossMonth => SourceImpactReasons.CrossMonth,
                Jobs.CascadePlanReason.AnnualAnchor => SourceImpactReasons.AnnualAnchor,
                Jobs.CascadePlanReason.ReaderPass => SourceImpactReasons.ReaderPass,
                _ when isTarget => TargetReason(target!, document.PeriodKind),
                _ => staleReads is not null ? SourceImpactReasons.CrossMonth : SourceImpactReasons.SourceMonth,
            };
            var templateId = document.TemplateId ?? version.TemplateId;
            result.Add(new SourceImpactConsumer(
                bindingId,
                bindingCode,
                templateCodes.TryGetValue(templateId, out var code) ? code : templateId.ToString(CultureInfo.InvariantCulture),
                documentId,
                document.ProjectCode,
                document.BusinessKey,
                periodKey,
                approved,
                sheets)
            {
                WillRecalculate = willRecalculate,
                SkipReason = willRecalculate ? null
                    : state == PeriodState.Closed ? SourceImpactReasons.SkipClosed
                    : !open ? SourceImpactReasons.SkipNotWritable
                    // Затверджені аркуші пропускаються, а решта аркушів не має що рахувати (немає таблиць у періоді) — задача
                    // відмовляє періодом так само, як за «затверджено все» (RefusedPeriodsAsync: кожен екземплярний скоуп пропущено).
                    : approved || sheets.Exists(sh => sh.Approved) ? SourceImpactReasons.SkipApproved
                    : recalculatesDependents ? SourceImpactReasons.SkipNotWritable
                    : SourceImpactReasons.SkipDependentsOff,
                Reason = reason,
                ReadsPeriodKey = reason == SourceImpactReasons.CrossMonth ? planned?.ReadsPeriodKey ?? staleReads : null,
                PolicyRelevant = policy,
            });
        }

        return result;
    }

    /// <summary>
    /// ✎ Огляд L3: чому ціль відображення в переліку — за періодичністю ПРОЄКТУ споживача: період володіє місяцем зміни
    /// (квартал — три місяці) чи ребро «місяць» — <c>sourceMonth</c>; інакше шаблон читає джерело за рік (ребро «рік»/«усе»,
    /// відмова звуження) — <c>readsYear</c>; місяць зміни невідомий — <c>sourceYear</c>.
    /// </summary>
    private static string TargetReason(Target target, PeriodKind consumerKind)
        => target.Month is not { } m
            ? SourceImpactReasons.SourceYear
            : target.MonthScope || DocumentSourceGate.OwnedMonths(consumerKind, target.Row.PeriodKey % 100).Contains(m)
                ? SourceImpactReasons.SourceMonth
                : SourceImpactReasons.ReadsYear;

    /// <summary>✎ Огляд M2: запасний шлях — кожен пізніший період документа за рік (з екземплярами) вважається читачем.</summary>
    private async Task<IReadOnlyDictionary<(long DocumentId, int PeriodKey), int>> EveryLaterPeriodAsync(
        IEnumerable<(long DocumentId, int Year, int First)> groups, CancellationToken ct)
    {
        var list = groups.ToList();
        var ids = list.Select(g => g.DocumentId).Distinct().ToList();
        var periods = (await db.TableInstances.AsNoTracking()
                .Where(i => ids.Contains(i.DocumentId) && i.SheetCopyId == null)
                .Select(i => new { i.DocumentId, i.PeriodKeyValue })
                .Distinct()
                .ToListAsync(ct).ConfigureAwait(false))
            .ToLookup(i => i.DocumentId, i => i.PeriodKeyValue);
        var result = new Dictionary<(long DocumentId, int PeriodKey), int>();
        foreach (var (documentId, year, first) in list)
        {
            foreach (var period in periods[documentId].Where(p => p / 100 == year && p > first))
            {
                result[(documentId, period)] = first;
            }
        }

        return result;
    }

    private static SourceImpact Build(
        SourceApprovalChange change,
        long documentId,
        int sheetDefId,
        int periodKey,
        DocumentSourcePolicy policy,
        IReadOnlyList<int> months,
        IReadOnlyList<SourceImpactConsumer> consumers)
    {
        var text = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"{change}|{documentId}|{sheetDefId}|{periodKey}|{policy}");
        // ✎ D-581 (огляд M1): лише факти політики — шлях транзакції (без плану) і GET (з планом) дають той самий відбиток.
        foreach (var c in consumers.Where(c => c.PolicyRelevant).OrderBy(c => c.BindingId).ThenBy(c => c.DocumentId).ThenBy(c => c.PeriodKey))
        {
            text.Append(CultureInfo.InvariantCulture, $"|{c.BindingId}:{c.DocumentId}:{c.PeriodKey}:{(c.Approved ? 1 : 0)}:{string.Join(',', c.Sheets.Where(sh => sh.Approved).Select(sh => sh.SheetDefId).Order())}/{c.Sheets.Count}");
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant()[..32];
        return new SourceImpact(change, documentId, sheetDefId, periodKey, policy, months, consumers, hash);
    }
}
