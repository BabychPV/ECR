// src/Ecr.Infrastructure/Persistence/DocumentSourceIssues.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// B80 (N4, R3.2): проблеми джерела документа-споживача зв'язки <c>ApprovedOnly</c> — спільне тіло панелі проблем
/// розрахунку (<see cref="DocumentSourceMonthGate.ListSourceIssuesAsync"/>) і правила узгодженості
/// <c>STALE_APPROVED_DEPENDENT</c> (<c>StaleApprovedDependentCheck</c>).
/// </summary>
/// <remarks>
/// <para>
/// Ті самі правила, що ворота подання N2b (<see cref="DocumentSourceMonthGate.FindUnapprovedSourceMonthAsync"/>): зв'язки
/// споживача в стані <c>ApprovedOnly</c> (<see cref="DocumentSourceMonthGate.ConsumerBindingsAsync"/>), читання з
/// документа (явний ключ перекриває біт), обраний документ року (<see cref="DocumentSourceBindingStore"/>), затверджені
/// місяці (<see cref="DocumentSourceGate.ApprovedMonthTimesAsync"/>, лише оригінал аркуша; кілька таблиць — місяць
/// затверджено, якщо затверджено будь-яку, момент — найпізніший).
/// </para>
/// <para>
/// ⚠ Затвердження місяця СПОЖИВАЧА — на рівні документ × період: період затверджено, якщо стани оригіналів УСІХ його
/// аркушів (<c>SheetCopyId = null</c>) є і всі <c>Approved</c>; момент — НАЙРАНІШИЙ <c>ApprovedAt</c> серед них
/// (консервативно: аркуш, затверджений раніше за повторне затвердження джерела, уже застарів). Копії аркушів не
/// враховуються. Місяці періоду — за періодичністю проєкту споживача (<see cref="DocumentSourceGate.OwnedMonths"/>).
/// </para>
/// <para>
/// ⛔ За <c>Any</c> — один дрібний запит (чи є хоч одна зв'язка <c>ApprovedOnly</c>) і нічого більше.
/// </para>
/// </remarks>
/// <param name="db">Контекст.</param>
/// <param name="readOverride">
/// Перекриття біта зв'язки <c>ReadFromDocument</c> (<see cref="Ecr.Application.Ports.IDocumentSourceFlagOverrides"/>;
/// у DI завжди <c>null</c> — ключ конфігурації виведено, P-0118-31/33); <c>null</c> — біт зв'язки.
/// </param>
/// <param name="graph">Граф залежностей документів: споживачі зв'язки (B179 L6; ручний перелік не читається).</param>
internal sealed class DocumentSourceIssues(EcrDbContext db, bool? readOverride, IDependencyGraphProvider graph)
{
    /// <summary>Причина <c>SourceChangedAfterApproval</c>: місяць джерела зараз не затверджено.</summary>
    public const string ReasonNotApproved = "notApproved";

    /// <summary>Причина <c>SourceChangedAfterApproval</c>: місяць джерела затверджено пізніше за споживача.</summary>
    public const string ReasonReapproved = "reapproved";

    private readonly Dictionary<(int BindingId, int Year), SourceYear?> years = [];
    private readonly Dictionary<long, (string ProjectCode, string DocumentKey)> sources = [];
    private readonly Dictionary<(long DocumentId, int PeriodKey, DateTime Since, long ConsumerId, int ConsumerPeriod), DateTime?> changes = [];
    private readonly Dictionary<(long DocumentId, int PeriodKey), Dictionary<string, string>?> seen = [];
    private readonly Dictionary<(long DocumentId, int PeriodKey), string> now = [];
    private (bool ApprovedOnly, bool Any)? kindsCache;

    /// <summary>✎ F5-R (D-570): причина <c>SourceChangedAfterApproval</c> за <c>Any</c> — дані місяця джерела змінено після затвердження.</summary>
    public const string ReasonChanged = "changed";

    /// <summary>Чи є хоч одна зв'язка складу HSE400 у стані <c>ApprovedOnly</c> — дрібна перевірка, за <c>Any</c> єдина.</summary>
    /// <param name="db">Контекст.</param>
    /// <param name="ct">Токен скасування.</param>
    public static Task<bool> AnyApprovedOnlyAsync(EcrDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.DocumentSourceBindings.AsNoTracking()
            .AnyAsync(
                b => b.SourceState == DocumentSourceState.ApprovedOnly
                     && b.MappingKind == DocumentSourceBinding.Hse400CompositionMapping,
                ct);
    }

    /// <summary>Проблеми джерела документа за періоди.</summary>
    /// <param name="documentId">Документ-споживач.</param>
    /// <param name="periodKeys">Періоди документа.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>За зростанням періоду й місяця; без повторів (кілька зв'язок одного місяця — один рядок).</returns>
    /// <remarks>
    /// ✎ F5-R (D-570, рішення людини P-1122-4): і зв'язки в стані <c>Any</c> — для ЗАТВЕРДЖЕНОГО періоду споживача, чиї дані
    /// місяця джерела змінено після затвердження (<see cref="SourceChangedAtAsync"/>): каскад затверджений аркуш не
    /// переписує (D-493/D-510), тож без позначки місяць мовчки лишався б зі старими числами. Жоден період не
    /// затверджено, а зв'язок <c>ApprovedOnly</c> немає — далі станів запитів немає.
    /// </remarks>
    public async Task<List<SourceIssueFinding>> ListAsync(
        long documentId, IReadOnlyCollection<int> periodKeys, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(periodKeys);

        // Явне «не читати з документа» — Land читає довідник, джерело не потрібне: жодного запиту.
        if (readOverride == false || periodKeys.Count == 0)
        {
            return [];
        }

        var kinds = await BindingKindsAsync(ct).ConfigureAwait(false);
        if (!kinds.ApprovedOnly && !kinds.Any)
        {
            return [];
        }

        var keys = periodKeys.Distinct().Order().ToList();
        var states = await db.ApprovalStates.AsNoTracking()
            .Where(s => s.DocumentId == documentId && s.SheetCopyId == null && keys.Contains(s.PeriodKey))
            .Select(s => new { s.Id, s.PeriodKey, s.Status, s.ApprovedAt })
            .ToListAsync(ct).ConfigureAwait(false);

        bool Approved(int periodKey)
        {
            var own = states.Where(s => s.PeriodKey == periodKey).ToList();
            return own.Count > 0 && own.TrueForAll(s => s.Status == DocumentStatus.Approved);
        }

        // ✎ F5-R: лише Any — потрібен хоч один затверджений період, інакше проблем Any бути не може.
        if (!kinds.ApprovedOnly && !keys.Exists(Approved))
        {
            return [];
        }

        // ✎ B80 N5 (F13): зв'язки споживача для ВСІХ періодів — один прохід поза циклом (раніше 2 запити на період).
        var bindingsByPeriod = await DocumentSourceMonthGate
            .ConsumerBindingsByPeriodAsync(db, graph, documentId, keys, ct, includeAny: kinds.Any).ConfigureAwait(false);

        var findings = new List<SourceIssueFinding>();
        foreach (var periodKey in keys)
        {
            if (!bindingsByPeriod.TryGetValue(periodKey, out var bindings) || bindings.Count == 0)
            {
                continue;
            }

            var own = states.Where(s => s.PeriodKey == periodKey).ToList();
            var approved = Approved(periodKey);
            var approvedAt = approved ? own.Min(s => s.ApprovedAt) : null;
            var stateId = own.Count == 0 ? (long?)null : own.Min(s => s.Id);
            var year = periodKey / 100;

            foreach (var binding in bindings)
            {
                if (!(readOverride ?? binding.ReadFromDocument)
                    || (binding.State == DocumentSourceState.Any && approvedAt is null))
                {
                    continue;
                }

                // A5: документа-джерела за рік немає — рядки довідника, як доти; проблеми немає.
                var source = await SourceYearAsync(binding.Id, year, ct).ConfigureAwait(false);
                if (source is null)
                {
                    continue;
                }

                foreach (var month in DocumentSourceGate.OwnedMonths(binding.PeriodKind, periodKey % 100))
                {
                    string? kind;
                    string? reason;
                    DateTime? sourceApprovedAt = null;
                    DateTime? changedAt = null;
                    if (binding.State == DocumentSourceState.Any)
                    {
                        // ✎ W15z M7 (огляд b11-2, b11 №3): правка, яку вже містять числа періоду споживача (Id не вищий за
                        // межу входів чинного прогону документа — InputsCellChangeWatermark), — не «змінено після». Доти
                        // повторне затвердження протягом 5 хв після правки джерела (повернути → F9 → подати → затвердити)
                        // лишалося позначеним назавжди. Межа — без годинників: Id закоміченого журналу, а не ChangedAt (мітка
                        // початку транзакції правки, годинник Api) проти StartedAt (годинник воркера); прогін, що не
                        // перерахував затверджене (перенесення, пропуск), межі не піднімає — і копії аркушів теж.
                        changedAt = await SourceChangedAtAsync(source, year, month, approvedAt!.Value, documentId, periodKey, ct).ConfigureAwait(false);

                        // ✎ P-1123-20: зміни джерела БЕЗ aud.CellChange (видалення рядка подією чи людиною, новий актуальний
                        // прогін джерела з іншими F_<X>) — стан джерела зараз ≠ стану, який бачив чинний прогін споживача.
                        // Стану не записано (прогін до міграції, невідомо) — як доти: лише журнал комірок.
                        var changed = changedAt is not null
                                      || await SourceStateChangedAsync(source, year, month, documentId, periodKey, ct).ConfigureAwait(false);

                        (kind, reason) = !changed
                            ? (null, null)
                            : (ISourceIssueReader.SourceChangedAfterApproval, ReasonChanged);
                    }
                    else
                    {
                        var sourceApproved = source.Approved.TryGetValue(month, out var at);
                        sourceApprovedAt = sourceApproved ? at : null;
                        (kind, reason) = Classify(approved, approvedAt, sourceApproved, at);
                    }

                    if (kind is null)
                    {
                        continue;
                    }

                    var facts = await SourceFactsAsync(source.DocumentId, ct).ConfigureAwait(false);
                    findings.Add(new SourceIssueFinding(
                        new SourceIssueRow(
                            kind, periodKey, month, (year * 100) + month, source.DocumentId, facts.ProjectCode,
                            facts.DocumentKey, sourceApprovedAt, approvedAt, changedAt),
                        reason,
                        stateId));
                }
            }
        }

        return [.. findings.DistinctBy(f => (f.Row.Kind, f.Row.PeriodKey, f.Row.MonthNumber))];
    }

    /// <summary>
    /// ✎ P-1123-20: стан джерел документа-споживача <paramref name="documentId"/> за роки <paramref name="years"/> — те, що
    /// записує прогін споживача на старті (<c>CalculationRun.SourceStateJson</c>): для кожної зв'язки <c>Any</c> з читанням із
    /// документа — обраний документ-джерело року і відбиток кожного його періоду-власника місяця (<see cref="SourceStatesAsync(SourceYear, IReadOnlyCollection{int}, CancellationToken)"/>).
    /// </summary>
    /// <param name="documentId">Документ-споживач.</param>
    /// <param name="years">Роки.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>JSON <c>{"{документ}:{період}": "{відбиток}"}</c> (порожній об'єкт — джерел немає).</returns>
    /// <remarks>
    /// ⛔ Що читає споживач (<see cref="Hse400CompositionSnapshotLoader"/>): лише екземпляри ОРИГІНАЛУ таблиць зв'язки обраного
    /// документа (<c>SheetCopyId = NULL</c>) — комірки рядків і розрахункові <c>F_&lt;X&gt;</c> актуальних прогонів; ні
    /// копій аркушів, ні полів шапки (інших відображень зв'язки, ніж склад HSE400, немає — <c>MappingKind</c>). Тому
    /// додавання/видалення копії й шапка в стан не входять — числа споживача від них не залежать.
    /// </remarks>
    internal async Task<string> SourceStatesJsonAsync(long documentId, IReadOnlyCollection<int> years, CancellationToken ct)
    {
        var states = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var periodKeys = years.SelectMany(y => Enumerable.Range(1, 12).Select(m => (y * 100) + m)).ToList();
        var kinds = await BindingKindsAsync(ct).ConfigureAwait(false);
        if (kinds.Any && readOverride != false && periodKeys.Count > 0)
        {
            var bindingsByPeriod = await DocumentSourceMonthGate
                .ConsumerBindingsByPeriodAsync(db, graph, documentId, periodKeys, ct, includeAny: true).ConfigureAwait(false);
            var done = new HashSet<(int Binding, int Year)>();
            foreach (var (periodKey, bindings) in bindingsByPeriod)
            {
                foreach (var binding in bindings.Where(b => b.State == DocumentSourceState.Any && (readOverride ?? b.ReadFromDocument)))
                {
                    var year = periodKey / 100;
                    if (!done.Add((binding.Id, year)) || await SourceYearAsync(binding.Id, year, ct).ConfigureAwait(false) is not { } source)
                    {
                        continue;
                    }

                    var owners = Enumerable.Range(1, 12).Select(m => OwnerPeriod(source.PeriodKind, year, m)).Distinct().ToList();
                    foreach (var (owner, state) in await SourceStatesAsync(source, owners, ct).ConfigureAwait(false))
                    {
                        states[StateKey(source.DocumentId, owner)] = state;
                    }
                }
            }
        }

        return System.Text.Json.JsonSerializer.Serialize(states);
    }

    /// <summary>Ключ стану джерела: документ і період-власник.</summary>
    private static string StateKey(long documentId, int periodKey)
        => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{documentId}:{periodKey}");

    /// <summary>
    /// ✎ P-1123-20: чи стан місяця <paramref name="month"/> джерела зараз інший, ніж бачив чинний прогін споживача
    /// (<c>SourceStateJson</c>). Стану не записано — <c>false</c> (лише журнал комірок, як доти); джерела в записі немає
    /// (тоді його не було) — так.
    /// </summary>
    private async Task<bool> SourceStateChangedAsync(
        SourceYear source, int year, int month, long consumerId, int consumerPeriod, CancellationToken ct)
    {
        if (!seen.TryGetValue((consumerId, consumerPeriod), out var recorded))
        {
            var json = (await db.CalculationRuns.AsNoTracking()
                    .Where(r => r.DocumentId == consumerId && r.PeriodKey == consumerPeriod
                                && r.Status == Ecr.Domain.Entities.Calculations.CalculationRun.CurrentStatus)
                    .Select(r => r.SourceStateJson)
                    .ToListAsync(ct).ConfigureAwait(false))
                .FirstOrDefault();
            recorded = json is null ? null : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            seen[(consumerId, consumerPeriod)] = recorded;
        }

        if (recorded is null)
        {
            return false;
        }

        var owner = OwnerPeriod(source.PeriodKind, year, month);
        if (!now.TryGetValue((source.DocumentId, owner), out var current))
        {
            current = (await SourceStatesAsync(source, [owner], ct).ConfigureAwait(false)).GetValueOrDefault(owner) ?? string.Empty;
            now[(source.DocumentId, owner)] = current;
        }

        return !recorded.TryGetValue(StateKey(source.DocumentId, owner), out var was)
               || !string.Equals(was, current, StringComparison.Ordinal);
    }

    /// <summary>
    /// ✎ P-1123-20: відбиток кожного періоду <paramref name="periodKeys"/> документа-джерела — одним запитом: рядки
    /// оригіналів таблиць зв'язки (кількість і сума <c>RowVersion</c> невидалених: видалення рядка, вставка, будь-який
    /// запис) і хеш розрахункових <c>F_&lt;X&gt;</c> (екземпляр, рядок, код, значення) актуальних прогонів документа за
    /// період (правило <see cref="CurrentRunSelection"/>: власний документний, інакше прогін усього проєкту). Порожній
    /// перерахунок з тими самими числами хеша не міняє.
    /// </summary>
    private async Task<Dictionary<int, string>> SourceStatesAsync(SourceYear source, IReadOnlyCollection<int> periodKeys, CancellationToken ct)
    {
        // ⚠ SQL Server 2016 (SqlServer2016FloorTests): без STRING_AGG — рядки F_ повертаються тим самим запитом, хеш — тут.
        var rows = await db.Database
            .SqlQueryRaw<SourceStateRow>(
                """
                SELECT p.Period AS Period, N'R' AS Kind, CONCAT(rs.Cnt, ':', rs.Sm) AS Value
                  FROM (SELECT CAST(j.[value] AS int) AS Period FROM OPENJSON(@periods) AS j) AS p
                 OUTER APPLY (SELECT COUNT_BIG(*) AS Cnt, SUM(CAST(CAST(r.RowVersion AS bigint) AS decimal(38, 0))) AS Sm
                                FROM doc.TableRow AS r
                               WHERE r.IsDeleted = 0 AND r.PeriodKey = p.Period
                                 AND r.TableInstanceId IN (SELECT i.Id FROM doc.TableInstance AS i
                                                            WHERE i.DocumentId = @doc AND i.SheetCopyId IS NULL
                                                              AND i.TableDefId IN (SELECT CAST(t.[value] AS int) FROM OPENJSON(@tables) AS t))) AS rs
                UNION ALL
                SELECT res.PeriodKey, N'F',
                       CONCAT(res.TableInstanceId, N'|', res.SourceRowKey, N'|', res.OutputCode, N'|', CONVERT(nvarchar(80), res.[Value]))
                  FROM calc.CalculationRun AS cr
                  JOIN calc.CalculationResult AS res ON res.CalculationRunId = cr.Id
                 WHERE cr.Status = N'Current'
                   AND res.PeriodKey IN (SELECT CAST(j.[value] AS int) FROM OPENJSON(@periods) AS j)
                   AND (cr.DocumentId = @doc
                        OR (cr.DocumentId IS NULL
                            AND cr.ProjectId = (SELECT d.ProjectId FROM doc.Document AS d WHERE d.Id = @doc)
                            AND NOT EXISTS (SELECT 1 FROM calc.CalculationRun AS own
                                             WHERE own.Status = N'Current' AND own.DocumentId = @doc AND own.PeriodKey = res.PeriodKey)))
                   AND res.DocumentId = @doc AND res.SourceRowKey LIKE N'F[_]%'
                   AND res.TableInstanceId IN (SELECT i.Id FROM doc.TableInstance AS i
                                                WHERE i.DocumentId = @doc AND i.SheetCopyId IS NULL
                                                  AND i.TableDefId IN (SELECT CAST(t.[value] AS int) FROM OPENJSON(@tables) AS t))
                """,
                new Microsoft.Data.SqlClient.SqlParameter("@doc", source.DocumentId),
                new Microsoft.Data.SqlClient.SqlParameter("@periods", System.Text.Json.JsonSerializer.Serialize(periodKeys)),
                new Microsoft.Data.SqlClient.SqlParameter("@tables", System.Text.Json.JsonSerializer.Serialize(source.TableIds)))
            .ToListAsync(ct).ConfigureAwait(false);

        var result = new Dictionary<int, string>();
        foreach (var period in rows.Where(r => r.Kind == "R"))
        {
            var results = rows.Where(r => r.Kind == "F" && r.Period == period.Period).Select(r => r.Value).Order(StringComparer.Ordinal);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(string.Join('\n', results))));
            result[period.Period] = period.Value + "|" + hash;
        }

        return result;
    }

    /// <summary>Рядок відбитка стану джерела: <c>R</c> — штамп рядків періоду, <c>F</c> — один розрахунковий результат.</summary>
    private sealed record SourceStateRow(int Period, string Kind, string Value);

    /// <summary>
    /// ✎ F5-R (D-570): запас перед моментом затвердження споживача. Момент затвердження (<c>ApprovedAt</c>) береться ДО
    /// читання воріт F5, а момент правки джерела (<c>aud.CellChange.ChangedAt</c>) — на початку транзакції правки: правка,
    /// що закомічена між воротами й комітом затвердження, може мати мітку трохи раніше за <c>ApprovedAt</c>. Сумнів — у бік
    /// позначки (D-567 (3)): правка джерела за цей запас ДО затвердження теж позначається («перегляньте»).
    /// </summary>
    internal static readonly TimeSpan ApprovalRaceMargin = TimeSpan.FromMinutes(5);

    /// <summary>
    /// ✎ F5-R (D-570): коли востаннє змінено дані місяця <paramref name="month"/> обраного документа-джерела в таблицях зв'язки
    /// (журнал комірок, період-власник місяця за періодичністю проєкту джерела) ПІСЛЯ <paramref name="approvedAt"/>
    /// мінус <see cref="ApprovalRaceMargin"/>; <c>null</c> — не змінено. Позначки помилок формул (<c>CalcError*</c>) — не
    /// зміна чисел. Один запит (ключ <c>IX_CellChange_DocPeriodChanged</c>), кеш на документ × період × поріг.
    /// </summary>
    /// <remarks>✎ W15z M7 (огляд b11 №3): правки з <c>Id</c> ≤ межі входів чинного прогону документа-споживача <paramref name="consumerId"/> за
    /// період <paramref name="consumerPeriod"/> (<c>InputsCellChangeWatermark</c>) числа споживача вже містять — не рахуються. Межа —
    /// підзапитом у тому самому запиті (нуль додаткових звернень); немає прогону чи межі — 0 (рахується кожна правка).
    /// ⛔ Лише ЧИННИЙ прогін осі документа: прогін, що став чинним ПІСЛЯ затвердження, затверджених аркушів (оригіналів і копій) не
    /// переписує й переносить їхні результати — його межа за побудовою не вища за межу прогону, чиї числа затверджено
    /// (<c>RecalculationJob.EffectiveInputsWatermark</c>). Тому «завершився до затвердження» годинником не перевіряється, а
    /// витіснений прогін (його прибирає SupersededResultsPurger) не потрібен.</remarks>
    private async Task<DateTime?> SourceChangedAtAsync(
        SourceYear source, int year, int month, DateTime approvedAt, long consumerId, int consumerPeriod, CancellationToken ct)
    {
        var owner = OwnerPeriod(source.PeriodKind, year, month);
        var since = approvedAt - ApprovalRaceMargin;
        if (changes.TryGetValue((source.DocumentId, owner, since, consumerId, consumerPeriod), out var cached))
        {
            return cached;
        }

        source.ColumnIds ??= await db.ColumnDefs.AsNoTracking()
            .Where(c => source.TableIds.Contains(c.TableDefId))
            .Select(c => c.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        var found = await db.Database
            .SqlQueryRaw<DateTime?>(
                """
                SELECT MAX(c.ChangedAt) AS Value
                  FROM aud.CellChange AS c
                 WHERE c.DocumentId = @doc AND c.PeriodKey = @period AND c.ChangedAt > @since
                   AND c.Id > ISNULL((SELECT MAX(r.InputsCellChangeWatermark) FROM calc.CalculationRun AS r
                                       WHERE r.DocumentId = @consumer AND r.PeriodKey = @consumerPeriod AND r.Status = N'Current'), 0)
                   AND c.Origin NOT IN (N'CalcError', N'CalcErrorResolved')
                   AND c.ColumnDefId IN (SELECT CAST(j.[value] AS int) FROM OPENJSON(@columns) AS j)
                """,
                new Microsoft.Data.SqlClient.SqlParameter("@doc", source.DocumentId),
                new Microsoft.Data.SqlClient.SqlParameter("@period", owner),
                new Microsoft.Data.SqlClient.SqlParameter("@since", System.Data.SqlDbType.DateTime2) { Value = since },
                new Microsoft.Data.SqlClient.SqlParameter("@consumer", consumerId),
                new Microsoft.Data.SqlClient.SqlParameter("@consumerPeriod", consumerPeriod),
                new Microsoft.Data.SqlClient.SqlParameter("@columns", System.Text.Json.JsonSerializer.Serialize(source.ColumnIds)))
            .ToListAsync(ct).ConfigureAwait(false);

        var result = found.FirstOrDefault() is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : (DateTime?)null;
        changes[(source.DocumentId, owner, since, consumerId, consumerPeriod)] = result;
        return result;
    }

    /// <summary>Період-власник місяця за періодичністю проєкту (те саме правило, що <see cref="DocumentSourceGate.OwnedMonths"/>).</summary>
    internal static int OwnerPeriod(PeriodKind kind, int year, int month) => (year * 100) + kind switch
    {
        PeriodKind.Yearly => 1,
        PeriodKind.Quarterly => (((month - 1) / 3) * 3) + 1,
        _ => month,
    };

    /// <summary>Які стани мають зв'язки складу HSE400 (з читанням із документа для <c>Any</c>); кеш на екземпляр.</summary>
    private async Task<(bool ApprovedOnly, bool Any)> BindingKindsAsync(CancellationToken ct)
    {
        if (kindsCache is { } cached)
        {
            return cached;
        }

        var found = await db.DocumentSourceBindings.AsNoTracking()
            .Where(b => b.MappingKind == DocumentSourceBinding.Hse400CompositionMapping
                        && (b.SourceState == DocumentSourceState.ApprovedOnly || readOverride == true || b.ReadFromDocument))
            .Select(b => b.SourceState)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

        kindsCache = (found.Contains(DocumentSourceState.ApprovedOnly), found.Contains(DocumentSourceState.Any));
        return kindsCache.Value;
    }
    /// <summary>Вид проблеми місяця за станом споживача і джерела; <c>null</c> — проблеми немає.</summary>
    private static (string? Kind, string? Reason) Classify(
        bool approved, DateTime? approvedAt, bool sourceApproved, DateTime? sourceApprovedAt)
    {
        if (!approved)
        {
            return sourceApproved ? (null, null) : (ISourceIssueReader.SourceMonthNotApproved, null);
        }

        if (!sourceApproved)
        {
            return (ISourceIssueReader.SourceChangedAfterApproval, ReasonNotApproved);
        }

        return sourceApprovedAt > approvedAt
            ? (ISourceIssueReader.SourceChangedAfterApproval, ReasonReapproved)
            : (null, null);
    }

    /// <summary>Обраний документ-джерело року зв'язки і його затверджені місяці з моментом; кеш на зв'язку × рік.</summary>
    private async Task<SourceYear?> SourceYearAsync(int bindingId, int year, CancellationToken ct)
    {
        if (years.TryGetValue((bindingId, year), out var cached))
        {
            return cached;
        }

        var store = new DocumentSourceBindingStore(db, graph);
        var tableIds = await store.BoundTableIdsAsync(bindingId, ct).ConfigureAwait(false);
        var chosen = await store.ChosenSourceDocumentAsync(tableIds, year, ct).ConfigureAwait(false);
        SourceYear? result = null;
        if (chosen is not null)
        {
            var times = await DocumentSourceGate
                .ApprovedMonthTimesAsync(db, [chosen.DocumentId], tableIds, ct).ConfigureAwait(false);
            var approved = new Dictionary<int, DateTime?>();
            foreach (var ((_, _, sourceYear, month), at) in times)
            {
                if (sourceYear != year)
                {
                    continue;
                }

                approved[month] = approved.TryGetValue(month, out var seen) && (at is null || (seen is { } s && s >= at))
                    ? seen
                    : at;
            }

            result = new SourceYear(chosen.DocumentId, approved, chosen.PeriodKind, [.. tableIds]);
        }

        years[(bindingId, year)] = result;
        return result;
    }

    /// <summary>Код проєкту й бізнес-ключ документа-джерела; кеш на документ.</summary>
    private async Task<(string ProjectCode, string DocumentKey)> SourceFactsAsync(long documentId, CancellationToken ct)
    {
        if (sources.TryGetValue(documentId, out var cached))
        {
            return cached;
        }

        var facts = await (
                from d in db.Documents.AsNoTracking()
                where d.Id == documentId
                join p in db.Projects.AsNoTracking() on d.ProjectId equals p.Id
                select new { ProjectCode = p.Code, d.BusinessKey })
            .FirstAsync(ct).ConfigureAwait(false);

        var result = (facts.ProjectCode, facts.BusinessKey);
        sources[documentId] = result;
        return result;
    }

    private sealed record SourceYear(long DocumentId, Dictionary<int, DateTime?> Approved, PeriodKind PeriodKind, List<int> TableIds)
    {
        /// <summary>Колонки таблиць зв'язки — лише для <c>Any</c> (лінива: <c>ApprovedOnly</c> їх не читає).</summary>
        public List<int>? ColumnIds { get; set; }
    }
}

/// <summary>Проблема джерела з тим, що потрібно правилу узгодженості.</summary>
/// <param name="Row">Рядок для панелі.</param>
/// <param name="Reason">
/// Для <c>SourceChangedAfterApproval</c> — <see cref="DocumentSourceIssues.ReasonNotApproved"/> чи
/// <see cref="DocumentSourceIssues.ReasonReapproved"/>; інакше <c>null</c>.
/// </param>
/// <param name="StateId">Найменший id стану оригіналу аркуша споживача за період (сутність знахідки); <c>null</c> — станів немає.</param>
internal sealed record SourceIssueFinding(SourceIssueRow Row, string? Reason, long? StateId);
