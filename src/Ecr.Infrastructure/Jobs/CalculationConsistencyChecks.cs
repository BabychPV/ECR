// src/Ecr.Infrastructure/Jobs/CalculationConsistencyChecks.cs
using System.Diagnostics;
using System.Globalization;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Правила узгодженості результатів розрахунку й методологій (аудит 24.11, R1–R5, R7, R8) — частина
/// <see cref="ConsistencyCheckJob"/>.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Людина 24.11: «в рамках методології перевір це, бо я не певен, що воно дійсно працює». Вісім структурних правил
/// <see cref="ConsistencyCheckJob"/> не бачили жодного з трьох реальних дефектів розрахунку, знайдених на live того ж дня
/// (результати не чинної версії, методологія лишилась на формулах, сироти проблем розрахунку). Ці правила дивляться саме
/// туди: актуальні прогони (<c>Current</c>), їхні результати, чинні версії методологій і прив'язки.
/// </para>
/// <para>
/// ⚠ «Чинна версія» — те саме правило, що в оркестраторі розрахунку (<c>MethodologyResolver.ResolveBindingVersionAsync</c>):
/// ОПУБЛІКОВАНА версія з <c>EffectiveFrom ≤ кінець періоду</c>, найчинніша за <see cref="MethodologyVersionKey.Currency"/>.
/// Інше правило вибору тут означало б знахідки, яких перерахунок не виправить.
/// </para>
/// <para>
/// ⚠ Усе читання — малі таблиці конфігурації (версії, виходи, прив'язки) і рядки ЛИШЕ актуальних прогонів; партиції
/// <c>doc.CellValue</c> читає тільки <see cref="RowInputsCompleteNoResultRule"/>, і лише рядки прив'язаних таблиць у
/// періодах актуальних прогонів, лише колонки обов'язкових входів і статусу. Кожен запит має стелю (правило 6
/// <c>LayerRulesTests</c>); правило, що вперлося в стелю, повертає <c>Complete = false</c> і своїх старих знахідок не
/// видаляє (D-480).
/// </para>
/// </remarks>
/// <param name="db">Контекст задачі.</param>
/// <param name="utcNow">Момент прогону: «чинна сьогодні» версія для R3/R8.</param>
internal sealed class CalculationConsistencyChecks(EcrDbContext db, DateTime utcNow)
{
    /// <summary>R1: результат актуального прогону порахований не чинною для його періоду версією методології.</summary>
    public const string ResultVersionNotEffectiveRule = "CALC_RESULT_VERSION_NOT_EFFECTIVE";

    /// <summary>R1, текст для місяця із затвердженими аркушами (затверджене не перераховується).</summary>
    public const string ResultVersionNotEffectiveApprovedKey = "CALC_RESULT_VERSION_NOT_EFFECTIVE_APPROVED";

    /// <summary>R3: чинна опублікована версія активної методології досі на рушії <c>Formulas</c>.</summary>
    public const string EffectiveVersionFormulasRule = "EFFECTIVE_VERSION_FORMULAS";

    /// <summary>R4: рядок прив'язаної таблиці з усіма обов'язковими входами без результату й без проблеми в актуальному прогоні.</summary>
    public const string RowInputsCompleteNoResultRule = "ROW_INPUTS_COMPLETE_NO_RESULT";

    /// <summary>R4, підсумок «і ще N рядків» понад <see cref="MaxRowFindingsPerDocumentPeriod"/>.</summary>
    public const string RowInputsCompleteNoResultMoreKey = "ROW_INPUTS_COMPLETE_NO_RESULT_MORE";

    /// <summary>R5: актуальний прогін без жодного результату й жодної проблеми, хоча прив'язані таблиці з рядками є.</summary>
    public const string CurrentRunNoResultsRule = "CURRENT_RUN_NO_RESULTS_WITH_BINDINGS";

    /// <summary>R2: проблема розрахунку (<c>calc.CalculationIssue</c>), чийого прогону немає.</summary>
    public const string IssueWithoutRunRule = "CALC_ISSUE_WITHOUT_RUN";

    /// <summary>R8: вихід чинної версії прив'язаної методології без жодної чинної прив'язки на живих версіях шаблонів.</summary>
    public const string OutputWithoutBindingRule = "OUTPUT_WITHOUT_BINDING";

    /// <summary>R7: чинна прив'язка на живій версії шаблону веде у видалену колонку/таблицю/аркуш.</summary>
    public const string BindingDeadColumnRule = "BINDING_DEAD_COLUMN";

    /// <summary>R4: скільки рядків одного документа × періоду називати поіменно; решта — одним підсумком.</summary>
    public const int MaxRowFindingsPerDocumentPeriod = 50;

    /// <summary>Сутність R1 — найменший <c>Id</c> результату групи «прогін × документ × період × версія».</summary>
    public const string ResultEntity = "calc.CalculationResult";

    /// <summary>Сутність R2, R5 і підсумку R4.</summary>
    public const string RunEntity = "calc.CalculationRun";

    /// <summary>Сутність R3.</summary>
    public const string VersionEntity = "calc.MethodologyVersion";

    /// <summary>Сутність R4 (рядок документа).</summary>
    public const string RowEntity = "doc.TableRow";

    /// <summary>Сутність R8.</summary>
    public const string OutputEntity = "calc.MethodologyOutput";

    /// <summary>Сутність R7.</summary>
    public const string BindingEntity = "cfg.CalculationBinding";

    /// <summary>Стеля читання конфігурації (версій, прогонів, прив'язок).</summary>
    private const int MaxConfigRows = 100_000;

    /// <summary>Стеля рядків/комірок одного документа × періоду для R4.</summary>
    private const int MaxRowsPerDocumentPeriod = 200_000;

    /// <summary>Стеля проблем одного прогону, яку пише <c>CalculationIssueStore</c> на таблицю (повна, якщо менше).</summary>
    private const int IssueCapPerTable = Ecr.Application.Ports.ICalculationIssueStore.MaxPerTable;

    private Context? _context;

    /// <summary>Області знахідок правила (D-480: правило, перевірене повністю, замінює знахідки саме цих областей).</summary>
    /// <param name="ruleCode">Код правила.</param>
    public static IReadOnlyList<string> EntityTypesOf(string ruleCode) => ruleCode switch
    {
        ResultVersionNotEffectiveRule => [ResultEntity],
        EffectiveVersionFormulasRule => [VersionEntity],
        RowInputsCompleteNoResultRule => [RowEntity, RunEntity],
        CurrentRunNoResultsRule => [RunEntity],
        IssueWithoutRunRule => [RunEntity],
        OutputWithoutBindingRule => [OutputEntity],
        BindingDeadColumnRule => [BindingEntity],
        _ => throw new ArgumentOutOfRangeException(nameof(ruleCode), ruleCode, "Невідоме правило розрахунку."),
    };

    /// <summary>Усі правила по черзі; кожне — зі своїм часом.</summary>
    /// <param name="limit">Стеля знахідок одного правила.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<IReadOnlyList<CalcCheckOutcome>> RunAllAsync(int limit, CancellationToken ct)
    {
        var outcomes = new List<CalcCheckOutcome>();

        async Task Timed(string rule, Func<Task<(List<CalcFinding> Findings, bool Complete)>> check)
        {
            var watch = Stopwatch.StartNew();
            var (findings, complete) = await check().ConfigureAwait(false);
            outcomes.Add(new CalcCheckOutcome(rule, findings, complete, watch.ElapsedMilliseconds));
        }

        await Timed(ResultVersionNotEffectiveRule, () => ResultVersionNotEffectiveAsync(limit, ct)).ConfigureAwait(false);
        await Timed(EffectiveVersionFormulasRule, () => EffectiveVersionFormulasAsync(limit, ct)).ConfigureAwait(false);
        await Timed(RowInputsCompleteNoResultRule, () => RowInputsCompleteNoResultAsync(limit, ct)).ConfigureAwait(false);
        await Timed(CurrentRunNoResultsRule, () => CurrentRunNoResultsAsync(limit, ct)).ConfigureAwait(false);
        await Timed(IssueWithoutRunRule, () => IssueWithoutRunAsync(limit, ct)).ConfigureAwait(false);
        await Timed(OutputWithoutBindingRule, () => OutputWithoutBindingAsync(limit, ct)).ConfigureAwait(false);
        await Timed(BindingDeadColumnRule, () => BindingDeadColumnAsync(limit, ct)).ConfigureAwait(false);

        return outcomes;
    }

    /// <summary>
    /// R1: група результатів актуального прогону (документ × період × версія), чия версія — не чинна для періоду.
    /// </summary>
    /// <remarks>
    /// Причина на live 24.11: місяць пораховано 13.11 версіями <c>Formulas</c>, а відтоді опубліковано версії
    /// <c>Script</c> — перерахунку не було, і екран показує числа старої методики без жодної позначки. Вага 2 і для
    /// затвердженого місяця (рішення людини 24.11: «info на затверджених — неприйнятно»); текст тоді каже, що
    /// затверджене не перераховується і місяць треба повернути в роботу.
    /// </remarks>
    public async Task<(List<CalcFinding> Findings, bool Complete)> ResultVersionNotEffectiveAsync(int limit, CancellationToken ct)
    {
        var context = await ContextAsync(ct).ConfigureAwait(false);

        var groups = await (
                from result in db.CalculationResults.AsNoTracking()
                join run in db.CalculationRuns.AsNoTracking() on result.CalculationRunId equals run.Id
                where run.Status == CalculationRun.CurrentStatus
                group result by new { result.CalculationRunId, run.ProjectId, result.DocumentId, result.PeriodKey, result.MethodologyVersionId }
                into g
                orderby g.Key.CalculationRunId, g.Key.MethodologyVersionId, g.Key.DocumentId
                select new
                {
                    g.Key.CalculationRunId,
                    g.Key.ProjectId,
                    g.Key.DocumentId,
                    g.Key.PeriodKey,
                    g.Key.MethodologyVersionId,
                    FirstId = g.Min(r => r.Id),
                    Count = g.Count(),
                })
            .Take(MaxConfigRows)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var stale = new List<(long RunId, long DocumentId, int PeriodKey, VersionRow Version, VersionRow? Effective, long FirstId, int Count)>();
        var projectRuns = context.Runs.Where(r => r.DocumentId is null).Select(r => r.Id).ToHashSet();
        foreach (var g in groups)
        {
            // Результати проєктного прогону документа, що має власний актуальний прогін періоду, ніхто не читає.
            if (projectRuns.Contains(g.CalculationRunId) && context.DocumentRuns.Contains((g.DocumentId, g.PeriodKey)))
            {
                continue;
            }

            if (!context.Versions.TryGetValue(g.MethodologyVersionId, out var version)
                || !context.PeriodEnds.TryGetValue((g.ProjectId, g.PeriodKey), out var periodEnd))
            {
                continue;
            }

            var effective = context.Effective(version.MethodologyId, periodEnd);
            if (effective?.Id != version.Id)
            {
                stale.Add((g.CalculationRunId, g.DocumentId, g.PeriodKey, version, effective, g.FirstId, g.Count));
            }
        }

        var approved = await ApprovedAsync([.. stale.Select(s => s.DocumentId).Distinct()], ct).ConfigureAwait(false);

        var findings = stale
            .Take(limit)
            .Select(s => new CalcFinding(
                ResultVersionNotEffectiveRule,
                Severity: 2,
                ResultEntity,
                s.FirstId,
                ConsistencyCheckJob.FindingText(
                    approved.Contains((s.DocumentId, s.PeriodKey)) ? ResultVersionNotEffectiveApprovedKey : ResultVersionNotEffectiveRule,
                    ("documentId", s.DocumentId),
                    ("period", s.PeriodKey),
                    ("methodology", context.Code(s.Version.MethodologyId)),
                    ("methodologyId", s.Version.MethodologyId),
                    ("resultVersion", s.Version.Version),
                    ("effectiveVersion", s.Effective?.Version ?? "—"),
                    ("results", s.Count),
                    ("runId", s.RunId)),
                s.PeriodKey))
            .ToList();

        return (findings, groups.Count < MaxConfigRows && context.Complete);
    }

    /// <summary>R3: активна методологія, чия чинна сьогодні опублікована версія — на рушії <c>Formulas</c>.</summary>
    /// <remarks>Рішення людини 24.11 «методології лише Script, без втрат»: така версія ще не перенесена.</remarks>
    public async Task<(List<CalcFinding> Findings, bool Complete)> EffectiveVersionFormulasAsync(int limit, CancellationToken ct)
    {
        var context = await ContextAsync(ct).ConfigureAwait(false);
        var today = DateOnly.FromDateTime(utcNow);

        var findings = context.Methodologies.Values
            .Where(m => m.IsActive)
            .OrderBy(m => m.Code, StringComparer.Ordinal)
            .Select(m => context.Effective(m.Id, today))
            .Where(v => v is { Engine: MethodologyEngine.Formulas })
            .Take(limit)
            .Select(v => new CalcFinding(
                EffectiveVersionFormulasRule,
                Severity: 2,
                VersionEntity,
                v!.Id,
                ConsistencyCheckJob.FindingText(
                    EffectiveVersionFormulasRule,
                    ("methodology", context.Code(v.MethodologyId)),
                    ("methodologyId", v.MethodologyId),
                    ("version", v.Version),
                    ("effectiveFrom", v.EffectiveFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "—"))))
            .ToList();

        return (findings, context.Complete);
    }

    /// <summary>
    /// R5: актуальний прогін без жодного результату й жодної проблеми, хоча в його документах за період є прив'язані
    /// таблиці з рядками, а прив'язана методологія має чинну версію.
    /// </summary>
    /// <remarks>
    /// Прогін без прив'язок (live 24.11: прогін 10143 документа 93) — законний нуль, тому умова саме з прив'язками.
    /// Нуль результатів за наявних проблем (усі рядки без входів) — теж не мовчання, а видима причина. Затверджені
    /// аркуші не рахуються (їхні результати переносяться), тож їхні таблиці сюди не входять.
    /// </remarks>
    public async Task<(List<CalcFinding> Findings, bool Complete)> CurrentRunNoResultsAsync(int limit, CancellationToken ct)
    {
        var context = await ContextAsync(ct).ConfigureAwait(false);

        var silent = await db.CalculationRuns.AsNoTracking()
            .Where(r => r.Status == CalculationRun.CurrentStatus && r.PeriodKey != null
                        && !db.CalculationResults.Any(x => x.CalculationRunId == r.Id)
                        && !db.CalculationIssues.Any(i => i.CalculationRunId == r.Id))
            .OrderBy(r => r.Id)
            .Select(r => r.Id)
            .Take(MaxConfigRows)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var findings = new List<CalcFinding>();
        foreach (var run in context.Runs.Where(r => silent.Contains(r.Id)))
        {
            if (findings.Count >= limit)
            {
                break;
            }

            var bound = await BoundInstancesAsync(run, ct).ConfigureAwait(false);
            var live = bound
                .Where(b => b.HasRows && context.Effective(b.MethodologyId, run.PeriodEnd) is not null)
                .ToList();
            if (live.Count == 0)
            {
                continue;
            }

            findings.Add(new CalcFinding(
                CurrentRunNoResultsRule,
                Severity: 3,
                RunEntity,
                run.Id,
                ConsistencyCheckJob.FindingText(
                    CurrentRunNoResultsRule,
                    ("runId", run.Id),
                    ("documentId", run.DocumentId?.ToString(CultureInfo.InvariantCulture) ?? "—"),
                    ("period", run.PeriodKey),
                    ("tables", live.Select(b => b.InstanceId).Distinct().Count()),
                    ("methodologies", live.Select(b => b.MethodologyId).Distinct().Count())),
                run.PeriodKey));
        }

        return (findings, silent.Count < MaxConfigRows && context.Complete);
    }

    /// <summary>R2: проблеми розрахунку, чийого прогону немає — одна знахідка на зниклий прогін.</summary>
    /// <remarks>
    /// Друга лінія: після <c>FK_CIssue_Run</c> (lane calc-issue-orphans) такий стан неможливий, і знахідка означає
    /// базу, де ключ ще не створено, або дані поза звичайним шляхом. Live 24.11: 323 із 416 проблем — сироти, і панель
    /// проблем документа рахувала їх як поточні.
    /// </remarks>
    public async Task<(List<CalcFinding> Findings, bool Complete)> IssueWithoutRunAsync(int limit, CancellationToken ct)
    {
        var orphans = await db.CalculationIssues.AsNoTracking()
            .Where(i => !db.CalculationRuns.Any(r => r.Id == i.CalculationRunId))
            .GroupBy(i => i.CalculationRunId)
            .OrderBy(g => g.Key)
            .Select(g => new { RunId = g.Key, DocumentId = g.Min(i => i.DocumentId), PeriodKey = g.Min(i => i.PeriodKey), Count = g.Count() })
            .Take(limit + 1)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var findings = orphans
            .Take(limit)
            .Select(o => new CalcFinding(
                IssueWithoutRunRule,
                Severity: 3,
                RunEntity,
                o.RunId,
                ConsistencyCheckJob.FindingText(
                    IssueWithoutRunRule,
                    ("runId", o.RunId),
                    ("documentId", o.DocumentId),
                    ("period", o.PeriodKey),
                    ("issues", o.Count)),
                o.PeriodKey))
            .ToList();

        return (findings, orphans.Count <= limit);
    }

    /// <summary>
    /// R8: вихід чинної сьогодні версії активної методології, яка прив'язана до живого шаблону, але саме цей вихід — ні.
    /// </summary>
    /// <remarks>
    /// Вага 1: число рахується й нікуди не потрапляє; дані не зіпсовані. Методологія без жодної прив'язки на живих
    /// версіях (бібліотека, ще не підключена) — поза правилом: її не рахує ніхто, і «вихід без прив'язки» там не новина.
    /// </remarks>
    public async Task<(List<CalcFinding> Findings, bool Complete)> OutputWithoutBindingAsync(int limit, CancellationToken ct)
    {
        var context = await ContextAsync(ct).ConfigureAwait(false);
        var today = DateOnly.FromDateTime(utcNow);

        var liveBindings = await (
                from b in db.CalculationBindings.AsNoTracking()
                join t in db.TableDefs.AsNoTracking() on b.TableDefId equals t.Id
                join s in db.SheetDefs.AsNoTracking() on t.SheetDefId equals s.Id
                where b.IsActive && ConsistencyCheckJob.LiveTemplateVersions(db).Contains(s.TemplateVersionId)
                select new { b.MethodologyId, b.OutputCode })
            .Distinct()
            .OrderBy(b => b.MethodologyId).ThenBy(b => b.OutputCode)
            .Take(MaxConfigRows)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var boundOutputs = liveBindings.Select(b => (b.MethodologyId, b.OutputCode)).ToHashSet();
        var boundMethodologies = liveBindings.Select(b => b.MethodologyId).ToHashSet();

        var effective = context.Methodologies.Values
            .Where(m => m.IsActive && boundMethodologies.Contains(m.Id))
            .Select(m => context.Effective(m.Id, today))
            .Where(v => v is not null)
            .ToDictionary(v => v!.Id, v => v!);
        var versionIds = effective.Keys.ToList();

        var outputs = await db.MethodologyOutputs.AsNoTracking()
            .Where(o => versionIds.Contains(o.MethodologyVersionId))
            .OrderBy(o => o.Id)
            .Select(o => new { o.Id, o.MethodologyVersionId, o.Code })
            .Take(MaxConfigRows)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var findings = outputs
            .Where(o => !boundOutputs.Contains((effective[o.MethodologyVersionId].MethodologyId, o.Code)))
            .Take(limit)
            .Select(o =>
            {
                var version = effective[o.MethodologyVersionId];
                return new CalcFinding(
                    OutputWithoutBindingRule,
                    Severity: 1,
                    OutputEntity,
                    o.Id,
                    ConsistencyCheckJob.FindingText(
                        OutputWithoutBindingRule,
                        ("methodology", context.Code(version.MethodologyId)),
                        ("methodologyId", version.MethodologyId),
                        ("version", version.Version),
                        ("output", o.Code)));
            })
            .ToList();

        return (findings, liveBindings.Count < MaxConfigRows && outputs.Count < MaxConfigRows && context.Complete);
    }

    /// <summary>
    /// R7: чинна прив'язка на ЖИВІЙ версії шаблону (проєкт не в архіві), чия колонка, таблиця чи аркуш видалені.
    /// </summary>
    /// <remarks>
    /// Лише живі версії: на live 24.11 775 таких прив'язок, і всі — на версіях, за якими не живе жоден проєкт (шум,
    /// а не сигнал). Вага 1: прогін бере прив'язку за <c>TableDefId</c> і пише результат у колонку, якої форма не
    /// показує.
    /// </remarks>
    public async Task<(List<CalcFinding> Findings, bool Complete)> BindingDeadColumnAsync(int limit, CancellationToken ct)
    {
        var context = await ContextAsync(ct).ConfigureAwait(false);

        var dead = await (
                from b in db.CalculationBindings.AsNoTracking()
                join c in db.ColumnDefs.AsNoTracking() on b.ColumnDefId equals c.Id
                join t in db.TableDefs.AsNoTracking() on b.TableDefId equals t.Id
                join s in db.SheetDefs.AsNoTracking() on t.SheetDefId equals s.Id
                where b.IsActive
                      && (c.IsDeleted || t.IsDeleted || s.IsDeleted)
                      && ConsistencyCheckJob.LiveTemplateVersions(db).Contains(s.TemplateVersionId)
                orderby b.Id
                select new { b.Id, b.MethodologyId, Table = t.Code, Column = c.Code, s.TemplateVersionId })
            .Take(limit + 1)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var findings = dead
            .Take(limit)
            .Select(d => new CalcFinding(
                BindingDeadColumnRule,
                Severity: 1,
                BindingEntity,
                d.Id,
                ConsistencyCheckJob.FindingText(
                    BindingDeadColumnRule,
                    ("methodology", context.Code(d.MethodologyId)),
                    ("methodologyId", d.MethodologyId),
                    ("table", d.Table),
                    ("column", d.Column),
                    ("version", d.TemplateVersionId),
                    ("bindingId", d.Id))))
            .ToList();

        return (findings, dead.Count <= limit);
    }

    /// <summary>
    /// R4: рядок прив'язаної таблиці, у якому заповнено ВСІ обов'язкові входи чинної версії методології, але
    /// актуальний прогін не дав для нього ні результату цієї методології, ні проблеми (тихий пропуск).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Виключено все, що прогін законно не рахує: затверджені аркуші (переносяться), рядки, відсіяні фільтром
    /// активності методології (D-425 — те саме <see cref="Ecr.Application.Ports.RowActivityFilterResolution.Admits"/>), прив'язки зі
    /// звуженим предикатом (не <c>{}</c>), таблиці, де прогін уперся в стелю проблем, методології без чинної версії або
    /// без обов'язкових входів на цій таблиці. Проблема рядка будь-якого виду (без правила, відхилено категорією,
    /// бракує аргументів, помилка кроку) — видима причина, а не мовчання.
    /// </para>
    /// <para>
    /// ⚠ Обов'язковий вхід зіставляється з колонкою таблиці документа за кодами таблиці й колонки: версія методології
    /// посилається на колонку ОДНІЄЇ версії шаблону, а документ може жити на іншій.
    /// </para>
    /// <para>
    /// Не більше <see cref="MaxRowFindingsPerDocumentPeriod"/> рядків на документ × період; решта — одна знахідка-підсумок
    /// на прогін (<see cref="RowInputsCompleteNoResultMoreKey"/>).
    /// </para>
    /// </remarks>
    public async Task<(List<CalcFinding> Findings, bool Complete)> RowInputsCompleteNoResultAsync(int limit, CancellationToken ct)
    {
        var context = await ContextAsync(ct).ConfigureAwait(false);
        var findings = new List<CalcFinding>();
        var complete = context.Complete;

        foreach (var run in context.Runs)
        {
            if (findings.Count >= limit)
            {
                complete = false;
                break;
            }

            var outcome = await RowGapsOfRunAsync(context, run, ct).ConfigureAwait(false);
            complete &= outcome.Complete;

            foreach (var document in outcome.Gaps.GroupBy(g => g.DocumentId).OrderBy(g => g.Key))
            {
                var rows = document.OrderBy(g => g.TableCode, StringComparer.Ordinal).ThenBy(g => g.Ordinal).ToList();
                findings.AddRange(rows.Take(MaxRowFindingsPerDocumentPeriod).Select(g => new CalcFinding(
                    RowInputsCompleteNoResultRule,
                    Severity: 2,
                    RowEntity,
                    g.RowId,
                    ConsistencyCheckJob.FindingText(
                        RowInputsCompleteNoResultRule,
                        ("documentId", g.DocumentId),
                        ("period", run.PeriodKey),
                        ("table", g.TableCode),
                        ("row", g.RowKey),
                        ("methodology", string.Join(", ", g.MethodologyIds.Select(context.Code).Order(StringComparer.Ordinal))),
                        ("methodologyId", g.MethodologyIds.Min()),
                        ("runId", run.Id)),
                    run.PeriodKey)));

                if (rows.Count > MaxRowFindingsPerDocumentPeriod)
                {
                    // ⚠ Сутність підсумку — прогін, а прогін документного перерахунку має один документ; проєктний прогін
                    // з кількома документами з надлишком дає один підсумок на перший такий документ (решта — у тексті прогону).
                    findings.Add(new CalcFinding(
                        RowInputsCompleteNoResultRule,
                        Severity: 2,
                        RunEntity,
                        run.Id,
                        ConsistencyCheckJob.FindingText(
                            RowInputsCompleteNoResultMoreKey,
                            ("documentId", document.Key),
                            ("period", run.PeriodKey),
                            ("more", rows.Count - MaxRowFindingsPerDocumentPeriod),
                            ("runId", run.Id)),
                        run.PeriodKey));
                }
            }
        }

        return (findings.Take(limit).ToList(), complete && findings.Count < limit);
    }

    /// <summary>Тихі пропуски одного актуального прогону (див. <see cref="RowInputsCompleteNoResultAsync"/>).</summary>
    private async Task<(List<RowGap> Gaps, bool Complete)> RowGapsOfRunAsync(Context context, RunRow run, CancellationToken ct)
    {
        var bound = (await BoundInstancesAsync(run, ct).ConfigureAwait(false))
            .Where(b => b.Unfiltered && b.HasRows && !b.Approved)
            .ToList();
        if (bound.Count == 0)
        {
            return ([], true);
        }

        // Чинна версія кожної методології на кінець періоду і її обов'язкові входи. ⚠ Вхід посилається на колонку ОДНІЄЇ
        // версії шаблону, а таблиця документа може жити на іншій: колонка перекладається у версію таблиці тим самим
        // `ColumnPathMapper`, що й у воротах запису (C1, `MethodologyKeyLocalizer`).
        var effective = bound.Select(b => b.MethodologyId).Distinct()
            .Select(m => context.Effective(m, run.PeriodEnd))
            .Where(v => v is not null)
            .ToDictionary(v => v!.MethodologyId, v => v!);
        var versionIds = effective.Values.Select(v => v.Id).ToList();
        var required = await db.MethodologyRequiredInputs.AsNoTracking()
            .Where(ri => versionIds.Contains(ri.MethodologyVersionId))
            .OrderBy(ri => ri.Id)
            .Select(ri => new { ri.MethodologyVersionId, ri.ColumnDefId })
            .Take(MaxConfigRows)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var requiredByVersion = required
            .GroupBy(r => r.MethodologyVersionId)
            .ToDictionary(g => g.Key, g => g.Select(r => r.ColumnDefId).Distinct().ToList());

        var tableIds = bound.Select(b => b.TableDefId).Distinct().ToList();
        var tables = await (
                from t in db.TableDefs.AsNoTracking()
                join s in db.SheetDefs.AsNoTracking() on t.SheetDefId equals s.Id
                where tableIds.Contains(t.Id)
                orderby t.Id
                select new { t.Id, t.Code, s.TemplateVersionId })
            .Take(MaxConfigRows)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var tableCode = tables.ToDictionary(t => t.Id, t => t.Code);
        var columnTable = (await db.ColumnDefs.AsNoTracking()
                .Where(c => tableIds.Contains(c.TableDefId) && !c.IsDeleted)
                .OrderBy(c => c.Id)
                .Select(c => new { c.Id, c.TableDefId })
                .Take(MaxConfigRows)
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .ToDictionary(c => c.Id, c => c.TableDefId);

        var allRequired = required.Select(r => r.ColumnDefId).Distinct().ToList();
        var mapper = new ColumnPathMapper(db);
        var maps = new Dictionary<int, IReadOnlyDictionary<int, int>>();
        foreach (var templateVersionId in tables.Select(t => t.TemplateVersionId).Distinct())
        {
            maps[templateVersionId] = allRequired.Count == 0
                ? new Dictionary<int, int>()
                : await mapper.MapToVersionAsync(allRequired, templateVersionId, ct).ConfigureAwait(false);
        }

        var versionOfTable = tables.ToDictionary(t => t.Id, t => t.TemplateVersionId);

        // Пара «таблиця × методологія» → колонки обов'язкових входів НА ЦІЙ таблиці; без таких входів пара не перевіряється.
        var pairs = new Dictionary<(int TableDefId, int MethodologyId), IReadOnlyList<int>>();
        foreach (var (tableDefId, methodologyId) in bound.Select(b => (b.TableDefId, b.MethodologyId)).Distinct())
        {
            if (!effective.TryGetValue(methodologyId, out var version)
                || !requiredByVersion.TryGetValue(version.Id, out var inputs)
                || !versionOfTable.TryGetValue(tableDefId, out var templateVersionId))
            {
                continue;
            }

            var map = maps[templateVersionId];
            var ids = inputs
                .Select(i => map.TryGetValue(i, out var local) ? local : 0)
                .Where(local => local != 0 && columnTable.TryGetValue(local, out var owner) && owner == tableDefId)
                .Distinct()
                .ToList();
            if (ids.Count > 0)
            {
                pairs[(tableDefId, methodologyId)] = ids;
            }
        }

        if (pairs.Count == 0)
        {
            return ([], true);
        }

        var filters = await new RowActivityFilterSource(db)
            .ResolveForMethodologiesAsync([.. pairs.Keys.Select(p => p.MethodologyId).Distinct()], tableIds, ct)
            .ConfigureAwait(false);

        var neededColumns = pairs.Values.SelectMany(c => c)
            .Concat(filters.Values.Where(f => f is not null).SelectMany(f => tableIds.SelectMany(t => f!.ColumnsOf(t))))
            .Distinct()
            .ToList();
        var instanceIds = bound.Where(b => pairs.ContainsKey((b.TableDefId, b.MethodologyId))).Select(b => b.InstanceId).Distinct().ToList();
        var period = run.PeriodKey;

        var rows = await db.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == period && instanceIds.Contains(r.TableInstanceId) && !r.IsDeleted)
            .OrderBy(r => r.Id)
            .Select(r => new { r.Id, r.TableInstanceId, r.RowKeyValue, r.Ordinal })
            .Take(MaxRowsPerDocumentPeriod)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var cells = await (
                from c in db.CellValues.AsNoTracking()
                join r in db.TableRows.AsNoTracking()
                    on new { c.PeriodKeyValue, Id = c.TableRowId } equals new { r.PeriodKeyValue, r.Id }
                where c.PeriodKeyValue == period && r.PeriodKeyValue == period
                      && instanceIds.Contains(r.TableInstanceId) && !r.IsDeleted
                      && neededColumns.Contains(c.ColumnDefId) && !c.IsEmpty
                select new { c.TableRowId, c.ColumnDefId, c.ValueRegistryEntryId })
            .OrderBy(c => c.TableRowId).ThenBy(c => c.ColumnDefId)
            .Take(MaxRowsPerDocumentPeriod)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var cellsByRow = cells
            .GroupBy(c => c.TableRowId)
            .ToDictionary(g => g.Key, g => g.GroupBy(c => c.ColumnDefId).ToDictionary(x => x.Key, x => x.First().ValueRegistryEntryId));

        var methodologyOfVersion = context.Versions;
        var results = await db.CalculationResults.AsNoTracking()
            .Where(x => x.CalculationRunId == run.Id && x.PeriodKey == period && x.TableInstanceId != null && x.SourceRowKey != null)
            .Select(x => new { InstanceId = x.TableInstanceId!.Value, RowKey = x.SourceRowKey!, x.MethodologyVersionId })
            .Distinct()
            .OrderBy(x => x.InstanceId).ThenBy(x => x.RowKey).ThenBy(x => x.MethodologyVersionId)
            .Take(MaxRowsPerDocumentPeriod)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var computed = results
            .Select(x => (x.InstanceId, x.RowKey, methodologyOfVersion.TryGetValue(x.MethodologyVersionId, out var v) ? v.MethodologyId : 0))
            .ToHashSet();

        var issues = await db.CalculationIssues.AsNoTracking()
            .Where(i => i.CalculationRunId == run.Id)
            .Select(i => new { i.Id, i.DocumentId, i.TableDefId, i.SheetCopyId, i.RowKey, i.MethodologyId })
            .OrderBy(i => i.Id)
            .Take(MaxRowsPerDocumentPeriod)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var issueRows = issues.Select(i => (i.DocumentId, i.TableDefId, i.SheetCopyId, i.RowKey)).ToHashSet();
        var cappedTables = issues
            .GroupBy(i => (i.DocumentId, i.TableDefId, i.SheetCopyId))
            .Where(g => g.Count() >= IssueCapPerTable)
            .Select(g => g.Key)
            .ToHashSet();

        var complete = rows.Count < MaxRowsPerDocumentPeriod && cells.Count < MaxRowsPerDocumentPeriod
                       && results.Count < MaxRowsPerDocumentPeriod && issues.Count < MaxRowsPerDocumentPeriod;

        var instances = bound.GroupBy(b => b.InstanceId).ToDictionary(g => g.Key, g => g.ToList());
        var gaps = new List<RowGap>();
        foreach (var row in rows)
        {
            var instance = instances[row.TableInstanceId];
            var first = instance[0];
            if (cappedTables.Contains((first.DocumentId, first.TableDefId, first.SheetCopyId))
                || issueRows.Contains((first.DocumentId, first.TableDefId, first.SheetCopyId, row.RowKeyValue)))
            {
                continue;
            }

            var values = cellsByRow.GetValueOrDefault(row.Id);
            var silentMethodologies = new List<int>();
            foreach (var binding in instance)
            {
                if (!pairs.TryGetValue((binding.TableDefId, binding.MethodologyId), out var inputs)
                    || values is null
                    || !inputs.All(values.ContainsKey)
                    || computed.Contains((row.TableInstanceId, row.RowKeyValue, binding.MethodologyId)))
                {
                    continue;
                }

                var filter = filters.GetValueOrDefault(binding.MethodologyId);
                if (filter is not null
                    && !filter.Admits(
                        binding.TableDefId,
                        column => values.TryGetValue(column, out var entry) && entry is { } id
                            ? id.ToString(CultureInfo.InvariantCulture)
                            : null))
                {
                    continue;
                }

                silentMethodologies.Add(binding.MethodologyId);
            }

            if (silentMethodologies.Count > 0)
            {
                gaps.Add(new RowGap(
                    first.DocumentId, row.Id, tableCode.GetValueOrDefault(first.TableDefId) ?? "—", row.RowKeyValue,
                    row.Ordinal, silentMethodologies));
            }
        }

        return (gaps, complete);
    }

    /// <summary>
    /// Екземпляри таблиць документів прогону за його період, до яких веде чинна прив'язка, — пари «екземпляр ×
    /// методологія» з ознаками: чи має екземпляр рядки, чи аркуш затверджено, чи всі прив'язки пари — на всю таблицю.
    /// </summary>
    private async Task<List<BoundInstance>> BoundInstancesAsync(RunRow run, CancellationToken ct)
    {
        var period = run.PeriodKey;
        var documents = run.DocumentId is { } documentId
            ? db.Documents.AsNoTracking().Where(d => d.Id == documentId).Select(d => d.Id)
            : db.Documents.AsNoTracking().Where(d => d.ProjectId == run.ProjectId).Select(d => d.Id);

        var pairs = await (
                from i in db.TableInstances.AsNoTracking()
                join b in db.CalculationBindings.AsNoTracking() on i.TableDefId equals b.TableDefId
                join t in db.TableDefs.AsNoTracking() on i.TableDefId equals t.Id
                where i.PeriodKeyValue == period && documents.Contains(i.DocumentId) && b.IsActive
                select new
                {
                    InstanceId = i.Id,
                    i.DocumentId,
                    i.TableDefId,
                    i.SheetCopyId,
                    t.SheetDefId,
                    b.MethodologyId,
                    Filtered = b.MatchJson != "{}",
                })
            .Distinct()
            .OrderBy(p => p.InstanceId).ThenBy(p => p.MethodologyId)
            .Take(MaxConfigRows)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // ⚠ Проєктний прогін не відповідає за документ, що має власний актуальний прогін того самого періоду: читання
        // результатів бере документний (`CurrentRunSelection`).
        var context = await ContextAsync(ct).ConfigureAwait(false);
        if (run.DocumentId is null)
        {
            pairs = [.. pairs.Where(p => !context.DocumentRuns.Contains((p.DocumentId, period)))];
        }

        var instanceIds = pairs.Select(p => p.InstanceId).Distinct().ToList();
        var withRows = instanceIds.Count == 0
            ? []
            : (await db.TableRows.AsNoTracking()
                    .Where(r => r.PeriodKeyValue == period && instanceIds.Contains(r.TableInstanceId) && !r.IsDeleted)
                    .Select(r => r.TableInstanceId)
                    .Distinct()
                    .OrderBy(id => id)
                    .Take(MaxConfigRows)
                    .ToListAsync(ct)
                    .ConfigureAwait(false))
                .ToHashSet();

        var documentIds = pairs.Select(p => p.DocumentId).Distinct().ToList();
        var approved = documentIds.Count == 0
            ? []
            : (await db.ApprovalStates.AsNoTracking()
                    .Where(s => documentIds.Contains(s.DocumentId) && s.PeriodKey == period && s.Status == DocumentStatus.Approved)
                    .Select(s => new { s.Id, s.DocumentId, s.SheetDefId, s.SheetCopyId })
                    .OrderBy(s => s.Id)
                    .Take(MaxConfigRows)
                    .ToListAsync(ct)
                    .ConfigureAwait(false))
                .Select(s => (s.DocumentId, s.SheetDefId, s.SheetCopyId))
                .ToHashSet();

        return [.. pairs
            .GroupBy(p => (p.InstanceId, p.DocumentId, p.TableDefId, p.SheetCopyId, p.SheetDefId, p.MethodologyId))
            .Select(g => new BoundInstance(
                g.Key.InstanceId,
                g.Key.DocumentId,
                g.Key.TableDefId,
                g.Key.SheetCopyId,
                g.Key.MethodologyId,
                Unfiltered: g.All(p => !p.Filtered),
                HasRows: withRows.Contains(g.Key.InstanceId),
                Approved: approved.Contains((g.Key.DocumentId, g.Key.SheetDefId, g.Key.SheetCopyId))))];
    }

    /// <summary>Документ × період, де хоч один аркуш оригіналу затверджено.</summary>
    private async Task<HashSet<(long DocumentId, int PeriodKey)>> ApprovedAsync(List<long> documentIds, CancellationToken ct)
    {
        if (documentIds.Count == 0)
        {
            return [];
        }

        var approved = await db.ApprovalStates.AsNoTracking()
            .Where(s => documentIds.Contains(s.DocumentId) && s.SheetCopyId == null && s.Status == DocumentStatus.Approved)
            .Select(s => new { s.DocumentId, s.PeriodKey })
            .Distinct()
            .OrderBy(s => s.DocumentId).ThenBy(s => s.PeriodKey)
            .Take(MaxConfigRows)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return [.. approved.Select(a => (a.DocumentId, a.PeriodKey))];
    }


    /// <summary>Спільне для правил читання: актуальні прогони, межі періодів, версії й методології — раз на прохід.</summary>
    private async Task<Context> ContextAsync(CancellationToken ct)
    {
        if (_context is not null)
        {
            return _context;
        }

        var runs = await (
                from r in db.CalculationRuns.AsNoTracking()
                from p in db.Periods.AsNoTracking()
                where r.Status == CalculationRun.CurrentStatus && r.PeriodKey != null
                      && p.ProjectId == r.ProjectId && p.PeriodKeyValue == r.PeriodKey
                orderby r.Id
                select new RunRow(r.Id, r.ProjectId, r.DocumentId, p.PeriodKeyValue, p.PeriodEnd))
            .Take(MaxConfigRows)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var projectIds = runs.Select(r => r.ProjectId).Distinct().ToList();
        var periods = await db.Periods.AsNoTracking()
            .Where(p => projectIds.Contains(p.ProjectId))
            .Select(p => new { p.ProjectId, p.PeriodKeyValue, p.PeriodEnd })
            .OrderBy(p => p.ProjectId).ThenBy(p => p.PeriodKeyValue)
            .Take(MaxConfigRows)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var versions = await db.MethodologyVersions.AsNoTracking()
            .OrderBy(v => v.Id)
            .Select(v => new VersionRow(v.Id, v.MethodologyId, v.Version, v.EffectiveFrom, v.Status, v.Engine))
            .Take(MaxConfigRows)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var methodologies = await db.Methodologies.AsNoTracking()
            .OrderBy(m => m.Id)
            .Select(m => new MethodologyRow(m.Id, m.Code, m.IsActive))
            .Take(MaxConfigRows)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        _context = new Context(
            runs,
            periods.GroupBy(p => (p.ProjectId, p.PeriodKeyValue)).ToDictionary(g => g.Key, g => g.First().PeriodEnd),
            versions.ToDictionary(v => v.Id),
            methodologies.ToDictionary(m => m.Id),
            runs.Count < MaxConfigRows && periods.Count < MaxConfigRows && versions.Count < MaxConfigRows
            && methodologies.Count < MaxConfigRows);

        return _context;
    }

    /// <summary>Знімок конфігурації одного проходу.</summary>
    private sealed class Context(
        List<RunRow> runs,
        Dictionary<(int ProjectId, int PeriodKey), DateOnly> periodEnds,
        Dictionary<int, VersionRow> versions,
        Dictionary<int, MethodologyRow> methodologies,
        bool complete)
    {
        private readonly ILookup<int, VersionRow> _published = versions.Values
            .Where(v => v.Status == TemplateVersionStatus.Published && v.EffectiveFrom is not null)
            .ToLookup(v => v.MethodologyId);

        public List<RunRow> Runs { get; } = runs;

        public Dictionary<(int ProjectId, int PeriodKey), DateOnly> PeriodEnds { get; } = periodEnds;

        public Dictionary<int, VersionRow> Versions { get; } = versions;

        public Dictionary<int, MethodologyRow> Methodologies { get; } = methodologies;

        public bool Complete { get; } = complete;

        /// <summary>Документ × період, що має власний (документний) актуальний прогін.</summary>
        public HashSet<(long DocumentId, int PeriodKey)> DocumentRuns { get; } =
            [.. runs.Where(r => r.DocumentId is not null).Select(r => (r.DocumentId!.Value, r.PeriodKey))];

        /// <summary>Чинна на дату опублікована версія — правило оркестратора; <c>null</c> — на дату не діє жодна.</summary>
        public VersionRow? Effective(int methodologyId, DateOnly onDate)
            => _published[methodologyId]
                .Where(v => v.EffectiveFrom <= onDate)
                .OrderByDescending(v => new MethodologyVersionKey(v.EffectiveFrom, v.Version, v.Id), MethodologyVersionKey.Currency)
                .FirstOrDefault();

        public string Code(int methodologyId)
            => Methodologies.TryGetValue(methodologyId, out var m) ? m.Code : methodologyId.ToString(CultureInfo.InvariantCulture);
    }

    private sealed record RunRow(long Id, int ProjectId, long? DocumentId, int PeriodKey, DateOnly PeriodEnd);

    private sealed record VersionRow(
        int Id, int MethodologyId, string Version, DateOnly? EffectiveFrom, TemplateVersionStatus Status, MethodologyEngine Engine);

    private sealed record MethodologyRow(int Id, string Code, bool IsActive);

    private sealed record BoundInstance(
        long InstanceId, long DocumentId, int TableDefId, long? SheetCopyId, int MethodologyId, bool Unfiltered, bool HasRows, bool Approved);

    private sealed record RowGap(
        long DocumentId, long RowId, string TableCode, string RowKey, int Ordinal, IReadOnlyList<int> MethodologyIds);
}

/// <summary>Знахідка правила розрахунку (див. <see cref="CalculationConsistencyChecks"/>).</summary>
/// <param name="RuleCode">Код правила.</param>
/// <param name="Severity">Вага: 1 інформація, 2 попередження, 3 помилка.</param>
/// <param name="EntityType">Тип сутності.</param>
/// <param name="EntityId">Ідентифікатор сутності.</param>
/// <param name="Message">Конверт тексту.</param>
/// <param name="PeriodKey">Період; <c>null</c> — знахідка конфігурації.</param>
internal sealed record CalcFinding(
    string RuleCode, byte Severity, string EntityType, long EntityId, string Message, int? PeriodKey = null);

/// <summary>Підсумок одного правила: знахідки, повнота (D-480) і час.</summary>
/// <param name="RuleCode">Код правила.</param>
/// <param name="Findings">Знахідки.</param>
/// <param name="Complete">Правило перевірено повністю — його старі знахідки поза цим проходом можна видалити.</param>
/// <param name="ElapsedMs">Час правила, мс.</param>
internal sealed record CalcCheckOutcome(string RuleCode, List<CalcFinding> Findings, bool Complete, long ElapsedMs);
