// src/Ecr.Infrastructure/Jobs/SourceCascadePendingGate.cs
using System.Text.Json;
using Ecr.Application.Calculations;
using Ecr.Application.DocumentDependencies;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Binding;
using Ecr.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// F5 (рішення людини «Заборонити, поки йде перерахунок»): реалізація <see cref="ISourceCascadePendingGate"/> над чергою
/// в базі (<c>itg.JobProgress</c>).
/// </summary>
/// <remarks>
/// <para>
/// Один запит: живі (<c>Queued</c> — і відкладені, і запарковані; <c>Running</c>) рядки документа за фільтрованим
/// <c>IX_JobProgress_Document_Active</c> з ціллю рівно каскаду цього року (<c>doc{id}-y{рік}-dependents</c>,
/// <c>D-566</c>) чи цього періоду (<c>doc{id}-p{період}-dependents</c> — черга без об'єднання, довідник RT-25, повернення
/// покритої задачі). Документ без живої задачі каскаду — порожній результат пошуком за індексом. «З затвердженими»
/// (<c>-dependents-approved</c>) не блокує: вона й так переписує затверджені аркуші.
/// </para>
/// <para>
/// Чи період ще в плані задачі року — <see cref="Pends"/>.
/// </para>
/// <para>
/// ✎ F5 (огляд b9, п. 1): і живий прохід читача періоду (ціль <c>doc{id}-p{період}</c>, <see cref="IsReaderPass"/>) — крок
/// каскаду й прогін власника ставлять місяцям, що читають його числа (<c>row.X[m]</c>, не-місячна колонка якоря), окремі
/// задачі поза <c>chainPlan</c>: без цього після останнього кроку ланцюжка місяць-читач подавався б зі старим числом.
/// Той самий запит (третя ціль у тому ж пошуку за індексом документа).
/// </para>
/// <para>
/// ✎ F5 (огляд координатора, п. 1): і вікно ДО постановки каскаду. Місяць споживача чекає й тоді, коли жива задача
/// ланцюжка, що до каскаду ПРИВЕДЕ: задача формул (<c>doc{id}-formula…</c>) чи власний перерахунок (<c>doc{id}-p{період}-auto</c>)
/// документа-джерела зв'язки (стан <c>Any</c>, біт <c>RecalculateDependents</c> і читання з документа —
/// <see cref="SourceBindingGateCache"/>, той самий знімок, що в хуку джерела) або задача впливу зв'язки
/// <c>source-change-b{зв'язка}-y{рік}[-m{місяць}]</c> (будь-який стан зв'язки, і <c>ApprovedOnly</c> — її ставить затвердження
/// джерела) — коли шаблон документа є споживачем зв'язки в графі й обсяг ребра (<c>PeriodScope</c>) доводить зміну до
/// періоду (<see cref="Affects"/>). Кожна ланка ставить наступну ДО того, як сама залишить <c>Running</c>, тож читання
/// одним оператором (RCSI) бачить ланцюжок без шпарини.
/// </para>
/// <para>
/// Ціна: граф без жодного ребра «джерело → споживач» — той самий один запит, що доти (0 зайвих). Інакше — ТОЙ САМИЙ
/// один запит, розширений: шаблон документа за період і живі задачі ланцюжків джерел (фільтровані
/// <c>UX_JobProgress_Target_Queued</c>/<c>_Running</c> — лише живі рядки черги). Шаблон-не-споживач — нуль рядків-ланок
/// у рішенні, звернень стільки ж.
/// </para>
/// <para>
/// ✎ b10 (D-567 (4)): Quartz у пам'яті (<c>D-563</c> дозволяє його малим інсталяціям; live — Quartz/InProcess) пише
/// дзеркало прогресу без цілі й payload, але з <c>DocumentId</c> і <c>JobId = {тип}~{ціль}~{guid}</c> — ціль береться з
/// <c>JobId</c>, і ворота бачать ті самі задачі, що в черзі в базі. Без payload жива задача Quartz на ціль місяця чекає
/// завжди (прохід читача від натискання не відрізнити), задача формул джерела — увесь рік.
/// ⚠ Затвердження джерела за <c>ApprovedOnly</c> ставить задачу впливу ПІСЛЯ свого коміту (<c>SourceApprovalChangeTrigger</c>)
/// — між комітом і постановкою (мілісекунди) ворота її не бачать.
/// </para>
/// </remarks>
/// <param name="db">Контекст (запит іде в його підключенні й транзакції обробника).</param>
/// <param name="gateCache">Знімок активних зв'язок хука джерела; <c>null</c> — ланок джерел не перевіряти (тести).</param>
/// <param name="graph">Граф залежностей (споживачі зв'язок); <c>null</c> — ланок джерел не перевіряти (тести).</param>
public sealed class SourceCascadePendingGate(
    EcrDbContext db, SourceBindingGateCache? gateCache = null, IDependencyGraphProvider? graph = null) : ISourceCascadePendingGate
{
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    // ✎ D-567 (4), b10: дзеркало Quartz у пам'яті (D-563, малі інсталяції; live — Quartz/InProcess) — рядок прогресу без
    // TargetKey, Lane і Payload, а JobId = `{тип}~{ціль}~{guid N}` (QuartzJobScheduler.TargetPrefixOf). Ціль такого рядка —
    // JobId без хвоста `~{guid}`: та сама форма, що TargetKey черги в базі. Рядок черги в базі — як доти (TargetKey).
    private const string EffectiveTarget = """
          CROSS APPLY (SELECT COALESCE(q.TargetKey,
                              CASE WHEN q.Lane IS NULL AND LEN(q.JobId) > 34 AND SUBSTRING(q.JobId, LEN(q.JobId) - 32, 1) = N'~'
                                   THEN LEFT(q.JobId, LEN(q.JobId) - 33) END) AS Tk) AS e
        """;

    // `Q` — рядок Quartz: payload немає, тож він чекає завжди (і прохід читача, і натискання «Перерахувати» місяця —
    // різниці без payload не видно; сумнів — у бік «чекати», D-567 (3)).
    private const string CascadeSql = """
        /* ecr:f5-cascade-gate */
        SELECT CASE WHEN q.TargetKey IS NULL THEN N'Q' ELSE N'C' END AS Kind, e.Tk AS TargetKey, q.[State] AS State,
               q.Payload AS Payload,
               CAST(NULL AS int) AS ConsumerTemplateId, CAST(NULL AS int) AS ConsumerPeriodKind,
               CAST(NULL AS int) AS SourceTemplateId, CAST(NULL AS int) AS SourcePeriodKind
          FROM itg.JobProgress AS q WITH (INDEX(IX_JobProgress_Document_Active))
        """ + "\n" + EffectiveTarget + "\n" + """
         WHERE q.DocumentId = @doc AND q.DocumentId IS NOT NULL AND q.[State] IN ('Queued', 'Running')
           AND e.Tk IN (@yearTarget, @periodTarget, @readerTarget)
        """;

    // ⛔ Огляд b9: ціль у черзі в базі — `{тип задачі}~{ціль}` (DbBackgroundJobScheduler.TargetKeyOf). Доти ворота порівнювали
    // голі цілі (`doc…`, `source-change-b…`) і в Production не бачили жодної задачі — лише рядки, вставлені тестами руками.
    private static readonly string AutoLike = DbBackgroundJobScheduler.TargetKeyOf<IRecalculationJob>("doc") + "%-auto";
    private static readonly string FormulaLike = DbBackgroundJobScheduler.TargetKeyOf<IFormulaRecalculationJob>("doc") + "%-formula%";
    private static readonly string SourceChangeLike =
        DbBackgroundJobScheduler.TargetKeyOf<IRegistryImpactRecalculationJob>("source-change-b") + "%";

    // Ланки джерел — лише живі рядки (фільтровані індекси цілі); документні — з шаблоном і періодичністю документа-джерела.
    // ✎ D-567 (4), b10: `{2}` — відбір рядків: черга в базі (TargetKey, фільтровані індекси цілі) чи дзеркало Quartz
    // (TargetKey/Lane NULL, ціль — з JobId, `EffectiveTarget`). Період документної ланки — з payload; у Quartz payload
    // немає: `-auto` несе період у цілі (`doc{id}-p{yyyyMM}-auto`), задача формул — невідомий (увесь рік, як доти), а шаблон
    // документа-джерела тоді — за періодом споживача (той самий рік).
    private static readonly string UpstreamSql = """
        SELECT N'U', e.Tk, q.[State], CAST(k.Pk AS nvarchar(20)),
               @t, CAST(@kind AS int), COALESCE(sd.TemplateId, sv.TemplateId), CAST(sp.PeriodKind AS int)
          FROM itg.JobProgress AS q WITH (INDEX({0}))
        {effective}
          CROSS APPLY (SELECT CASE WHEN (e.Tk LIKE N'{auto}' OR e.Tk LIKE N'{formula}') AND ISJSON(q.Payload) = 1
                                   THEN TRY_CAST(JSON_VALUE(q.Payload, '$.periodKey') AS int)
                                   WHEN e.Tk LIKE N'{auto}' AND q.Payload IS NULL AND CHARINDEX(N'-p', e.Tk) > 0
                                   THEN TRY_CAST(SUBSTRING(e.Tk, CHARINDEX(N'-p', e.Tk) + 2, 6) AS int) END AS Pk) AS k
          LEFT JOIN doc.Document AS sd ON (e.Tk LIKE N'{auto}' OR e.Tk LIKE N'{formula}') AND sd.Id = q.DocumentId
          LEFT JOIN doc.Project AS sp ON sp.Id = sd.ProjectId
          LEFT JOIN cfg.TemplateVersion AS sv ON sv.Id = CASE WHEN sd.TemplateId IS NULL THEN doc.fn_VersionOf(sd.ProjectId, COALESCE(k.Pk, @period)) ELSE doc.fn_TemplateVersionOf(sd.ProjectId, sd.TemplateId, COALESCE(k.Pk, @period)) END
         WHERE {2} AND @t IS NOT NULL
           AND (e.Tk LIKE N'{source}' OR e.Tk LIKE N'{auto}' OR e.Tk LIKE N'{formula}')
        """
        .Replace("{effective}", EffectiveTarget, StringComparison.Ordinal)
        .Replace("{auto}", AutoLike, StringComparison.Ordinal)
        .Replace("{formula}", FormulaLike, StringComparison.Ordinal)
        .Replace("{source}", SourceChangeLike, StringComparison.Ordinal);

    private static string UpstreamPart(string index, string where)
        => UpstreamSql.Replace("{0}", index, StringComparison.Ordinal).Replace("{2}", where, StringComparison.Ordinal);

    private static readonly string ExtendedSql =
        """
        /* ecr:f5-cascade-gate */
        DECLARE @t int = NULL, @kind tinyint = NULL;
        SELECT @t = COALESCE(d.TemplateId, v.TemplateId), @kind = p.PeriodKind
          FROM doc.Document AS d
          JOIN doc.Project AS p ON p.Id = d.ProjectId
          LEFT JOIN cfg.TemplateVersion AS v ON v.Id = CASE WHEN d.TemplateId IS NULL THEN doc.fn_VersionOf(d.ProjectId, @period) ELSE doc.fn_TemplateVersionOf(d.ProjectId, d.TemplateId, @period) END
         WHERE d.Id = @doc;
        """
        + "\n" + CascadeSql + "\nUNION ALL\n"
        + UpstreamPart("UX_JobProgress_Target_Queued", "q.[State] = 'Queued' AND q.TargetKey IS NOT NULL")
        + "\nUNION ALL\n"
        + UpstreamPart("UX_JobProgress_Target_Running", "q.[State] = 'Running' AND q.TargetKey IS NOT NULL")
        + "\nUNION ALL\n"
        // Дзеркало Quartz: живі рядки без цілі й лейна — пошук за (State, UpdatedAt), живих рядків одиниці.
        + UpstreamPart(
            "IX_JobProgress_State_UpdatedAt",
            "q.[State] IN ('Queued', 'Running') AND q.TargetKey IS NULL AND q.Lane IS NULL AND e.Tk IS NOT NULL")
        + "\nUNION ALL\nSELECT N'T', NULL, NULL, NULL, @t, CAST(@kind AS int), NULL, NULL;";

    /// <inheritdoc />
    public async Task<bool> IsPendingAsync(long documentId, int periodKey, CancellationToken ct)
    {
        var yearTarget = QueueKey(CalculationTrigger.SourceCascadeTargetOf(documentId, periodKey / 100));
        var periodTarget = QueueKey(CalculationTrigger.DependentsTargetOf(documentId, new PeriodKey(periodKey)));
        var readerTarget = QueueKey(Ecr.Application.Documents.RecalculateDocumentHandler.TargetOf(documentId, new PeriodKey(periodKey)));

        // Ребра «джерело → споживач» графа (будь-який стан зв'язки) і зв'язки, чиї документні ланки ведуть до каскаду.
        var upstream = await UpstreamAsync(ct).ConfigureAwait(false);

        var rows = await db.Database
            .SqlQueryRaw<GateRow>(
                upstream is null ? CascadeSql : ExtendedSql,
                new SqlParameter("@doc", documentId),
                new SqlParameter("@period", periodKey),
                new SqlParameter("@yearTarget", yearTarget),
                new SqlParameter("@periodTarget", periodTarget),
                new SqlParameter("@readerTarget", readerTarget))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // ✎ D-567 (4), b10: жива задача Quartz на ціль каскаду чи проходу місяця — чекає (payload немає, D-567 (3)).
        if (rows.Exists(r => r.Kind == "Q"))
        {
            return true;
        }

        if (rows.Exists(r => r.Kind == "C"
                             && (r.TargetKey == periodTarget
                                 || (r.TargetKey == readerTarget
                                     ? IsReaderPass(r.Payload)
                                     : Pends(r.State == "Running", r.Payload, periodKey)))))
        {
            return true;
        }

        if (upstream is null || rows.Find(r => r.Kind == "T") is not { ConsumerTemplateId: { } consumer } self)
        {
            return false;
        }

        var consumerKind = (PeriodKind)(self.ConsumerPeriodKind ?? 0);
        return rows.Exists(r => r.Kind == "U" && UpstreamPends(upstream, r, consumer, consumerKind, periodKey));
    }

    /// <summary>Ціль задачі перерахунку так, як її пише черга в базі (<c>IRecalculationJob~{ціль}</c>).</summary>
    private static string QueueKey(string target) => DbBackgroundJobScheduler.TargetKeyOf<IRecalculationJob>(target);

    /// <summary>Ціль без префікса типу задачі (<c>{тип}~</c>).</summary>
    private static string BareTarget(string queueKey)
        => queueKey.IndexOf('~', StringComparison.Ordinal) is var at and >= 0 ? queueKey[(at + 1)..] : queueKey;

    /// <summary>Ланки джерел для рішення; <c>null</c> — у графі жодного ребра «джерело → споживач» (запит як доти).</summary>
    private async Task<Upstream?> UpstreamAsync(CancellationToken ct)
    {
        if (graph is null || (gateCache is null && AutoBindingsProbe is null))
        {
            return null;
        }

        var snapshot = await graph.GetAsync(ct).ConfigureAwait(false);
        if (snapshot.TemplateDependencies.Count == 0)
        {
            return null;
        }

        IReadOnlyCollection<int> auto = AutoBindingsProbe is not null
            ? await AutoBindingsProbe(ct).ConfigureAwait(false)
            : [.. (await gateCache!.GetAsync(db, ct).ConfigureAwait(false)).Bindings.Where(b => b.ReadFromDocument).Select(b => b.Id)];
        return new Upstream(snapshot.TemplateDependencies, [.. auto]);
    }

    /// <summary>Тести: зв'язки, документні ланки яких ведуть до каскаду (замість знімка <see cref="SourceBindingGateCache"/>).</summary>
    internal Func<CancellationToken, Task<IReadOnlyCollection<int>>>? AutoBindingsProbe { get; init; }

    /// <summary>Чи жива ланка джерела <paramref name="row"/> доведе каскад до періоду споживача.</summary>
    private static bool UpstreamPends(
        Upstream upstream, GateRow row, int consumerTemplateId, PeriodKind consumerKind, int periodKey)
    {
        var edges = upstream.Edges.Where(e => e.ConsumerTemplateId == consumerTemplateId).ToList();
        if (edges.Count == 0 || row.TargetKey is null)
        {
            return false;
        }

        if (SourceChangeKey.TryParse(BareTarget(row.TargetKey), out var bindingId, out var year, out var month, out var approved))
        {
            // «З затвердженими» перераховує й затверджені аркуші — затвердження не закріпить старих чисел.
            return !approved
                   && ScopeOf(edges.Where(e => e.BindingId == bindingId)) is { } scope
                   && Affects(scope, year, month, periodKey, consumerKind);
        }

        // Документна ланка (задача формул чи -auto) документа-джерела зв'язки, що ставить каскад з -auto (стан Any).
        if (row.SourceTemplateId is not { } sourceTemplate)
        {
            return false;
        }

        var bySource = edges.Where(e => e.SourceTemplateId == sourceTemplate && upstream.AutoBindings.Contains(e.BindingId));
        if (ScopeOf(bySource) is not { } sourceScope)
        {
            return false;
        }

        // Період джерела з payload; задача формул несе комірки кількох періодів — місяць невідомий (увесь рік).
        int? sourcePeriod = int.TryParse(row.Payload, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var p)
            ? p
            : null;
        var isAuto = row.TargetKey.EndsWith("-auto", StringComparison.Ordinal);
        int? sourceMonth = isAuto && sourcePeriod is { } sp && row.SourcePeriodKind == (int)PeriodKind.Monthly ? sp % 100 : null;
        var sourceYear = sourcePeriod is { } sy ? sy / 100 : periodKey / 100;
        return Affects(sourceScope, sourceYear, sourceMonth, periodKey, consumerKind);
    }

    private static PeriodScope? ScopeOf(IEnumerable<TemplateDependency> edges)
    {
        PeriodScope? scope = null;
        foreach (var e in edges)
        {
            scope = scope is { } s ? s.Join(e.Scope) : e.Scope;
        }

        return scope;
    }

    /// <summary>
    /// Чи зміна джерела (рік <paramref name="sourceYear"/>, місяць <paramref name="sourceMonth"/>; <c>null</c> — увесь рік)
    /// дійде каскадом до періоду споживача — те саме відображення, що в <c>RegistryImpactRecalculationJob</c>
    /// (<c>Month</c> — період-власник місяця, <c>Year</c> — увесь рік з маскою літеральних місяців, <c>All</c> — усі роки),
    /// плюс ПІЗНІШІ періоди року при ребрі «місяць» (каскад дописує читачів обчислених чисел, D-566 п. 2 — консервативно).
    /// </summary>
    internal static bool Affects(
        PeriodScope scope, int sourceYear, int? sourceMonth, int periodKey, PeriodKind consumerKind)
    {
        if (scope.Kind == PeriodScopeKind.All)
        {
            return true;
        }

        if (sourceYear != periodKey / 100)
        {
            return false;
        }

        if (scope.Kind == PeriodScopeKind.Year)
        {
            return scope.MonthMask == 0 || sourceMonth is not { } m || (scope.MonthMask & (1 << m)) != 0;
        }

        return sourceMonth is not { } month
               || DocumentSourceGate.OwnedMonths(consumerKind, periodKey % 100).Any(owned => owned >= month);
    }

    /// <summary>Ребра графа й зв'язки, документні ланки яких ведуть до каскаду.</summary>
    private sealed record Upstream(
        IReadOnlyList<TemplateDependency> Edges, HashSet<int> AutoBindings);

    /// <summary>
    /// Чи задача каскаду року (payload <see cref="RecalculationRequest"/>) ще має порахувати період <paramref name="periodKey"/>.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Перший крок ще не завершився (<c>chainAfter</c> = <c>null</c>, план не записано): період у
    /// <c>cascadePeriods</c>/<c>cascadeAppend</c> — так.</item>
    /// <item>Після першого кроку — період у решті плану <c>chainPlan</c> (з кроком, що виконується) чи в <c>cascadeAppend</c>.
    /// Крок, що завершився, знімає свій період із <c>chainPlan</c> продовження — і відмова зникає. (<c>cascadePeriods</c>
    /// продовження несе далі незмінними — після першого кроку вони не ознака.)</item>
    /// <item>План ще не ухвалено чи до ухваленого злилися нові періоди (<c>cascadeAppend</c>): план допише ПІЗНІШІ місяці,
    /// що читають обчислені числа (<c>CrossReadsAsync</c>, <c>D-566</c> п. 2), — тому кожен період, пізніший за найменший
    /// із ще не спланованих (<c>cascadePeriods</c>/<c>cascadeAppend</c>/<c>cascadeSeeds</c>), теж чекає. Сумнів — у бік
    /// «чекати»: це секунди вікна злиття й черги, а хибне «можна» закріпило б застарілі числа.</item>
    /// <item>Крок, що виконується (<c>Running</c>), може дописати в план пізніших читачів своїх змінених чисел — поки він
    /// іде, кожен період, пізніший за його (перший у <c>chainPlan</c>), теж чекає.</item>
    /// <item>Старий payload ланцюжка без плану — усі періоди після <c>chainAfter</c>.</item>
    /// <item>Payload не читається — чекає (консервативно).</item>
    /// </list>
    /// </remarks>
    /// <param name="running">Рядок у стані <c>Running</c> (крок виконується).</param>
    /// <param name="payload">Тіло задачі.</param>
    /// <param name="periodKey">Період споживача.</param>
    internal static bool Pends(bool running, string? payload, int periodKey)
    {
        RecalculationRequest? request;
        try
        {
            request = payload is null ? null : JsonSerializer.Deserialize<RecalculationRequest>(payload, PayloadOptions);
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request is null)
        {
            return true;
        }

        if (request.ChainAfter is not { } after)
        {
            // Перший крок ще не завершився: план не записано. Задача року без періодів каскаду — не наша форма: увесь рік.
            if (request.CascadePeriods is null)
            {
                return true;
            }

            return Has(request.CascadePeriods, periodKey)
                   || Has(request.CascadeAppend, periodKey)
                   || periodKey > Floor(request.CascadePeriods, request.CascadeAppend, request.CascadeSeeds);
        }

        // ⚠ Продовження ланцюжка несе ПЕРШІ cascadePeriods далі (`request with { … }`) — після першого кроку вони вже не
        // ознака роботи; чинні решта плану (chainPlan) і злиті після нього періоди (cascadeAppend).
        if (Has(request.ChainPlan, periodKey) || Has(request.CascadeAppend, periodKey))
        {
            return true;
        }

        if (request.CascadeAppend is not null)
        {
            return periodKey > Floor(request.CascadeAppend, request.CascadeSeeds);
        }

        // Крок, що ВИКОНУЄТЬСЯ (перший у chainPlan), дописує в план пізніших читачів своїх змінених чисел
        // (`ExtendWithLaterReadersAsync`) — поки він іде, пізніші за нього теж чекають.
        if (running && request.ChainPlan is { Length: > 0 } plan && periodKey > plan.Min())
        {
            return true;
        }

        return request.ChainPlan is null && periodKey > after;
    }

    /// <summary>
    /// ✎ F5 (огляд b9, п. 1): чи задача цілі <c>doc{id}-p{період}</c> — прохід читача (<c>foreignOwnerPass</c>, його ставить
    /// крок каскаду й прогін власника ПОЗА планом ланцюжка — <c>EnqueueOwnerPeriodReadersAsync</c>) чи натискання, у яке
    /// прохід злився (<c>readerPassMerged</c>, <see cref="DbJobQueue"/>). Звичайне натискання «Перерахувати» місяця не
    /// блокує; payload, що не читається, — теж (каскад такої форми не ставить).
    /// </summary>
    /// <param name="payload">Тіло задачі.</param>
    internal static bool IsReaderPass(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return false;
        }

        try
        {
            using var json = JsonDocument.Parse(payload);
            return json.RootElement.ValueKind == JsonValueKind.Object
                   && (Flag(json.RootElement, "foreignOwnerPass") || Flag(json.RootElement, ReaderPassMergedField));
        }
        catch (JsonException)
        {
            return false;
        }

        static bool Flag(JsonElement root, string name)
            => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    }

    /// <summary>
    /// Позначка payload натискання, у яке злився прохід читача (<see cref="DbJobQueue"/>, злиття постановок і поглинання
    /// при поверненні в чергу): воріт вона тримає місяць, поки задача жива.
    /// </summary>
    internal const string ReaderPassMergedField = "readerPassMerged";

    private static int Floor(params int[]?[] sets)
        => sets.SelectMany(s => s ?? []).DefaultIfEmpty(int.MaxValue).Min();

    private static bool Has(int[]? periods, int periodKey) => periods is not null && Array.IndexOf(periods, periodKey) >= 0;

    /// <summary>Рядок відповіді воріт.</summary>
    /// <param name="Kind"><c>C</c> — задача каскаду документа; <c>U</c> — ланка джерела; <c>T</c> — шаблон документа.</param>
    /// <param name="TargetKey">Ціль.</param>
    /// <param name="State">Стан (<c>Queued</c>/<c>Running</c>).</param>
    /// <param name="Payload">Тіло задачі (<c>C</c>) чи період документа-джерела з payload (<c>U</c>).</param>
    /// <param name="ConsumerTemplateId">Шаблон документа за період (<c>T</c>).</param>
    /// <param name="ConsumerPeriodKind">Періодичність проєкту документа (<c>T</c>).</param>
    /// <param name="SourceTemplateId">Шаблон документа ланки (<c>U</c>, документні ланки).</param>
    /// <param name="SourcePeriodKind">Періодичність проєкту документа ланки.</param>
    private sealed record GateRow(
        string Kind, string? TargetKey, string? State, string? Payload, int? ConsumerTemplateId, int? ConsumerPeriodKind,
        int? SourceTemplateId, int? SourcePeriodKind);
}

/// <summary>Розбір цілі задачі впливу зв'язки <c>source-change-b{зв'язка}-y{рік}[-m{місяць}][-approved]</c>.</summary>
internal static partial class SourceChangeKey
{
    [System.Text.RegularExpressions.GeneratedRegex(@"^source-change-b(\d+)-y(\d{4})(?:-m(\d{1,2}))?(-approved)?\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex Pattern();

    /// <summary>Розбирає ціль; <c>false</c> — не задача впливу зв'язки.</summary>
    public static bool TryParse(string target, out int bindingId, out int year, out int? month, out bool approved)
    {
        var match = Pattern().Match(target);
        bindingId = 0;
        year = 0;
        month = null;
        approved = false;
        if (!match.Success)
        {
            return false;
        }

        bindingId = int.Parse(match.Groups[1].ValueSpan, System.Globalization.CultureInfo.InvariantCulture);
        year = int.Parse(match.Groups[2].ValueSpan, System.Globalization.CultureInfo.InvariantCulture);
        month = match.Groups[3].Success ? int.Parse(match.Groups[3].ValueSpan, System.Globalization.CultureInfo.InvariantCulture) : null;
        approved = match.Groups[4].Success;
        return true;
    }
}
