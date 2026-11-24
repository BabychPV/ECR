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
           AND (e.Tk LIKE @recalcLike OR e.Tk = @formulaTarget)
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
        var year = periodKey / 100;
        var documentPrefix = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"doc{documentId}-");
        var recalcLike = QueueKey(documentPrefix) + "%";
        var formulaTarget = DbBackgroundJobScheduler.TargetKeyOf<IFormulaRecalculationJob>(
            FormulaRecalculationTarget.OfDocument(documentId, createdByUserId: null));

        // Ребра «джерело → споживач» графа (будь-який стан зв'язки) і зв'язки, чиї документні ланки ведуть до каскаду.
        var upstream = await UpstreamAsync(ct).ConfigureAwait(false);

        var rows = await db.Database
            .SqlQueryRaw<GateRow>(
                upstream is null ? CascadeSql : ExtendedSql,
                new SqlParameter("@doc", documentId),
                new SqlParameter("@period", periodKey),
                new SqlParameter("@recalcLike", recalcLike),
                new SqlParameter("@formulaTarget", formulaTarget))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // ✎ Огляд b11: будь-яка жива задача документа, що ще може змінити числа року, — чекає ввесь рік.
        if (rows.Exists(r => r.Kind is "Q" or "C" && HoldsYear(r, documentPrefix, formulaTarget, year)))
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

    /// <summary>
    /// ✎ Огляд b11 (HIGH-1/2): чи жива задача документа <paramref name="row"/> тримає рік <paramref name="year"/>.
    /// </summary>
    /// <remarks>
    /// Тримають: каскад року (<c>y{рік}-dependents</c>); залежні будь-якого місяця року (<c>p{рік}MM-dependents</c>); ціль
    /// місяця року (<c>p{рік}MM</c>) — у Quartz завжди (payload немає), у черзі в базі — прохід читача (<see cref="IsReaderPass"/>,
    /// звичайне натискання «Перерахувати» — ні); системна задача формул документа (<c>doc{id}-formula</c>: читачі fan-out) —
    /// якщо в її комірках є період року або payload не читається. Не тримають: <c>-auto</c>, <c>-dependents-approved</c>
    /// (переписує й затверджені), задачі формул людини (<c>-formula-u{id}</c>), інший рік.
    /// </remarks>
    private static bool HoldsYear(GateRow row, string documentPrefix, string formulaTarget, int year)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.TargetKey is not { } key)
        {
            return false;
        }

        if (string.Equals(key, formulaTarget, StringComparison.Ordinal))
        {
            return row.Kind == "Q" || FormulaTouchesYear(row.Payload, year);
        }

        var bare = BareTarget(key);
        if (!bare.StartsWith(documentPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = bare[documentPrefix.Length..];
        var yearText = year.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (string.Equals(rest, $"y{yearText}-dependents", StringComparison.Ordinal))
        {
            return true;
        }

        if (rest.Length < 7 || rest[0] != 'p' || !rest.AsSpan(1, 4).SequenceEqual(yearText)
            || !int.TryParse(rest.AsSpan(5, 2), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            return false;
        }

        var suffix = rest[7..];
        return suffix switch
        {
            "-dependents" => true,
            "" => row.Kind == "Q" || IsReaderPass(row.Payload),
            _ => false,
        };
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
            if (!root.TryGetProperty("cells", out var cells) || cells.ValueKind != JsonValueKind.Array)
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
            var byBinding = edges.Where(e => e.BindingId == bindingId).ToList();
            return !approved
                   && ScopeOf(byBinding) is { } scope
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
