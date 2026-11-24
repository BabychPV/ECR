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
/// ⛔ ✎ Огляд b11 (HIGH-1/2, рішення координатора 24.11): ворота тримають УВЕСЬ рік документа, поки жива будь-яка задача,
/// що ще може змінити його числа: каскад року (<c>doc{id}-y{рік}-dependents</c>), залежні будь-якого місяця року
/// (<c>doc{id}-p{рік}MM-dependents</c>), прохід читача будь-якого місяця (<see cref="IsReaderPass"/>; у Quartz — будь-яка задача
/// цілі місяця), системна задача формул документа (<c>doc{id}-formula</c> — читачі <c>CrossPeriodFanOut</c>: пізніші місяці
/// й ЯКІР року з річними підсумками) і ланка джерела, що доводить зміну до цього року. Доти ворота відпускали раніші місяці й
/// завершені кроки ланцюжка, а прогін власника місяця читачами будив і раніші періоди (якір, <c>[Period:#k]</c>) — затверджений
/// якір закріплював старий річний підсумок. Будити звужено (PERF-06b) — тримати консервативно.
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
    // ✎ D-567 (4), b10: дзеркало Quartz у пам'яті (D-563, малі інсталяції; live — Quartz/InProcess) — рядок прогресу без
    // TargetKey, Lane і Payload, а JobId = `{тип}~{ціль}~{guid N}` (QuartzJobScheduler.TargetPrefixOf). Ціль такого рядка —
    // JobId без хвоста `~{guid}`: та сама форма, що TargetKey черги в базі. Рядок черги в базі — як доти (TargetKey).
    private const string EffectiveTarget = """
          CROSS APPLY (SELECT COALESCE(q.TargetKey,
                              CASE WHEN q.Lane IS NULL AND LEN(q.JobId) > 34 AND SUBSTRING(q.JobId, LEN(q.JobId) - 32, 1) = N'~'
                                   THEN LEFT(q.JobId, LEN(q.JobId) - 33) END) AS Tk) AS e
        """;

    // ✎ Огляд b11-2 (A, урок 498: звужуємо лише БУДІННЯ, ТРИМАННЯ — завжди консервативне): УСІ живі задачі перерахунку й
    // формул документа — будь-яка ціль (каскад, залежні, натискання, -auto, -dependents-approved, прохід читача, задача формул
    // людини -formula-u{id}) і без цілі (постановка без цілі в Quartz): кожна з них переписує обчислені числа і будить читачів
    // в інших періодах року (якір із річними підсумками, [Period:#k]). `R` — перерахунок, `F` — формули.
    private const string CascadeSql = """
        /* ecr:f5-cascade-gate */
        SELECT CASE WHEN q.JobCode = @formulaCode OR e.Tk LIKE @formulaLike THEN N'F' ELSE N'R' END AS Kind,
               e.Tk AS TargetKey, q.[State] AS State, q.Payload AS Payload,
               CAST(NULL AS int) AS ConsumerTemplateId, CAST(NULL AS int) AS ConsumerPeriodKind,
               CAST(NULL AS int) AS SourceTemplateId, CAST(NULL AS int) AS SourcePeriodKind
          FROM itg.JobProgress AS q WITH (INDEX(IX_JobProgress_Document_Active))
        """ + "\n" + EffectiveTarget + "\n" + """
         WHERE q.DocumentId = @doc AND q.DocumentId IS NOT NULL AND q.[State] IN ('Queued', 'Running')
           AND (q.JobCode IN (@recalcCode, @formulaCode) OR e.Tk LIKE @recalcLike OR e.Tk LIKE @formulaLike)
        """;

    // ⛔ Огляд b9: ціль у черзі в базі — `{тип задачі}~{ціль}` (DbBackgroundJobScheduler.TargetKeyOf).
    private static readonly string AnyRecalcLike = DbBackgroundJobScheduler.TargetKeyOf<IRecalculationJob>("doc") + "%";
    private static readonly string AnyFormulaLike = DbBackgroundJobScheduler.TargetKeyOf<IFormulaRecalculationJob>("doc") + "%-formula%";
    private static readonly string SourceChangeLike =
        DbBackgroundJobScheduler.TargetKeyOf<IRegistryImpactRecalculationJob>("source-change-b") + "%";

    // Задачі впливу зв'язок — лише живі рядки (фільтровані індекси цілі; дзеркало Quartz — ціль з JobId).
    private static readonly string SourceChangeSql = """
        SELECT N'S', e.Tk, q.[State], CAST(NULL AS nvarchar(20)), @t, CAST(@kind AS int), CAST(NULL AS int), CAST(NULL AS int)
          FROM itg.JobProgress AS q WITH (INDEX({0}))
        {effective}
         WHERE {2} AND @t IS NOT NULL AND e.Tk LIKE N'{source}'
        """
        .Replace("{effective}", EffectiveTarget, StringComparison.Ordinal)
        .Replace("{source}", SourceChangeLike, StringComparison.Ordinal);

    private static string SourceChangePart(string index, string where)
        => SourceChangeSql.Replace("{0}", index, StringComparison.Ordinal).Replace("{2}", where, StringComparison.Ordinal);

    // ✎ Огляд b11-2 (B): живі задачі перерахунку й формул ІНШИХ документів (будь-якої ланки ланцюжка джерел X→A→B) — з шаблоном
    // документа й роком (період з payload чи цілі, рік з payload чи цілі `-y{рік}`); формули — рік не визначається (будь-який).
    // Одним проходом фільтрованого IX_JobProgress_Document_Active (живих рядків одиниці–сотні); що з них — предки споживача,
    // вирішує граф (`AncestorsOf`).
    private static readonly string DocumentLinksSql = """
        SELECT N'U', e.Tk, q.[State],
               CASE WHEN q.JobCode = @formulaCode OR e.Tk LIKE N'{formula}' THEN NULL ELSE CAST(y.Yr AS nvarchar(20)) END,
               @t, CAST(@kind AS int), COALESCE(sd.TemplateId, sv.TemplateId),
               CASE WHEN q.JobCode = @formulaCode OR e.Tk LIKE N'{formula}' THEN 1 ELSE 0 END
          FROM itg.JobProgress AS q WITH (INDEX(IX_JobProgress_Document_Active))
        {effective}
          CROSS APPLY (SELECT CASE WHEN ISJSON(q.Payload) = 1 THEN TRY_CAST(JSON_VALUE(q.Payload, '$.periodKey') AS int) END AS JsonPk,
                              CASE WHEN ISJSON(q.Payload) = 1 THEN TRY_CAST(JSON_VALUE(q.Payload, '$.year') AS int) END AS JsonYear,
                              CASE WHEN CHARINDEX(N'-p', e.Tk) > 0 THEN TRY_CAST(SUBSTRING(e.Tk, CHARINDEX(N'-p', e.Tk) + 2, 6) AS int) END AS TkPk,
                              CASE WHEN CHARINDEX(N'-y', e.Tk) > 0 THEN TRY_CAST(SUBSTRING(e.Tk, CHARINDEX(N'-y', e.Tk) + 2, 4) AS int) END AS TkYear) AS k
          CROSS APPLY (SELECT COALESCE(NULLIF(k.JsonPk, 0) / 100, k.JsonYear, k.TkPk / 100, k.TkYear) AS Yr,
                              COALESCE(NULLIF(k.JsonPk, 0), k.TkPk) AS Pk) AS y
          JOIN doc.Document AS sd ON sd.Id = q.DocumentId
          LEFT JOIN cfg.TemplateVersion AS sv ON sv.Id = CASE WHEN sd.TemplateId IS NULL THEN doc.fn_VersionOf(sd.ProjectId, COALESCE(y.Pk, @period)) ELSE doc.fn_TemplateVersionOf(sd.ProjectId, sd.TemplateId, COALESCE(y.Pk, @period)) END
         WHERE q.DocumentId IS NOT NULL AND q.[State] IN ('Queued', 'Running') AND q.DocumentId <> @doc AND @t IS NOT NULL
           AND (q.JobCode IN (@recalcCode, @formulaCode) OR e.Tk LIKE N'{recalc}' OR e.Tk LIKE N'{formula}')
        """
        .Replace("{effective}", EffectiveTarget, StringComparison.Ordinal)
        .Replace("{recalc}", AnyRecalcLike, StringComparison.Ordinal)
        .Replace("{formula}", AnyFormulaLike, StringComparison.Ordinal);

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
        + SourceChangePart("UX_JobProgress_Target_Queued", "q.[State] = 'Queued' AND q.TargetKey IS NOT NULL")
        + "\nUNION ALL\n"
        + SourceChangePart("UX_JobProgress_Target_Running", "q.[State] = 'Running' AND q.TargetKey IS NOT NULL")
        + "\nUNION ALL\n"
        // Дзеркало Quartz: живі рядки без цілі й лейна — пошук за (State, UpdatedAt), живих рядків одиниці.
        + SourceChangePart(
            "IX_JobProgress_State_UpdatedAt",
            "q.[State] IN ('Queued', 'Running') AND q.TargetKey IS NULL AND q.Lane IS NULL AND e.Tk IS NOT NULL")
        + "\nUNION ALL\n" + DocumentLinksSql
        + "\nUNION ALL\nSELECT N'T', NULL, NULL, NULL, @t, CAST(@kind AS int), NULL, NULL;";

    /// <inheritdoc />
    public async Task<bool> IsPendingAsync(long documentId, int periodKey, CancellationToken ct)
    {
        var year = periodKey / 100;
        var documentPrefix = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"doc{documentId}-");

        // Ребра «джерело → споживач» графа (будь-який стан зв'язки) і зв'язки, чиї документні ланки ведуть до каскаду.
        var upstream = await UpstreamAsync(ct).ConfigureAwait(false);

        var rows = await db.Database
            .SqlQueryRaw<GateRow>(
                upstream is null ? CascadeSql : ExtendedSql,
                new SqlParameter("@doc", documentId),
                new SqlParameter("@period", periodKey),
                new SqlParameter("@recalcCode", JobListKinds.RecalculationJobCode),
                new SqlParameter("@formulaCode", typeof(IFormulaRecalculationJob).FullName!),
                new SqlParameter("@recalcLike", QueueKey(documentPrefix) + "%"),
                new SqlParameter("@formulaLike", DbBackgroundJobScheduler.TargetKeyOf<IFormulaRecalculationJob>(documentPrefix) + "formula%"))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // ✎ Огляд b11/b11-2: будь-яка жива задача перерахунку чи формул документа, що може змінити числа року, — чекає ввесь рік.
        if (rows.Exists(r => r.Kind is "R" or "F" && HoldsYear(r, documentPrefix, year)))
        {
            return true;
        }

        if (upstream is null || rows.Find(r => r.Kind == "T") is not { ConsumerTemplateId: { } consumer } self)
        {
            return false;
        }

        var consumerKind = (PeriodKind)(self.ConsumerPeriodKind ?? 0);
        var ancestors = AncestorsOf(upstream, consumer);
        return rows.Exists(r => r.Kind is "S" or "U" && UpstreamPends(upstream, ancestors, r, consumer, consumerKind, periodKey));
    }

    /// <summary>
    /// ✎ Огляд b11/b11-2 (A): чи жива задача документа <paramref name="row"/> тримає рік <paramref name="year"/>. Тримає КОЖНА
    /// задача перерахунку й формул документа, крім тих, чий рік відомо інший: ціль <c>p{рік}MM…</c>/<c>y{рік}…</c>, задача
    /// формул — комірки чи тіло лише іншого року. Рік невідомий (без цілі й payload, тіло не читається) — тримає.
    /// </summary>
    private static bool HoldsYear(GateRow row, string documentPrefix, int year)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Kind == "F")
        {
            return FormulaTouchesYear(row.Payload, year);
        }

        if (row.TargetKey is { } key && BareTarget(key) is var bare && bare.StartsWith(documentPrefix, StringComparison.Ordinal))
        {
            var rest = bare[documentPrefix.Length..];
            if (rest.Length >= 5 && rest[0] is 'p' or 'y'
                && int.TryParse(rest.AsSpan(1, 4), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var targetYear))
            {
                return targetYear == year;
            }
        }

        return PayloadYear(row.Payload) is not { } payloadYear || payloadYear == year;
    }

    /// <summary>Рік тіла перерахунку (<c>periodKey</c> чи <c>year</c>); <c>null</c> — невідомо.</summary>
    private static int? PayloadYear(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(payload);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("periodKey", out var period) && period.ValueKind == JsonValueKind.Number && period.GetInt32() > 0)
            {
                return period.GetInt32() / 100;
            }

            return root.TryGetProperty("year", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetInt32() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
    /// <summary>Чи системна задача формул торкається року: період комірки (<c>cells[].periodKey</c>) чи тіла в році; сумнів — так.</summary>
    private static bool FormulaTouchesYear(string? payload, int year)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return true;
        }

        try
        {
            using var json = JsonDocument.Parse(payload);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return true;
            }

            var fallback = root.TryGetProperty("periodKey", out var body) && body.ValueKind == JsonValueKind.Number
                ? body.GetInt32()
                : (int?)null;
            if (!root.TryGetProperty("cells", out var cells) || cells.ValueKind != JsonValueKind.Array || cells.GetArrayLength() == 0)
            {
                return fallback is not { } only || only / 100 == year;
            }

            foreach (var cell in cells.EnumerateArray())
            {
                var period = cell.ValueKind == JsonValueKind.Object
                             && cell.TryGetProperty("periodKey", out var p) && p.ValueKind == JsonValueKind.Number
                    ? p.GetInt32()
                    : fallback;
                if (period is not { } known || known / 100 == year)
                {
                    return true;
                }
            }

            return false;
        }
        catch (JsonException)
        {
            return true;
        }
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

    /// <summary>
    /// ✎ Огляд b11-2 (B): шаблони-ПРЕДКИ споживача — усі, з яких ланцюжок зв'язок (документні ланки ведуть до каскаду,
    /// <see cref="Upstream.AutoBindings"/>) доходить до нього, до <see cref="RegistryImpactRecalculationRequest.MaxHops"/> ланок,
    /// без зациклення (відвідані). <c>AnyAll</c> — на шляху є ребро обсягу «усе» (рік не звужує).
    /// </summary>
    private static (HashSet<int> Templates, bool AnyAll) AncestorsOf(Upstream upstream, int consumerTemplateId)
    {
        var bySink = upstream.Edges.Where(e => upstream.AutoBindings.Contains(e.BindingId)).ToLookup(e => e.ConsumerTemplateId);
        var seen = new HashSet<int>();
        var anyAll = false;
        var frontier = new List<int> { consumerTemplateId };
        for (var hop = 0; hop < RegistryImpactRecalculationRequest.MaxHops && frontier.Count > 0; hop++)
        {
            var next = new List<int>();
            foreach (var edge in frontier.SelectMany(t => bySink[t]))
            {
                anyAll |= edge.Scope.Kind == PeriodScopeKind.All;
                if (edge.SourceTemplateId != consumerTemplateId && seen.Add(edge.SourceTemplateId))
                {
                    next.Add(edge.SourceTemplateId);
                }
            }

            frontier = next;
        }

        return (seen, anyAll);
    }

    /// <summary>Чи жива ланка (задача впливу зв'язки чи задача документа-предка) доведе каскад до періоду споживача.</summary>
    private static bool UpstreamPends(
        Upstream upstream, (HashSet<int> Templates, bool AnyAll) ancestors, GateRow row, int consumerTemplateId,
        PeriodKind consumerKind, int periodKey)
    {
        var consumerYear = periodKey / 100;
        if (row.Kind == "S")
        {
            if (row.TargetKey is null
                || !SourceChangeKey.TryParse(BareTarget(row.TargetKey), out var bindingId, out var year, out var month, out var approved)
                || approved)
            {
                // «З затвердженими» перераховує й затверджені аркуші — затвердження не закріпить старих чисел.
                return false;
            }

            var byBinding = upstream.Edges.Where(e => e.BindingId == bindingId).ToList();

            // Пряма зв'язка до споживача — відображення обсягу ребра (той самий рік, маска літеральних місяців).
            var direct = byBinding.Where(e => e.ConsumerTemplateId == consumerTemplateId).ToList();
            if (ScopeOf(direct) is { } scope && Affects(scope, year, month, periodKey, consumerKind))
            {
                return true;
            }

            // Через проміжного споживача-предка — рік збігається (чи на шляху «усе»).
            return byBinding.Exists(e => ancestors.Templates.Contains(e.ConsumerTemplateId))
                   && (ancestors.AnyAll || AnyAllScope(byBinding) || year == consumerYear);
        }

        // Документ-предок: будь-яка жива задача перерахунку чи формул — рік збігається, невідомий чи на шляху «усе».
        if (row.SourceTemplateId is not { } template || !ancestors.Templates.Contains(template))
        {
            return false;
        }

        int? rowYear = int.TryParse(row.Payload, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var y)
            ? y
            : null;
        return ancestors.AnyAll || rowYear is not { } known || known == consumerYear;

        static bool AnyAllScope(List<TemplateDependency> edges) => edges.Exists(e => e.Scope.Kind == PeriodScopeKind.All);
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
    /// зачіпає РІК споживача: <c>All</c> — так; <c>Year</c> з маскою літеральних місяців — коли маска містить місяць зміни;
    /// інакше — той самий рік.
    /// </summary>
    /// <remarks>
    /// ⛔ ✎ Огляд b11 (HIGH-1): увесь рік, а не «період-власник і пізніші». Прогін власника місяця будить читачів його чисел,
    /// зокрема РАНІШІ періоди — якір річної розкладки з річними підсумками (<c>PeriodCalendar.AnchorOf</c>) і
    /// <c>[Period:#k]</c>; відпущений раніший місяць затвердили б зі старим підсумком. Будить постановка звужено (PERF-06b),
    /// тримають ворота — рік.
    /// </remarks>
    /// <param name="scope">Обсяг ребер зв'язки.</param>
    /// <param name="sourceYear">Рік зміни джерела.</param>
    /// <param name="sourceMonth">Місяць зміни; <c>null</c> — увесь рік.</param>
    /// <param name="periodKey">Період споживача.</param>
    /// <param name="consumerKind">Періодичність проєкту споживача (не звужує: тримається рік).</param>
    internal static bool Affects(PeriodScope scope, int sourceYear, int? sourceMonth, int periodKey, PeriodKind consumerKind)
    {
        _ = consumerKind;
        if (scope.Kind == PeriodScopeKind.All)
        {
            return true;
        }

        if (sourceYear != periodKey / 100)
        {
            return false;
        }

        return scope.Kind != PeriodScopeKind.Year
               || scope.MonthMask == 0
               || sourceMonth is not { } m
               || (scope.MonthMask & (1 << m)) != 0;
    }

    /// <summary>Ребра графа й зв'язки, документні ланки яких ведуть до каскаду.</summary>
    private sealed record Upstream(
        IReadOnlyList<TemplateDependency> Edges, HashSet<int> AutoBindings);

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
