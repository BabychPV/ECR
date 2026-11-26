// src/Ecr.Infrastructure/Persistence/SourceImpactAssessor.cs
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ecr.Application.DocumentDependencies;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Microsoft.EntityFrameworkCore;

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
internal sealed class SourceImpactAssessor(
    EcrDbContext db,
    IDependencyGraphProvider graph,
    Jobs.ISourceCascadePlanner? planner = null,
    ICalculationTrigger? trigger = null) : ISourceImpactAssessor
{
    /// <inheritdoc />
    public Task<SourceImpact> AssessAsync(
        long documentId, int sheetDefId, int periodKey, SourceApprovalChange change, CancellationToken ct)
        => AssessAsync(documentId, sheetDefId, periodKey, change, editedMonths: null, ct);

    /// <inheritdoc />
    public async Task<SourceImpact> AssessAsync(
        long documentId, int sheetDefId, int periodKey, SourceApprovalChange change, IReadOnlyCollection<int>? editedMonths,
        CancellationToken ct)
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
            var targets = new Dictionary<(long DocumentId, int PeriodKey), (RegistryImpactRow Row, string Reason)>();
            foreach (var month in cascadeMonths?.Select(m => (int?)m) ?? [null])
            {
                var routed = await Jobs.SourceCascadeRouting
                    .ConsumersAsync(snapshot, impact, binding.Id, year, month, onCycle: null, ct)
                    .ConfigureAwait(false);
                foreach (var (consumer, row) in routed.SelectMany(r => r.Rows.Select(row => (r, row))))
                {
                    var reason = month is not { } m
                        ? SourceImpactReasons.SourceYear
                        : consumer.Scope.Kind == Ecr.Expressions.Binding.PeriodScopeKind.Month || row.PeriodKey % 100 == m
                            ? SourceImpactReasons.SourceMonth
                            : SourceImpactReasons.ReadsYear;
                    var key = (row.DocumentId, row.PeriodKey);
                    if (!targets.TryGetValue(key, out var seen) || Rank(reason) < Rank(seen.Reason))
                    {
                        targets[key] = (row, reason);
                    }
                }
            }

            var found = await ConsumersAsync(
                    binding.Id, binding.Code, binding.RecalculateDependents, targets, templateCodes, ct)
                .ConfigureAwait(false);
            consumers.AddRange(found);
            // D-554: політика захищає затверджені дані — вмикає її період, де затверджений ХОЧ ОДИН аркуш (він не перерахується й застаріє).
            policies.Add((binding.Policy, found.Exists(c => c.HasApprovedSheets)));
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

    /// <summary>Пріоритет причини (менше — точніше): період, до якого ведуть кілька місяців, показує найточнішу.</summary>
    private static int Rank(string reason) => reason switch
    {
        SourceImpactReasons.SourceMonth => 0,
        SourceImpactReasons.ReadsYear => 1,
        _ => 2,
    };

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

                grew |= changed.Add(target);
            }
        }

        return false;
    }

    private async Task<List<SourceImpactConsumer>> ConsumersAsync(
        int bindingId,
        string bindingCode,
        bool recalculatesDependents,
        Dictionary<(long DocumentId, int PeriodKey), (RegistryImpactRow Row, string Reason)> targets,
        Dictionary<int, string> templateCodes,
        CancellationToken ct)
    {
        if (targets.Count == 0)
        {
            return [];
        }

        // ⛔ План каскаду кожного документа-споживача за рік — той самий код, що його виконає (RecalculationJob): періоди
        // відображення, у які можна писати, закриті якорі, пізніші читачі обчислених чисел і проходи читачів результатів.
        // Біт зв'язки «перераховувати залежних» вимкнено — каскаду немає зовсім (хук і тригер мовчать).
        var periods = targets.ToDictionary(t => t.Key, t => (t.Value.Reason, Reads: (int?)null, WillRecalculate: false));
        if (recalculatesDependents)
        {
            foreach (var group in targets.Values.GroupBy(t => (t.Row.DocumentId, t.Row.ProjectId, Year: t.Row.PeriodKey / 100)))
            {
                IReadOnlyList<Jobs.CascadePlannedPeriod> plan = planner is null
                    ? [.. group.Select(t => new Jobs.CascadePlannedPeriod(t.Row.PeriodKey, Jobs.CascadePlanReason.Target, null))]
                    : await planner
                        .PlanSourceCascadeAsync(
                            group.Key.ProjectId, group.Key.DocumentId, group.Key.Year, [.. group.Select(t => t.Row.PeriodKey)], ct)
                        .ConfigureAwait(false);
                foreach (var planned in plan)
                {
                    var key = (group.Key.DocumentId, planned.PeriodKey);
                    periods[key] = planned.Reason switch
                    {
                        Jobs.CascadePlanReason.CrossMonth => (SourceImpactReasons.CrossMonth, planned.ReadsPeriodKey, true),
                        Jobs.CascadePlanReason.AnnualAnchor => (SourceImpactReasons.AnnualAnchor, null, true),
                        Jobs.CascadePlanReason.ReaderPass => (SourceImpactReasons.ReaderPass, null, true),
                        _ => (periods.TryGetValue(key, out var target) ? target.Reason : SourceImpactReasons.SourceMonth, null, true),
                    };
                }
            }
        }

        var documentIds = periods.Keys.Select(k => k.DocumentId).Distinct().ToList();
        var periodKeys = periods.Keys.Select(k => k.PeriodKey).Distinct().ToList();
        var facts = await (
                from d in db.Documents.AsNoTracking()
                where documentIds.Contains(d.Id)
                join p in db.Projects.AsNoTracking() on d.ProjectId equals p.Id
                select new { d.Id, d.TemplateId, ProjectCode = p.Code, d.BusinessKey })
            .ToDictionaryAsync(d => d.Id, ct).ConfigureAwait(false);
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
                    from s in db.ApprovalStates.AsNoTracking()
                    where s.SheetCopyId == null && documentIds.Contains(s.DocumentId) && s.PeriodKey >= minKey && s.PeriodKey <= maxKey
                          && s.Status == DocumentStatus.Approved
                    join sd in db.SheetDefs.AsNoTracking() on s.SheetDefId equals sd.Id
                    select new { s.DocumentId, s.PeriodKey, SheetCode = sd.Code })
                .ToListAsync(ct).ConfigureAwait(false))
            .ToLookup(s => (s.DocumentId, s.PeriodKey), s => s.SheetCode);

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
                .Where(s => s.SheetCopyId != null && documentIds.Contains(s.DocumentId)
                            && s.PeriodKey >= minKey && s.PeriodKey <= maxKey && s.Status == DocumentStatus.Approved)
                .Select(s => new { s.DocumentId, s.PeriodKey, CopyId = s.SheetCopyId!.Value })
                .ToListAsync(ct).ConfigureAwait(false))
            .Select(s => (s.DocumentId, s.PeriodKey, s.CopyId))
            .ToHashSet();

        var result = new List<SourceImpactConsumer>();
        foreach (var (key, (reason, reads, willRecalculate)) in periods.OrderBy(p => p.Key.DocumentId).ThenBy(p => p.Key.PeriodKey))
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
                    // Затверджені аркуші пропускаються, а решта аркушів не має що рахувати (немає таблиць у періоді) — задача
                    // відмовляє періодом так само, як за «затверджено все» (RefusedPeriodsAsync: кожен екземплярний скоуп пропущено).
                    : approved || sheets.Exists(sh => sh.Approved) ? SourceImpactReasons.SkipApproved
                    : recalculatesDependents ? SourceImpactReasons.SkipNotWritable
                    : SourceImpactReasons.SkipDependentsOff,
                Reason = reason,
                ReadsPeriodKey = reads,
            });
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
        foreach (var c in consumers.OrderBy(c => c.BindingId).ThenBy(c => c.DocumentId).ThenBy(c => c.PeriodKey))
        {
            text.Append(CultureInfo.InvariantCulture, $"|{c.BindingId}:{c.DocumentId}:{c.PeriodKey}:{(c.Approved ? 1 : 0)}:{(c.WillRecalculate ? 1 : 0)}:{string.Join(',', c.Sheets.Where(sh => sh.Approved).Select(sh => sh.SheetDefId).Order())}/{c.Sheets.Count}");
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant()[..32];
        return new SourceImpact(change, documentId, sheetDefId, periodKey, policy, months, consumers, hash);
    }
}
