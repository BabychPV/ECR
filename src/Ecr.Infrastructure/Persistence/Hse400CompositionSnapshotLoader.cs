using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Domain.Entities.Configuration;
using Ecr.Expressions.Evaluation;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Знімок довідників, у якому склад газу <c>LAND_HSE400_COMPOSITION</c> береться з документа HSE400, а не з
/// імпортованих рядків <c>dic.*</c> (рішення людини 11.11: довідник не дублює документ).
/// </summary>
/// <remarks>
/// Обгортка над <see cref="RegistrySnapshotLoader"/>: усі довідники читає він, а тут рядки складу за роки, що мають
/// документ HSE400, підмінюються значеннями документа (<see cref="Hse400CompositionMapping.Overlay"/>).
///
/// ⛔ Вимкнено (біт зв'язки <c>ReadFromDocument</c> = 0 або зв'язки немає) — той самий знімок довідників.
/// Запит до документів іде лише тоді, коли серед завантажених довідників справді є склад газу.
///
/// ⚠ Документ читається ПОТОЧНИМ, а не «станом на» момент прогону: комірки вводу — з <c>doc.CellValue</c>,
/// розрахункові рядки <c>F_&lt;X&gt;</c> — з актуального прогону (<see cref="ICalculationResultStore.ReadCurrentAsync"/>).
/// Перерахунок залежних документів Land після зміни HSE400 — окремий крок (3b).
///
/// ⚠ Якщо за рік є кілька документів шаблону, береться документ із найбільшим ідентифікатором (останній створений).
///
/// ✎ PERF-11 (аудит 21.11): склад документів (читання комірок усіх періодів + результати актуальних прогонів) не
/// перечитується кожним прогоном doc×місяць Land. <c>cache</c> (один на процес; <c>null</c> — без кешу) тримає
/// останній підрахований склад під ВІДБИТКОМ УСЬОГО, від чого він залежить (<see cref="Hse400CompositionCache"/>): обрані
/// екземпляри, версії їхніх рядків (<c>doc.TableRow.RowVersion</c> піднімає кожен запис комірки), опис рядків і колонок,
/// актуальні прогони документів (звідси розрахункові <c>F_&lt;X&gt;</c>) і затверджені місяці. Відбиток рахується на КОЖНОМУ
/// виклику дешевими запитами (без читання комірок), тож правка джерела дає інший відбиток і склад читається наново —
/// застарілого значення кеш не віддає за побудовою.
///
/// ✎ B80 (N1), P-0118-31/33: вирішує лише біт зв'язки <c>cfg.DocumentSourceBinding.ReadFromDocument</c> (немає
/// зв'язки — вимкнено); ключ <see cref="RetiredKey"/> виведено. Параметр <c>enabled</c> — лише для тестів (у DI — <c>null</c>). Стан-ворота зв'язки
/// <see cref="DocumentSourceState.ApprovedOnly"/> лишає з документа лише затверджені місяці (A2, варіант (a): місяць
/// без затвердження — без рядків, і довідник за рік документа однаково відкинуто). За <see cref="DocumentSourceState.Any"/>
/// запиту станів немає і рядки — ті самі, що до B80.
/// </remarks>
public sealed class Hse400CompositionSnapshotLoader(
    IRegistrySnapshotLoader inner,
    EcrDbContext db,
    ICalculationResultStore results,
    bool? enabled,
    string registryCode = Hse400CompositionMapping.RegistryCode,
    string templateCode = Hse400CompositionMapping.TemplateCode,
    Hse400CompositionCache? cache = null) : IRegistrySnapshotLoader
{
    /// <summary>
    /// ✎ P-0118-31/33 («один вимикач»): ВИВЕДЕНИЙ ключ. Застосунок його НЕ читає — читання складу з документа вирішує
    /// лише біт зв'язки <c>cfg.DocumentSourceBinding.ReadFromDocument</c>. Залишкова змінна середовища
    /// <c>ECR_Calculations__Hse400CompositionFromDocument</c> на майданчику — без дії; ім'я лишається для тестів-сторожів.
    /// </summary>
    public const string RetiredKey = "Calculations:Hse400CompositionFromDocument";

    /// <inheritdoc />
    public async Task<IRegistrySnapshot> LoadAsync(
        IReadOnlyCollection<int> registryDefIds,
        DateOnly businessDate,
        DateTime? registryAsOfUtc,
        CancellationToken ct)
    {
        var source = await LoadSourceAsync(registryDefIds, registryAsOfUtc, ct).ConfigureAwait(false);
        return source.Build(businessDate);
    }

    /// <inheritdoc />
    public async Task<IRegistrySnapshotSource> LoadSourceAsync(
        IReadOnlyCollection<int> registryDefIds,
        DateTime? registryAsOfUtc,
        CancellationToken ct)
    {
        var source = await inner.LoadSourceAsync(registryDefIds, registryAsOfUtc, ct).ConfigureAwait(false);
        if (enabled == false
            || source is not RegistrySnapshotSource loaded
            || !loaded.Registries.Any(r => string.Equals(r.Code, registryCode, StringComparison.OrdinalIgnoreCase)))
        {
            return source;
        }

        var binding = await DocumentSourceGate.FindBindingAsync(db, templateCode, registryCode, ct).ConfigureAwait(false);
        if (!(enabled ?? binding?.ReadFromDocument ?? false))
        {
            return source;
        }

        var approvedOnly = binding?.SourceState == DocumentSourceState.ApprovedOnly;
        var (rows, years) = await ReadDocumentsAsync(approvedOnly, ct).ConfigureAwait(false);
        return Hse400CompositionMapping.Overlay(loaded, registryCode, rows, years);
    }

    /// <summary>Склад із документів HSE400 (через кеш, якщо він заданий).</summary>
    /// <param name="approvedOnly">Стан-ворота зв'язки: лише затверджені місяці.</param>
    /// <param name="ct">Скасування.</param>
    internal async Task<(IReadOnlyList<Hse400CompositionRow> Rows, IReadOnlyCollection<int> Years)> ReadDocumentsAsync(
        bool approvedOnly, CancellationToken ct)
    {
        var allTables = await (
                from t in db.Templates.AsNoTracking()
                where t.Code == templateCode
                join v in db.TemplateVersions.AsNoTracking() on t.Id equals v.TemplateId
                join s in db.SheetDefs.AsNoTracking() on v.Id equals s.TemplateVersionId
                where !s.IsDeleted
                join d in db.TableDefs.AsNoTracking() on s.Id equals d.SheetDefId
                where !d.IsDeleted
                select new { d.Id, d.Code })
            .ToListAsync(ct).ConfigureAwait(false);

        // Код таблиці → канонічний префікс потоку; чужі таблиці шаблону складу не дають.
        var streamByTable = new Dictionary<int, string>();
        foreach (var table in allTables)
        {
            var stream = Hse400CompositionMapping.Streams.FirstOrDefault(
                s => string.Equals(s, table.Code, StringComparison.OrdinalIgnoreCase));
            if (stream is not null)
            {
                streamByTable[table.Id] = stream;
            }
        }

        if (streamByTable.Count == 0)
        {
            return ([], []);
        }

        var tableIds = streamByTable.Keys.ToList();

        // ⛔ Документ проєкту золотих тестів (Hse400CompositionMapping.GoldProjectCode) — синтетичний ввід: без цього
        // виключення «найбільший Id за рік» узяв би його, щойно робочий документ створено раніше за золотий.
        var instances = await (
                from i in db.TableInstances.AsNoTracking()
                where tableIds.Contains(i.TableDefId) && i.SheetCopyId == null
                join doc in db.Documents.AsNoTracking() on i.DocumentId equals doc.Id
                join project in db.Projects.AsNoTracking() on doc.ProjectId equals project.Id
                where project.Code != Hse400CompositionMapping.GoldProjectCode
                select new { i.Id, i.DocumentId, i.TableDefId, i.PeriodKeyValue })
            .ToListAsync(ct).ConfigureAwait(false);
        if (instances.Count == 0)
        {
            return ([], []);
        }

        var documentByYear = instances
            .GroupBy(i => i.PeriodKeyValue / 100)
            .ToDictionary(g => g.Key, g => g.Max(i => i.DocumentId));
        var chosen = instances.Where(i => documentByYear[i.PeriodKeyValue / 100] == i.DocumentId).ToList();
        var instanceIds = chosen.Select(i => i.Id).ToList();
        var periodKeys = chosen.Select(i => i.PeriodKeyValue).Distinct().ToList();
        var chosenTableIds = chosen.Select(i => i.TableDefId).Distinct().ToList();

        var monthColumns = await db.ColumnDefs.AsNoTracking()
            .Where(c => chosenTableIds.Contains(c.TableDefId) && c.IsMonthColumn && !c.IsDeleted && c.MonthNumber != null)
            .Select(c => new { c.Id, c.TableDefId, c.Code, Month = c.MonthNumber!.Value })
            .ToListAsync(ct).ConfigureAwait(false);
        var monthByColumn = monthColumns.ToDictionary(c => c.Id, c => (int)c.Month);
        var monthByCode = monthColumns
            .GroupBy(c => c.TableDefId)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(c => c.Code, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(x => x.Key, x => (int)x.First().Month, StringComparer.OrdinalIgnoreCase));
        var monthColumnIds = monthByColumn.Keys.ToList();

        var rowDefs = await db.RowDefs.AsNoTracking()
            .Where(r => chosenTableIds.Contains(r.TableDefId) && !r.IsDeleted)
            .Select(r => new { r.TableDefId, r.RowKeyValue, r.Ordinal })
            .ToListAsync(ct).ConfigureAwait(false);
        var ordinals = rowDefs
            .GroupBy(r => r.TableDefId)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(r => r.RowKeyValue, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(x => x.Key, x => x.Min(r => r.Ordinal), StringComparer.OrdinalIgnoreCase));

        // B80 A2: за ApprovedOnly — один запит станів погодження обраних документів; місяць без затвердження
        // (ніколи або після Reopen) не дає ні вводу, ні F_, ні нульового паритету. За Any запиту немає.
        // ✎ PERF-11: рахується ДО читання комірок — затверджені місяці входять у відбиток кешу.
        var approved = approvedOnly
            ? await DocumentSourceGate.ApprovedMonthsAsync(
                db, [.. chosen.Select(i => i.DocumentId)], chosenTableIds, ct).ConfigureAwait(false)
            : null;

        // ✎ PERF-11: актуальні прогони обраних документів — звідси розрахункові F_<X>; той самий вибір, що в
        // `ICalculationResultStore.ReadCurrentAsync` (`CurrentRunSelection`). Новий актуальний прогін = інша множина Id.
        var documentPeriods = chosen.Select(i => (i.DocumentId, i.PeriodKeyValue)).Distinct().ToList();
        string? fingerprint = null;

        // ⛔ M2 (рев'ю PERF-11): комірки, які пишуть шляхи БЕЗ піднімання версії рядка — перерахунок формул документа
        // (`TouchedRowIds: []`, D14-07) і прив'язка до дозволів, — відбитком рядків не видно. Тому там, де таблиці складу
        // мають формулу чи прив'язку до дозволу, кеш НЕ вмикається зовсім (читання, як до PERF-11). На справжній HSE400
        // їх немає (вхід + розрахункові F_ з прогонів), тож кеш працює; поява формули вимикає його без ручних дій.
        // Комірковий агрегат замість застороги не взято: на Land він коштує стільки ж, скільки саме читання (0,84 с CPU).
        var cacheable = cache is not null
            && !await db.FormulaDefs.AsNoTracking()
                .AnyAsync(f => chosenTableIds.Contains(f.TableDefId) && !f.IsDeleted, ct).ConfigureAwait(false)
            && !await db.PermitSheetBindings.AsNoTracking()
                .AnyAsync(b => chosenTableIds.Contains(b.SheetTableDefId), ct).ConfigureAwait(false);
        if (cacheable)
        {
            // Штамп рядків обраних екземплярів: кількість, найбільший RowVersion І СУМА версій. ⛔ H1 (рев'ю): версію
            // видає UPDATE на початку транзакції запису, а комітяться транзакції в іншому порядку (RCSI): T1 узяла 100 і
            // ще не закомітила, T2 узяла 101 і закомітила — кількість і максимум після коміту T1 ті самі. Сума ж
            // (порядок-незалежна) міняється на кожному закоміченому оновленні рядка (нова версія ≠ стара). Один прохід по
            // рядках без їх читання в пам'ять; списки — JSON-параметрами (`OPENJSON`, як в інших місцях проєкту).
            var periodsJson = System.Text.Json.JsonSerializer.Serialize(periodKeys);
            var instancesJson = System.Text.Json.JsonSerializer.Serialize(instanceIds);
            var stamp = (await db.Database.SqlQuery<RowStamp>($"""
                    SELECT COUNT_BIG(*) AS Cnt, MAX(r.RowVersion) AS MaxVersion,
                           SUM(CAST(CAST(r.RowVersion AS bigint) AS decimal(38, 0))) AS SumVersion
                    FROM doc.TableRow AS r
                    WHERE r.IsDeleted = 0
                      AND r.PeriodKey IN (SELECT CAST([value] AS int) FROM OPENJSON({periodsJson}))
                      AND r.TableInstanceId IN (SELECT CAST([value] AS bigint) FROM OPENJSON({instancesJson}))
                    """).ToListAsync(ct).ConfigureAwait(false)).Single();
            var rowCount = stamp.Cnt;
            var rowStamp = stamp.MaxVersion;
            var rowSum = stamp.SumVersion ?? 0m;

            var currentRuns = new List<(long DocumentId, int PeriodKey, long RunId)>();
            foreach (var documentId in documentPeriods.Select(d => d.DocumentId).Distinct())
            {
                var runs = await CurrentRunSelection.RunsOfDocumentAsync(db, documentId, ct).ConfigureAwait(false);
                foreach (var period in documentPeriods.Where(d => d.DocumentId == documentId).Select(d => d.PeriodKeyValue))
                {
                    currentRuns.AddRange(
                        CurrentRunSelection.Effective(runs, documentId, period).Select(r => (documentId, period, r.Id)));
                }
            }

            fingerprint = Hse400CompositionCache.Fingerprint(
                approvedOnly,
                chosen.Select(i => (i.Id, i.DocumentId, i.TableDefId, i.PeriodKeyValue)),
                rowCount,
                rowStamp ?? [],
                rowSum,
                streamByTable,
                rowDefs.Select(r => (r.TableDefId, r.RowKeyValue, r.Ordinal)),
                monthColumns.Select(c => (c.Id, c.TableDefId, c.Code, (int)c.Month)),
                currentRuns,
                approved?.Select(a => (a.DocumentId, a.TableDefId, a.Year, a.Month)) ?? []);
            if (cache!.TryGet(CacheScope(), fingerprint, out var hit))
            {
                return hit;
            }
        }

        var tableRows = await db.TableRows.AsNoTracking()
            .Where(r => periodKeys.Contains(r.PeriodKeyValue) && instanceIds.Contains(r.TableInstanceId) && !r.IsDeleted)
            .Select(r => new { r.Id, r.TableInstanceId, r.RowKeyValue })
            .ToListAsync(ct).ConfigureAwait(false);
        var rowById = tableRows.ToDictionary(r => r.Id);

        var cells = await db.CellValues.AsNoTracking()
            .Where(c => periodKeys.Contains(c.PeriodKeyValue)
                        && chosenTableIds.Contains(c.TableDefId)
                        && monthColumnIds.Contains(c.ColumnDefId)
                        && c.ValueNumeric != null
                        && !c.IsEmpty)
            .Select(c => new { c.TableRowId, c.ColumnDefId, Value = c.ValueNumeric!.Value })
            .ToListAsync(ct).ConfigureAwait(false);

        var instanceById = chosen.ToDictionary(i => i.Id);
        var found = new List<(int Year, int TableDefId, int Ordinal, Hse400DocumentCell Cell)>();

        bool Passes(long documentId, int tableDefId, int year, int month)
            => approved is null || approved.Contains((documentId, tableDefId, year, month));

        // Місяць таблиці, де введено хоч щось і ВСЕ введене — нуль: (рік, таблиця, місяць) → усі нулі?
        var inputMonths = new Dictionary<(int Year, int TableDefId, int Month), bool>();

        int OrdinalOf(int tableDefId, string rowKey)
            => ordinals.TryGetValue(tableDefId, out var byKey) && byKey.TryGetValue(rowKey, out var ordinal)
                ? ordinal
                : int.MaxValue;

        foreach (var cell in cells)
        {
            if (!rowById.TryGetValue(cell.TableRowId, out var row)
                || Hse400CompositionMapping.IsCalculated(row.RowKeyValue))
            {
                continue;
            }

            var instance = instanceById[row.TableInstanceId];
            var year = instance.PeriodKeyValue / 100;
            if (!Passes(instance.DocumentId, instance.TableDefId, year, monthByColumn[cell.ColumnDefId]))
            {
                continue;
            }

            var inputMonth = (year, instance.TableDefId, monthByColumn[cell.ColumnDefId]);
            inputMonths[inputMonth] = (!inputMonths.TryGetValue(inputMonth, out var allZero) || allZero) && cell.Value == 0m;
            found.Add((year, instance.TableDefId, OrdinalOf(instance.TableDefId, row.RowKeyValue), new Hse400DocumentCell(
                year, monthByColumn[cell.ColumnDefId], streamByTable[instance.TableDefId], row.RowKeyValue, cell.Value)));
        }

        // Розрахункові рядки F_<X>: числа актуального прогону, колонка місяця — у коді виходу.
        foreach (var document in documentPeriods)
        {
            var current = await results.ReadCurrentAsync(document.DocumentId, document.PeriodKeyValue, ct)
                .ConfigureAwait(false);
            foreach (var result in current)
            {
                if (result.SourceRowKey is not { } rowKey
                    || !Hse400CompositionMapping.IsCalculated(rowKey)
                    || result.TableInstanceId is not { } instanceId
                    || !instanceById.TryGetValue(instanceId, out var instance)
                    || !monthByCode.TryGetValue(instance.TableDefId, out var months)
                    || !months.TryGetValue(result.OutputCode, out var month))
                {
                    continue;
                }

                var year = instance.PeriodKeyValue / 100;
                if (!Passes(instance.DocumentId, instance.TableDefId, year, month))
                {
                    continue;
                }

                found.Add((year, instance.TableDefId, OrdinalOf(instance.TableDefId, rowKey), new Hse400DocumentCell(
                    year, month, streamByTable[instance.TableDefId], rowKey, result.Value)));
            }
        }

        // ⚠ Паритет зі старою системою: на місяці, де весь ввід таблиці — нулі, стара система рахувала й зберігала 0
        // (довідник LAND_HSE400_COMPOSITION має там нулі), а модуль місячних колонок гасить ділення на 0 і не пише
        // результат — Land отримував би #N/A замість 0 і губив результати (scratch 11.11: 84 результати Land за
        // FG_HMB M09). Тому порожній F_ такого місяця — 0. Місяць із ненульовим вводом не чіпаємо: там порожній F_ —
        // справжня відсутність (стара система: continue).
        var calculatedFound = found
            .Where(f => Hse400CompositionMapping.IsCalculated(f.Cell.RowKey))
            .Select(f => (f.Year, f.TableDefId, f.Cell.Month, Key: f.Cell.RowKey.ToUpperInvariant()))
            .ToHashSet();
        foreach (var ((year, tableDefId, month), allZero) in inputMonths)
        {
            if (!allZero)
            {
                continue;
            }

            foreach (var rowKey in rowDefs
                         .Where(r => r.TableDefId == tableDefId && Hse400CompositionMapping.IsCalculated(r.RowKeyValue))
                         .Select(r => r.RowKeyValue)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!calculatedFound.Contains((year, tableDefId, month, rowKey.ToUpperInvariant())))
                {
                    found.Add((year, tableDefId, OrdinalOf(tableDefId, rowKey),
                        new Hse400DocumentCell(year, month, streamByTable[tableDefId], rowKey, 0m)));
                }
            }
        }

        var rows = new List<Hse400CompositionRow>();
        foreach (var year in documentByYear.Keys.Order())
        {
            // Склад рядків — версії шаблону документа цього року: «показник існує» рахується за нею.
            var raw = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var instance in chosen.Where(i => i.PeriodKeyValue / 100 == year))
            {
                raw[streamByTable[instance.TableDefId]] = Hse400CompositionMapping.RawIndicators(
                    streamByTable[instance.TableDefId],
                    rowDefs.Where(r => r.TableDefId == instance.TableDefId).OrderBy(r => r.Ordinal).Select(r => r.RowKeyValue));
            }

            rows.AddRange(Hse400CompositionMapping.ToCompositionRows(
                found.Where(f => f.Year == year)
                    .OrderBy(f => f.TableDefId).ThenBy(f => f.Ordinal).ThenBy(f => f.Cell.Month)
                    .Select(f => f.Cell),
                raw));
        }

        IReadOnlyCollection<int> years = [.. documentByYear.Keys];
        if (cache is not null && fingerprint is not null)
        {
            cache.Set(CacheScope(), fingerprint, (rows, years));
        }

        return (rows, years);
    }

    /// <summary>Штамп рядків (<c>COUNT</c>, <c>MAX</c> і сума версій) для відбитка кешу.</summary>
    private sealed record RowStamp(long Cnt, byte[]? MaxVersion, decimal? SumVersion);

    /// <summary>Область кешу: сервер + база + шаблон + довідник (інша база чи зв'язка не ділять запису).</summary>
    private string CacheScope()
    {
        var connection = db.Database.GetDbConnection();
        return $"{connection.DataSource}|{connection.Database}|{templateCode}|{registryCode}";
    }
}
