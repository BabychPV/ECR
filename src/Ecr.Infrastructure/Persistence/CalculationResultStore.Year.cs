using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Читання фактів огляду «Calculation results — усі місяці року» (<see cref="ICalculationYearReader"/>).</summary>
/// <remarks>
/// ⛔ Набір запитів не залежить від числа рядків і місяців: ключі чисел (агрегат на
/// <c>IX_CalculationResult_DocRun (PeriodKey, DocumentId, CalculationRunId, Kind, TableInstanceId)</c> по ≤ 12 партиціях;
/// ключ CRY01) — один на набір
/// прогонів, числа — один на набір прогонів, комірки формул, рядки й підписи — по одному, довідники й версії — по
/// одному. Кожен запит до партиційованих таблиць несе <c>PeriodKey IN (…)</c> на ведучій таблиці; вкладені пошуки
/// (рядки екземпляра, комірки рядка) — рівністю PeriodKey з ведучим рядком.
/// <para>
/// ⚠ Заміри (CalculationYearPerfTests, 20 таблиць × 1 000 рядків × 12 місяців): матеріалізація ~840 тис. фактів (числа,
/// формули, рядки, підписи) займала 4.4–4.6 с із ~5.8 с відповіді. Тому зведення (<see cref="CalculationYearReadRequest.Aggregate"/>)
/// просить у SQL лише маску місяців на вихід, без рядків і підписів, а сторінка таблиці
/// (<see cref="CalculationYearReadRequest.SliceResults"/>) читає лише свої екземпляри.
/// </para>
/// </remarks>
public sealed partial class CalculationResultStore : ICalculationYearReader
{
    /// <inheritdoc />
    public async Task<CalculationYearFacts> ReadYearAsync(CalculationYearReadRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PeriodKeys.Count == 0)
        {
            return CalculationYearFacts.Empty;
        }

        var documentId = request.DocumentId;
        var keys = request.PeriodKeys.Distinct().Order().ToList();
        var current = await CurrentRunsOfDocumentAsync(documentId, ct).ConfigureAwait(false);

        // Прогін кожного періоду — тим самим правилом, що й ReadCurrentAsync; рядок числа належить періоду лише тоді,
        // коли його прогін у наборі саме цього періоду (проєктний прогін іншого періоду тут не рахується).
        var effective = keys.ToDictionary(k => k, k => EffectiveRuns(current, documentId, k).Select(r => r.Id).ToHashSet());

        // Місяці з однаковим набором прогонів — один запит (набори двох видів: документні прогони, або документні +
        // проєктні), тож запитів ≤ 2 на практиці і ніколи не на місяць понад число різних наборів.
        var limit = Ceiling * keys.Count;
        var groups = effective
            .Where(kv => kv.Value.Count > 0)
            .GroupBy(kv => string.Join(',', kv.Value.Order()), StringComparer.Ordinal)
            .Select(g => (Keys: g.Select(kv => kv.Key).ToList(), Runs: g.First().Value.ToList()))
            .ToList();

        // CRY03: числова частина зведення (ключі, агрегат виходів, числа без екземпляра) — з кешу, якщо актуальні прогони
        // й екземпляри документа ті самі. Сторінка й експорт (значення) читають наживо.
        YearResultsEntry? cached = null;
        string? cacheKey = null;
        if (request.Aggregate && yearCache is not null)
        {
            cacheKey = await YearResultsKeyAsync(documentId, keys, effective, ct).ConfigureAwait(false);
            cached = yearCache.Get(cacheKey);
        }

        // 1. Ключі чисел УСЬОГО документа (агрегат, без значень): прогін місяця, стеля і звуження для вузького читача
        //    не залежать від того, яку таблицю читає запит.
        var keyRows = cached?.Keys ?? await KeyRowsAsync(documentId, groups, ct).ConfigureAwait(false);

        // ⛔ Стеля рахує лише рядки (період, прогін цього періоду) — як і раніше, до читання значень.
        ThrowIfOverCeiling(keyRows.Sum(k => (long)k.Count), limit, documentId, keys[0]);

        if (request.Aggregate && cached is null)
        {
            var (lines, legacyRows) = await LoadAggregateResultsAsync(documentId, groups, ct).ConfigureAwait(false);
            cached = new YearResultsEntry(keyRows, lines, legacyRows);
            if (cacheKey is not null)
            {
                yearCache!.Set(cacheKey, cached);
            }
        }

        var runs = RunsOf(keyRows, current);
        var resultKeys = keyRows
            .Select(k => new YearResultKey(k.PeriodKey, k.TableInstanceId, k.MethodologyVersionId, k.OutputCode))
            .Distinct()
            .ToList();

        var instanceIds = request.TableInstanceIds.Distinct().ToList();
        // CRY04: зведення — маски місяців без розгортання (будівник розгортає сам); Results — лише числа без екземпляра.
        var resultMonths = request.Aggregate ? AggregateResultMonths(cached!, instanceIds) : [];
        var results = request.Aggregate
            ? [.. cached!.Legacy.OrderBy(r => r.PeriodKey)]
            : await ResultValuesAsync(documentId, groups, request.SliceResults ? instanceIds : null, ct).ConfigureAwait(false);

        var formulaCells = new List<YearFormulaCell>();
        var formulaMonths = new List<YearFormulaCellMonths>();
        var rows = new List<YearRow>();
        var labelCells = new List<YearLabelCell>();
        if (instanceIds.Count > 0)
        {
            if (request.Aggregate)
            {
                formulaMonths = await AggregateFormulaCellsAsync(documentId, keys, instanceIds, limit, ct).ConfigureAwait(false);
            }
            else
            {
                formulaCells = await FormulaCellValuesAsync(documentId, keys, instanceIds, limit, ct).ConfigureAwait(false);
            }

            if (request.IncludeRows)
            {
                rows = [.. await ReadRowsAsync(keys, instanceIds, ct).ConfigureAwait(false)];
            }

            if (request.LabelColumnIds.Count > 0)
            {
                // ⚠ «ColumnDefId + 0»: колонка — залишковий фільтр, а не частина пошуку. Інакше перелік колонок (по одній на
                // таблицю) множив пошуки на рядок: DMV — 10.6 млн читань і 6.5 с на 240 тис. рядків огляду для експорту.
                var sql = $"""
                    SELECT row.PeriodKey, row.TableInstanceId, row.RowKey, c.ColumnDefId, c.ValueString,
                           CAST(c.ValueRegistryEntryId AS bigint) AS EntryId, c.ValueNumeric
                      FROM doc.TableRow AS row {LiveRowSeek}
                     INNER LOOP JOIN doc.CellValue AS c {CellSeek}
                        ON c.PeriodKey = row.PeriodKey AND c.TableRowId = row.Id
                     WHERE {RowsOf(keys, instanceIds)}
                       AND c.ColumnDefId + 0 IN ({List(request.LabelColumnIds.Distinct())})
                       AND c.IsEmpty = 0
                    """;
                labelCells = [.. (await db.Database.SqlQueryRaw<LabelCellRow>(sql).ToListAsync(ct).ConfigureAwait(false))
                    .Select(c => new YearLabelCell(c.PeriodKey, c.TableInstanceId, c.RowKey, c.ColumnDefId, c.ValueString, c.EntryId, c.ValueNumeric))];
            }
        }

        var entryIds = results.Where(r => r.SubstanceEntryId is not null).Select(r => r.SubstanceEntryId!.Value)
            .Concat(resultMonths.Where(r => r.SubstanceEntryId is not null).Select(r => r.SubstanceEntryId!.Value))
            .Concat(labelCells.Where(c => c.EntryId is not null).Select(c => c.EntryId!.Value))
            .Distinct()
            .ToList();
        var entries = entryIds.Count == 0
            ? []
            : await db.RegistryEntries
                .AsNoTracking()
                .Where(e => entryIds.Contains(e.Id))
                .Select(e => new { e.Id, e.Code, e.DisplayL10n })
                .ToListAsync(ct)
                .ConfigureAwait(false);

        var versionIds = results.Select(r => r.MethodologyVersionId).Concat(resultMonths.Select(r => r.MethodologyVersionId)).Distinct().ToList();
        var versions = versionIds.Count == 0
            ? []
            : await (
                    from version in db.MethodologyVersions.AsNoTracking()
                    where versionIds.Contains(version.Id)
                    join methodology in db.Methodologies.AsNoTracking() on version.MethodologyId equals methodology.Id
                    select new YearMethodologyVersion(
                        version.Id, methodology.Id, methodology.Code, methodology.NameL10n, version.Version))
                .ToListAsync(ct)
                .ConfigureAwait(false);

        return new CalculationYearFacts(
            runs,
            resultKeys,
            results,
            formulaCells,
            rows,
            labelCells,
            [.. entries.Select(e => new YearEntry(e.Id, e.Code, e.DisplayL10n))],
            versions)
        {
            ResultMonths = resultMonths,
            FormulaCellMonths = formulaMonths,
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<YearRow>> ReadRowsAsync(
        IReadOnlyList<int> periodKeys, IReadOnlyCollection<long> tableInstanceIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(periodKeys);
        ArgumentNullException.ThrowIfNull(tableInstanceIds);
        if (periodKeys.Count == 0 || tableInstanceIds.Count == 0)
        {
            return [];
        }

        var sql = $"""
            SELECT row.TableInstanceId, row.RowKey, row.RowDefId, row.Ordinal
              FROM doc.TableRow AS row {RowSeek}
             WHERE {RowsOf(periodKeys.Distinct(), tableInstanceIds.Distinct())}
            """;
        return await db.Database.SqlQueryRaw<YearRow>(sql).ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Стеля екземплярів на період — та сама, що в <c>RowStore.GetTableInstancesAsync</c>.</summary>
    private const int MaxTableInstancesPerPeriod = 1000;

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Доти будівник огляду кликав <c>IRowStore.GetTableInstancesAsync</c> на кожен із 12 місяців: 12 гарячих пошуків
    /// і ще по одному <c>arc.TableInstance</c> на кожен місяць без екземплярів (майбутні місяці року) — до 23 звернень
    /// на кожне зведення й сторінку. Тут — одне: гарячі екземпляри всіх періодів пошуком <c>UQ_TableInstance</c> і
    /// архівні лише тих періодів, де гарячих немає (<c>NOT EXISTS</c>) — те саме правило F-13, що й у
    /// <c>RowStore</c>, застосоване до кожного періоду окремо. Порядок і стеля на період — як у поштучному читанні.
    /// </remarks>
    public async Task<IReadOnlyList<TableInstanceRef>> ReadTableInstancesAsync(
        long documentId, IReadOnlyList<int> periodKeys, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(periodKeys);
        if (periodKeys.Count == 0)
        {
            return [];
        }

        var keys = List(periodKeys.Distinct().Order());
        var sql = $"""
            SELECT x.TableInstanceId, x.DocumentId, x.TableDefId,
                   doc.fn_VersionOf(d.ProjectId, x.PeriodKey) AS TemplateVersionId, x.PeriodKey, x.SheetCopyId,
                   x.HasFormulaErrors
              FROM (SELECT ti.Id AS TableInstanceId, ti.DocumentId, ti.TableDefId, ti.PeriodKey, ti.SheetCopyId,
                           -- ✎ C-1: той самий прапорець, що й у RowStore (EXISTS у calc.FormulaError).
                           CAST(CASE WHEN EXISTS (SELECT 1
                                                    FROM calc.FormulaError AS fe
                                                   WHERE fe.PeriodKey = ti.PeriodKey AND fe.TableInstanceId = ti.Id)
                                     THEN 1 ELSE 0 END AS bit) AS HasFormulaErrors
                      FROM doc.TableInstance AS ti {InstanceSeek}
                     WHERE ti.PeriodKey IN ({keys}) AND ti.DocumentId = @documentId
                    UNION ALL
                    -- ✎ C-1: архівний період стану помилок формул не має (як ArchiveAwareCellReader).
                    SELECT a.Id, a.DocumentId, a.TableDefId, a.PeriodKey, a.SheetCopyId, CAST(0 AS bit)
                      FROM arc.TableInstance AS a
                     WHERE a.PeriodKey IN ({keys}) AND a.DocumentId = @documentId
                       AND NOT EXISTS (SELECT 1
                                         FROM doc.TableInstance AS h {InstanceSeek}
                                        WHERE h.PeriodKey = a.PeriodKey AND h.DocumentId = @documentId)) AS x
             INNER JOIN doc.Document AS d ON d.Id = x.DocumentId
             ORDER BY x.PeriodKey, x.SheetCopyId, x.TableInstanceId
            """;
        var all = await db.Database
            .SqlQueryRaw<TableInstanceRef>(sql, new SqlParameter("@documentId", documentId))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return [.. all.GroupBy(i => i.PeriodKey).SelectMany(g => g.Take(MaxTableInstancesPerPeriod))];
    }

    /// <summary>
    /// Предикат рядків екземплярів: <c>PeriodKey IN (…)</c> (партиції) і <c>TableInstanceId IN (…)</c> — разом із
    /// <see cref="RowSeek"/> це пошук по <c>UQ_TableRow_Key</c> (PeriodKey, TableInstanceId), а не перегляд рядків
    /// УСІХ документів партиції.
    /// </summary>
    /// <remarks>
    /// ⚠ Заміри (CalculationYearPerfTests, DMV): без підказки оптимізатор обирав перегляд кластерного індексу (рядкам
    /// бракує <c>Ordinal</c>/значень у некластерному індексі), а з голим <c>FORCESEEK</c> — пошук лише за PeriodKey; в
    /// обох випадках сторінка ОДНІЄЇ таблиці читала всі 240 тис. рядків документа (3 216 сторінок), на живій базі — ще й
    /// рядки інших 95 документів. Перелік — цілі числа сервера (періоди, екземпляри з бази), тож він іде текстом.
    /// </remarks>
    /// <summary>Пошук рядків за (PeriodKey, TableInstanceId) — <c>UQ_TableRow_Key</c> (ім'я з міграції InitialCreate).</summary>
    /// <remarks>
    /// ⚠ Саме <c>FORCESEEK, INDEX(…)</c>, а не <c>FORCESEEK (індекс (колонки))</c>: друга форма на партиційованому
    /// індексі з переліком у предикаті дає Msg 8622 «could not produce a query plan» (перевірено на тестовій базі).
    /// </remarks>
    private const string RowSeek = "WITH (FORCESEEK, INDEX(UQ_TableRow_Key))";

    /// <summary>
    /// Пошук комірок рядка за (PeriodKey, TableRowId) — <c>PK_CellValue</c>. ⚠ Без <c>c.PeriodKey IN (…)</c>: період
    /// комірки задає рівність із рядком (зовнішній запит несе <c>IN</c>), а список у предикаті перетворював пошук на 12
    /// пошуків на рядок (DMV: 10 млн читань на 240 тис. рядків).
    /// </summary>
    private const string CellSeek = "WITH (FORCESEEK, INDEX(PK_CellValue))";

    /// <summary>Пошук екземплярів документа за (PeriodKey, DocumentId) — <c>UQ_TableInstance</c>.</summary>
    private const string InstanceSeek = "WITH (FORCESEEK, INDEX(UQ_TableInstance))";

    /// <summary>
    /// Пошук ЖИВИХ рядків за (PeriodKey, TableInstanceId) — <c>IX_TableRow_Live</c> (B-18): <c>RowKey</c> в INCLUDE,
    /// <c>Id</c> — ключ кластера, фільтр <c>IsDeleted = 0</c>. Запит мусить нести <c>row.IsDeleted = 0</c> літералом.
    /// </summary>
    /// <remarks>
    /// ⚠ CRY01, заміри: <see cref="RowSeek"/> (<c>UQ_TableRow_Key</c>) не має <c>IsDeleted</c>, і кожен рядок платив key
    /// lookup — 498 тис. читань <c>doc.TableRow</c> у зведенні на 240 тис. рядків проти 2.7 тис. тут. Для читання з
    /// <c>RowDefId</c>/<c>Ordinal</c> (<c>ReadRowsAsync</c> рядків екземплярів) лишається <see cref="RowSeek"/>: їх немає в жодному.
    /// </remarks>
    private const string LiveRowSeek = "WITH (FORCESEEK, INDEX(IX_TableRow_Live))";

    /// <summary>
    /// Пошук чисел за (PeriodKey, DocumentId, CalculationRunId, Kind, TableInstanceId) — <c>IX_CalculationResult_DocRun</c>
    /// (ключ CRY01, <c>07-partition-tables.sql</c>).
    /// </summary>
    private const string ResultSeek = "WITH (FORCESEEK, INDEX(IX_CalculationResult_DocRun))";

    private static string RowsOf(IEnumerable<int> keys, IEnumerable<long> instanceIds)
        => $"row.PeriodKey IN ({List(keys)}) AND row.TableInstanceId IN ({List(instanceIds)}) AND row.IsDeleted = 0";

    /// <summary>
    /// Виходи документа в прогонах групи: періоди й прогони — літералами (цілі числа сервера), документ — параметром
    /// <c>@documentId</c>. Пошук <c>IX_CalculationResult_DocRun</c> на кожну пару (період, прогін).
    /// </summary>
    private static string OutputsOf(List<int> keys, List<long> runs)
        => $"r.PeriodKey IN ({List(keys)}) AND r.DocumentId = @documentId AND r.CalculationRunId IN ({List(runs)}) "
           + $"AND r.Kind = {((int)CalculationResultKind.Output).ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Пари (період, прогін) групи як <c>VALUES</c> — ведуча множина пошуку чисел сторінки.</summary>
    private static string RunPairs(List<int> keys, List<long> runs)
        => "(VALUES " + string.Join(", ", keys.SelectMany(k => runs.Select(r =>
               $"({k.ToString(CultureInfo.InvariantCulture)}, {r.ToString(CultureInfo.InvariantCulture)})")))
           + ") AS k (PeriodKey, RunId)";

    /// <summary>Прогін місяця, що справді дав числа документа: документний перед проєктним, новіший перед старішим.</summary>
    private static List<YearRun> RunsOf(List<YearKeyRow> keyRows, List<CurrentRunRow> current)
        => [.. keyRows
            .GroupBy(r => r.PeriodKey)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var ids = g.Select(r => r.CalculationRunId).ToHashSet();
                var run = current.Where(c => ids.Contains(c.Id))
                    .OrderByDescending(c => c.DocumentId is not null)
                    .ThenByDescending(c => c.Id)
                    .First();
                return new YearRun(g.Key, run.Id, Utc(run.FinishedAt));
            })];

    /// <summary>
    /// Числа методологій зі значеннями (огляд для експорту або сторінка таблиці); <c>slice</c> — екземпляри таблиці
    /// сторінки (плюс числа без екземпляра), <c>null</c> — увесь документ.
    /// </summary>
    private async Task<List<YearResult>> ResultValuesAsync(
        long documentId, List<(List<int> Keys, List<long> Runs)> groups, List<long>? slice, CancellationToken ct)
    {
        // ⚠ CRY01: сирим SQL, а не LINQ — параметризовані переліки EF давали пошук лише за PeriodKey (див. ключі вище).
        // Сторінка: пари (період, прогін) ведуть LOOP-пошук по ключу DocRun до Kind і TableInstanceId — читаються лише
        // числа екземплярів сторінки (синтетика: 3 671 → 768 читань), а не всі числа документа за рік.
        const string Projection = """
            r.PeriodKey, r.MethodologyVersionId, r.TableInstanceId, r.SourceRowKey, r.OutputCode,
                   CAST(r.SubstanceEntryId AS bigint) AS SubstanceEntryId, r.UnitId, r.Value
            """;
        var kept = new List<YearResult>();
        foreach (var (groupKeys, groupRuns) in groups)
        {
            var output = ((int)CalculationResultKind.Output).ToString(CultureInfo.InvariantCulture);
            var sql = slice is null
                ? $"""
                    SELECT {Projection}
                      FROM calc.CalculationResult AS r
                     WHERE {OutputsOf(groupKeys, groupRuns)}
                     ORDER BY r.PeriodKey, r.Id
                    """
                : $"""
                    SELECT {Projection}
                      FROM {RunPairs(groupKeys, groupRuns)}
                     INNER LOOP JOIN calc.CalculationResult AS r {ResultSeek}
                        ON r.PeriodKey = k.PeriodKey AND r.DocumentId = @documentId AND r.CalculationRunId = k.RunId
                       AND r.Kind = {output}
                       AND (r.TableInstanceId IS NULL{(slice.Count == 0 ? string.Empty : $" OR r.TableInstanceId IN ({List(slice)})")})
                     ORDER BY r.PeriodKey, r.Id
                    """;
            kept.AddRange(await db.Database
                .SqlQueryRaw<YearResult>(sql, new SqlParameter("@documentId", documentId))
                .ToListAsync(ct)
                .ConfigureAwait(false));
        }

        // Місяць цілком належить одній групі, тож стійке впорядкування за місяцем зберігає порядок Id усередині.
        return [.. kept.OrderBy(r => r.PeriodKey)];
    }

    /// <summary>
    /// Ключі чисел документа (агрегат, без значень). ⚠ Сирим SQL з переліками-літералами, а не LINQ: параметризовані
    /// <c>IN (@p…)</c> EF давали пошук лише за PeriodKey по ВСІХ партиціях, а DocumentId — залишковим фільтром (CRY01).
    /// </summary>
    private async Task<List<YearKeyRow>> KeyRowsAsync(
        long documentId, List<(List<int> Keys, List<long> Runs)> groups, CancellationToken ct)
    {
        var keyRows = new List<YearKeyRow>();
        foreach (var (groupKeys, groupRuns) in groups)
        {
            var sql = $"""
                SELECT r.PeriodKey, r.CalculationRunId, r.TableInstanceId, r.MethodologyVersionId, r.OutputCode, COUNT(*) AS Count
                  FROM calc.CalculationResult AS r
                 WHERE {OutputsOf(groupKeys, groupRuns)}
                 GROUP BY r.PeriodKey, r.CalculationRunId, r.TableInstanceId, r.MethodologyVersionId, r.OutputCode
                """;
            keyRows.AddRange(await db.Database
                .SqlQueryRaw<YearKeyRow>(sql, new SqlParameter("@documentId", documentId))
                .ToListAsync(ct)
                .ConfigureAwait(false));
        }

        return keyRows;
    }

    /// <summary>
    /// Ключ запису <see cref="CalculationYearResultsCache"/> (CRY03): документ, періоди, актуальні прогони КОЖНОГО періоду
    /// і відбиток екземплярів документа за ці періоди (PeriodKey, Id, TableDefId, SheetCopyId).
    /// </summary>
    /// <remarks>
    /// ⛔ Від чого залежить числова частина зведення: рядки <c>calc.CalculationResult</c> актуальних прогонів (незмінні
    /// після того, як прогін став актуальним — нові числа дає новий прогін з новим Id) і з'єднання з
    /// <c>doc.TableInstance</c> (група = таблиця + копія аркуша; перелік відомих екземплярів). Перенесення версії міняє
    /// TableDefId екземпляра, склад аркушів — екземпляри; обидва — у відбитку. Відбиток — один пошук
    /// <c>UQ_TableInstance</c> (сотні рядків), SHA-256 замість тексту ключа на кілобайти.
    /// </remarks>
    private async Task<string> YearResultsKeyAsync(
        long documentId, List<int> keys, Dictionary<int, HashSet<long>> effective, CancellationToken ct)
    {
        var sql = $"""
            SELECT ti.PeriodKey, ti.Id, ti.TableDefId, ti.SheetCopyId
              FROM doc.TableInstance AS ti {InstanceSeek}
             WHERE ti.PeriodKey IN ({List(keys)}) AND ti.DocumentId = @documentId
            """;
        var instances = await db.Database
            .SqlQueryRaw<InstanceFingerprintRow>(sql, new SqlParameter("@documentId", documentId))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var text = new System.Text.StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"d{documentId}");
        foreach (var key in keys)
        {
            text.Append(CultureInfo.InvariantCulture, $"|p{key}:{string.Join(',', effective[key].Order())}");
        }

        foreach (var i in instances.OrderBy(i => i.PeriodKey).ThenBy(i => i.Id))
        {
            text.Append(CultureInfo.InvariantCulture, $"|i{i.PeriodKey}.{i.Id}.{i.TableDefId}.{i.SheetCopyId}");
        }

        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString()));
        return "cry:" + documentId.ToString(CultureInfo.InvariantCulture) + ":" + Convert.ToHexString(hash);
    }

    /// <summary>
    /// Зведення: числа методологій, згруповані в SQL до виходу таблиці (визначення + копія аркуша) з маскою місяців, і
    /// числа без екземпляра (до D-401) — сирими: їхній господар залежить від місяця. Без звуження й без переліку
    /// екземплярів читача — це те, що кешується (CRY03).
    /// </summary>
    private async Task<(List<AggregateResultRow> Lines, List<YearResult> Legacy)> LoadAggregateResultsAsync(
        long documentId, List<(List<int> Keys, List<long> Runs)> groups, CancellationToken ct)
    {
        var lines = new List<AggregateResultRow>();
        var legacy = new List<YearResult>();
        foreach (var (groupKeys, groupRuns) in groups)
        {
            // ⚠ Лише цілі числа сервера (періоди, прогони) — у тексті запиту, щоб партиції відсікалися статично;
            // документ — параметром.
            var sql = $"""
                SELECT MIN(r.TableInstanceId) AS TableInstanceId, r.MethodologyVersionId, r.SourceRowKey, r.OutputCode,
                       CAST(r.SubstanceEntryId AS bigint) AS SubstanceEntryId, r.UnitId,
                       {MonthMask("r.PeriodKey")} AS Months
                  FROM calc.CalculationResult AS r
                  LEFT JOIN doc.TableInstance AS ti
                    ON ti.PeriodKey = r.PeriodKey AND ti.Id = r.TableInstanceId
                   AND ti.PeriodKey IN ({List(groupKeys)})
                 WHERE r.PeriodKey IN ({List(groupKeys)})
                   AND r.DocumentId = @documentId
                   AND r.CalculationRunId IN ({List(groupRuns)})
                   AND r.Kind = {(int)CalculationResultKind.Output}
                   AND r.TableInstanceId IS NOT NULL
                 GROUP BY ti.TableDefId, ti.SheetCopyId, r.MethodologyVersionId, r.SourceRowKey, r.OutputCode,
                          r.SubstanceEntryId, r.UnitId
                """;
            lines.AddRange(await db.Database
                .SqlQueryRaw<AggregateResultRow>(sql, new SqlParameter("@documentId", documentId))
                .ToListAsync(ct)
                .ConfigureAwait(false));

            // Числа без екземпляра: пошук по ключу DocRun до TableInstanceId IS NULL (CRY01) — літералами, як ключі вище.
            var legacySql = $"""
                SELECT r.PeriodKey, r.MethodologyVersionId, r.TableInstanceId, r.SourceRowKey, r.OutputCode,
                       CAST(r.SubstanceEntryId AS bigint) AS SubstanceEntryId, r.UnitId, CAST(0 AS decimal(34,16)) AS Value
                  FROM calc.CalculationResult AS r
                 WHERE {OutputsOf(groupKeys, groupRuns)}
                   AND r.TableInstanceId IS NULL
                """;
            legacy.AddRange(await db.Database
                .SqlQueryRaw<YearResult>(legacySql, new SqlParameter("@documentId", documentId))
                .ToListAsync(ct)
                .ConfigureAwait(false));
        }

        return (lines, legacy);
    }

    /// <summary>
    /// Агрегат зведення — виходи з маскою місяців, злиті за (екземпляр-представник, версія, рядок, вихід, речовина,
    /// одиниця) у порядку першої появи; екземпляр поза переліком запиту (видалений) — <c>-1</c>, як і в сирому читанні:
    /// таблиця не встановлюється. Живе, не кешується: залежить від переліку екземплярів запиту.
    /// </summary>
    private static List<YearResultMonths> AggregateResultMonths(YearResultsEntry entry, List<long> instanceIds)
    {
        var lines = new Dictionary<(long Instance, int Version, string? Row, string Output, long? Substance, int Unit), int>();
        foreach (var row in entry.Lines)
        {
            var key = (row.TableInstanceId, row.MethodologyVersionId, row.SourceRowKey, row.OutputCode, row.SubstanceEntryId, row.UnitId);
            lines[key] = lines.GetValueOrDefault(key) | row.Months;
        }

        var known = instanceIds.ToHashSet();
        return [.. lines.Select(kv => new YearResultMonths(
            kv.Value, kv.Key.Version, known.Contains(kv.Key.Instance) ? kv.Key.Instance : -1, kv.Key.Row, kv.Key.Output,
            kv.Key.Substance, kv.Key.Unit))];
    }

    /// <summary>Розраховані комірки формул шаблону зі значеннями (екземпляри сторінки або увесь документ).</summary>
    private async Task<List<YearFormulaCell>> FormulaCellValuesAsync(
        long documentId, List<int> keys, List<long> instanceIds, int limit, CancellationToken ct)
    {
        // ⚠ Рядки — пошуком по екземплярах, комірки — пошуком по (PeriodKey, TableRowId) кожного рядка: ціна залежить від
        // розміру таблиці (документа), а не від усієї партиції. IsCalculated у предикаті, але не в ключі індексу —
        // рядок читає всі свої комірки (див. 02-contracts.md, «Calculation results за рік», про відфільтрований індекс).
        // Рядки — IX_TableRow_Live (CRY01): без key lookup за IsDeleted на кожен рядок.
        var sql = $"""
            SELECT TOP ({limit + 1}) row.PeriodKey, row.TableInstanceId, row.RowKey, c.ColumnDefId, c.ValueNumeric
              FROM doc.TableRow AS row {LiveRowSeek}
             INNER LOOP JOIN doc.CellValue AS c {CellSeek}
                ON c.PeriodKey = row.PeriodKey AND c.TableRowId = row.Id
             WHERE {RowsOf(keys, instanceIds)}
               AND c.IsCalculated = 1 AND c.IsEmpty = 0 AND c.ValueNumeric IS NOT NULL
            """;
        var cells = await db.Database.SqlQueryRaw<FormulaCellRow>(sql).ToListAsync(ct).ConfigureAwait(false);
        ThrowIfOverCeiling(cells.Count, limit, documentId, keys[0]);
        return cells.ConvertAll(c => new YearFormulaCell(c.PeriodKey, c.TableInstanceId, c.RowKey, c.ColumnDefId, c.ValueNumeric));
    }

    /// <summary>Зведення: комірки формул, згруповані в SQL до (таблиця, рядок, колонка) з маскою місяців.</summary>
    private async Task<List<YearFormulaCellMonths>> AggregateFormulaCellsAsync(
        long documentId, List<int> keys, List<long> instanceIds, int limit, CancellationToken ct)
    {
        // ⚠ CRY01, заміри (синтетика 20 × 1 000 × 12): рядки — IX_TableRow_Live, а не UQ_TableRow_Key, бо в тому немає
        // IsDeleted і кожен із 240 тис. рядків платив key lookup — читання doc.TableRow 498 тис. → 2.7 тис. Комірки й
        // далі — пошук у PK_CellValue на рядок (513 тис. читань): відфільтрований індекс за IsCalculated — окреме рішення
        // (ціна запису розрахованих комірок +21–25 %, заміри CRY01).
        var sql = $"""
            SELECT MIN(ti.Id) AS TableInstanceId, row.RowKey, c.ColumnDefId,
                   {MonthMask("c.PeriodKey")} AS Months,
                   COUNT_BIG(*) AS Cells
              FROM doc.TableInstance AS ti {InstanceSeek}
             INNER LOOP JOIN doc.TableRow AS row {LiveRowSeek}
                ON row.PeriodKey = ti.PeriodKey AND row.TableInstanceId = ti.Id
             INNER LOOP JOIN doc.CellValue AS c {CellSeek}
                ON c.PeriodKey = row.PeriodKey AND c.TableRowId = row.Id
             WHERE ti.PeriodKey IN ({List(keys)})
               AND ti.DocumentId = @documentId
               AND row.IsDeleted = 0
               AND c.IsCalculated = 1 AND c.IsEmpty = 0 AND c.ValueNumeric IS NOT NULL
             GROUP BY ti.TableDefId, ti.SheetCopyId, row.RowKey, c.ColumnDefId
            """;
        // ⚠ Без переліку екземплярів у тексті запиту: OPENJSON у напівз'єднанні оптимізатор ставив ПІСЛЯ з'єднання з
        // комірками (розбір JSON на кожну комірку — 1.8–2.0 с проти ~0.5 с). Екземпляри документа за рік — це й так усі
        // його екземпляри цих періодів; групи поза переліком будівника (стеля екземплярів) відкидаються тут.
        var rows = await db.Database
            .SqlQueryRaw<AggregateFormulaRow>(sql, new SqlParameter("@documentId", documentId))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        ThrowIfOverCeiling(rows.Sum(r => r.Cells), limit, documentId, keys[0]);

        var known = instanceIds.ToHashSet();
        return [.. rows.Where(r => known.Contains(r.TableInstanceId))
            .Select(r => new YearFormulaCellMonths(r.Months, r.TableInstanceId, r.RowKey, r.ColumnDefId))];
    }

    /// <summary>
    /// Маска місяців групи: біт місяця — <c>MAX(CASE …)</c> на кожен із 12 місяців. Не <c>SUM(DISTINCT POWER(…))</c>:
    /// DISTINCT-агрегат змушував SQL сортувати кожну групу (заміри: ~1 с CPU на 240 тис. чисел).
    /// </summary>
    private static string MonthMask(string periodKey)
        => "(" + string.Join(" + ", Enumerable.Range(1, 12).Select(m =>
            $"MAX(CASE WHEN {periodKey} % 100 = {m.ToString(CultureInfo.InvariantCulture)} THEN {(1 << (m - 1)).ToString(CultureInfo.InvariantCulture)} ELSE 0 END)")) + ")";

    private static string List(IEnumerable<int> values) => string.Join(',', values.Select(v => v.ToString(CultureInfo.InvariantCulture)));

    private static string List(IEnumerable<long> values) => string.Join(',', values.Select(v => v.ToString(CultureInfo.InvariantCulture)));

    /// <summary>Запис <see cref="CalculationYearResultsCache"/>: сирі (до звуження) числові факти зведення документа.</summary>
    internal sealed record YearResultsEntry(List<YearKeyRow> Keys, List<AggregateResultRow> Lines, List<YearResult> Legacy)
    {
        /// <summary>Розмір запису для стелі кешу — число рядків фактів.</summary>
        public long Size => (long)Keys.Count + Lines.Count + Legacy.Count;
    }

    /// <summary>Екземпляр документа для відбитка ключа кешу.</summary>
    private sealed record InstanceFingerprintRow(int PeriodKey, long Id, int TableDefId, long? SheetCopyId);

    /// <summary>Ключ чисел (агрегат).</summary>
    internal sealed record YearKeyRow(
        int PeriodKey, long CalculationRunId, long? TableInstanceId, int MethodologyVersionId, string OutputCode, int Count);

    /// <summary>Вихід методології з маскою місяців (агрегат зведення).</summary>
    internal sealed record AggregateResultRow(
        long TableInstanceId, int MethodologyVersionId, string? SourceRowKey, string OutputCode, long? SubstanceEntryId,
        int UnitId, int Months);

    /// <summary>Значення колонки-підпису (проєкція запиту).</summary>
    private sealed record LabelCellRow(
        int PeriodKey, long TableInstanceId, string RowKey, int ColumnDefId, string? ValueString, long? EntryId, decimal? ValueNumeric);

    /// <summary>Розрахована комірка формули (проєкція запиту).</summary>
    private sealed record FormulaCellRow(int PeriodKey, long TableInstanceId, string RowKey, int ColumnDefId, decimal ValueNumeric);

    /// <summary>Комірка формули з маскою місяців (агрегат зведення).</summary>
    private sealed record AggregateFormulaRow(long TableInstanceId, string RowKey, int ColumnDefId, int Months, long Cells);

    private static DateTime? Utc(DateTime? value)
        => value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    /// <summary>Понад стелю — відмова тим самим кодом, що й читання одного періоду (D2-05), а не обрізання.</summary>
    private static void ThrowIfOverCeiling(long count, int limit, long documentId, int periodKey)
    {
        if (count <= limit)
        {
            return;
        }

        throw new BusinessRuleException(
            "ECR-CALC-0422",
            $"Чисел документа {documentId} за рік більше за {limit}: показати їх частково означало б показати хибні суми.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-CALC-0422.resultsTooLarge",
                ["documentId"] = documentId,
                ["periodKey"] = periodKey,
                ["max"] = limit,
            });
    }
}
