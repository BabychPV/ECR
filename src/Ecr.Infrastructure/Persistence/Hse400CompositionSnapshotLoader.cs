using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Expressions.Evaluation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Знімок довідників, у якому склад газу <c>LAND_HSE400_COMPOSITION</c> береться з документа HSE400, а не з
/// імпортованих рядків <c>dic.*</c> (рішення людини 11.11: довідник не дублює документ).
/// </summary>
/// <remarks>
/// Обгортка над <see cref="RegistrySnapshotLoader"/>: усі довідники читає він, а тут рядки складу за роки, що мають
/// документ HSE400, підмінюються значеннями документа (<see cref="Hse400CompositionMapping.Overlay"/>).
///
/// ⛔ Вимкнено (<see cref="EnabledKey"/> = <c>false</c>, умовчання) — жодного додаткового запиту й той самий знімок.
/// Запит до документів іде лише тоді, коли серед завантажених довідників справді є склад газу.
///
/// ⚠ Документ читається ПОТОЧНИМ, а не «станом на» момент прогону: комірки вводу — з <c>doc.CellValue</c>,
/// розрахункові рядки <c>F_&lt;X&gt;</c> — з актуального прогону (<see cref="ICalculationResultStore.ReadCurrentAsync"/>).
/// Перерахунок залежних документів Land після зміни HSE400 — окремий крок (3b).
///
/// ⚠ Якщо за рік є кілька документів шаблону, береться документ із найбільшим ідентифікатором (останній створений).
/// </remarks>
public sealed class Hse400CompositionSnapshotLoader(
    IRegistrySnapshotLoader inner,
    EcrDbContext db,
    ICalculationResultStore results,
    bool enabled,
    string registryCode = Hse400CompositionMapping.RegistryCode,
    string templateCode = Hse400CompositionMapping.TemplateCode) : IRegistrySnapshotLoader
{
    /// <summary>Ключ налаштування: <c>true</c> — Land читає склад газу з документа HSE400.</summary>
    public const string EnabledKey = "Calculations:Hse400CompositionFromDocument";

    /// <summary>Чи ввімкнено читання складу з документа.</summary>
    /// <param name="configuration">Конфігурація застосунку.</param>
    /// <returns><c>true</c> лише для явного <c>true</c>.</returns>
    public static bool IsEnabled(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return bool.TryParse(configuration[EnabledKey], out var value) && value;
    }

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
        if (!enabled
            || source is not RegistrySnapshotSource loaded
            || !loaded.Registries.Any(r => string.Equals(r.Code, registryCode, StringComparison.OrdinalIgnoreCase)))
        {
            return source;
        }

        var (rows, years) = await ReadDocumentsAsync(ct).ConfigureAwait(false);
        return Hse400CompositionMapping.Overlay(loaded, registryCode, rows, years);
    }

    private async Task<(IReadOnlyList<Hse400CompositionRow> Rows, IReadOnlyCollection<int> Years)> ReadDocumentsAsync(
        CancellationToken ct)
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
            var inputMonth = (year, instance.TableDefId, monthByColumn[cell.ColumnDefId]);
            inputMonths[inputMonth] = (!inputMonths.TryGetValue(inputMonth, out var allZero) || allZero) && cell.Value == 0m;
            found.Add((year, instance.TableDefId, OrdinalOf(instance.TableDefId, row.RowKeyValue), new Hse400DocumentCell(
                year, monthByColumn[cell.ColumnDefId], streamByTable[instance.TableDefId], row.RowKeyValue, cell.Value)));
        }

        // Розрахункові рядки F_<X>: числа актуального прогону, колонка місяця — у коді виходу.
        foreach (var document in chosen.Select(i => (i.DocumentId, i.PeriodKeyValue)).Distinct())
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

        return (rows, documentByYear.Keys.ToList());
    }
}
