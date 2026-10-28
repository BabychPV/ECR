// src/Ecr.Application/Calculations/CalculatedCellOverlay.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Ecr.Application.Calculations;

/// <summary>
/// Накладає результати методологій на комірки колонок <c>Calculated</c> — у
/// зрізі сітки й в експорті (F-02, четвертий раунд UX).
/// </summary>
/// <remarks>
/// ⛔ Рішення за доками (<c>D-69</c>, <c>03-glossary.md</c> §4): результат
/// методології живе в <c>calc.CalculationResult</c> і в документ приходить
/// <b>посиланням</b> через <c>ColumnDef.DataType = Calculated</c> +
/// <c>cfg.CalculationBinding</c>, а не копією в <c>doc.CellValue</c>. Доти
/// посилання не розв'язував НІХТО: панель «Calculation results» показувала
/// R1 = 20, а колонка <c>EMISSION</c> у сітці, у xlsx і в json лишалася
/// порожньою. Тепер розв'язання — тут, одне на всі три шляхи читання, і
/// комірки з'являються в зрізі тим самим <see cref="CellRecord"/>, що й
/// введені: клієнт сітки малює колонки зі структури, а значення — зі зрізу.
///
/// ⚠ Правило зіставлення, одне на всі шляхи:
/// <list type="bullet">
/// <item>значення — числа <b>актуального</b> прогону документа й періоду
/// (<see cref="ICalculationResultStore.ReadCurrentAsync"/>);</item>
/// <item>рядок — за <c>RowKey</c> (<c>D4-26</c>: ключ переживає перестворення
/// рядка);</item>
/// <item>вихід — <c>OutputCode</c> прив'язки, версія — будь-яка версія
/// методології прив'язки (стара версія, чинна в цьому періоді, теж її);</item>
/// <item>предикат прив'язки (<c>MatchJson</c>) звужує рядки, що отримують
/// число, — тим самим <see cref="MethodologyRuleMatcher"/>, що й правила відбору
/// (F-09: доти предикат не діяв ніде);</item>
/// <item>кілька речовин одного виходу в одному рядку — сума: одиниця виходу
/// одна на всі речовини (<c>MethodologyOutput.UnitId</c>), а комірка одна.</item>
/// </list>
///
/// ⚠ Таблиця без колонки <c>Calculated</c> не коштує ЖОДНОГО запиту: зріз —
/// найгарячіше читання системи (бюджет 1.5 с), і більшість таблиць методологій
/// не мають.
/// </remarks>
public sealed partial class CalculatedCellOverlay(
    IMethodologyStore methodologies,
    ICalculationResultStore results,
    ILogger<CalculatedCellOverlay>? log = null)
{
    /// <summary>Скільки прикладів неоднозначних комірок потрапляє в один запис журналу.</summary>
    private const int AmbiguousSamples = 5;

    /// <summary>Накладає результати на комірки кількох екземплярів таблиць одного документа й періоду.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="tables">Екземпляри з їхніми рядками, комірками й обчислюваними колонками.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Комірки кожного екземпляра — введені плюс накладені результати.</returns>
    public async Task<IReadOnlyDictionary<long, IReadOnlyList<CellRecord>>> ApplyAsync(
        long documentId,
        int periodKey,
        IReadOnlyList<OverlayTable> tables,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tables);

        var output = tables.ToDictionary(t => t.TableInstanceId, t => t.Cells);

        var withCalculated = tables.Where(t => t.CalculatedColumnIds.Count > 0).ToList();
        if (withCalculated.Count == 0)
        {
            return output;
        }

        var bindings = await methodologies
            .GetColumnResultBindingsAsync([.. withCalculated.Select(t => t.TableDefId).Distinct()], ct)
            .ConfigureAwait(false);

        if (bindings.Count == 0)
        {
            return output;
        }

        // Q-17: вибирати з бази лише те, що може дістатися комірок цих таблиць (код виходу, версія методології,
        // екземпляр). Порт без звуження (тест-подвійник) читається повністю, як і раніше.
        IReadOnlyList<CalculationResultRow> current;
        if (results is ICurrentResultFilterReader narrow)
        {
            var wanted = withCalculated
                .SelectMany(t => t.CalculatedColumnIds.Select(c => (t.TableDefId, ColumnDefId: c)))
                .ToHashSet();
            var used = bindings.Where(b => wanted.Contains((b.TableDefId, b.ColumnDefId))).ToList();

            current = await narrow
                .ReadCurrentMatchingAsync(
                    documentId,
                    periodKey,
                    new CurrentResultFilter(
                        [.. withCalculated.Select(t => t.TableInstanceId).Distinct()],
                        [.. used.Select(b => b.OutputCode).Distinct(StringComparer.OrdinalIgnoreCase)],
                        [.. used.SelectMany(b => b.VersionIds).Distinct()]),
                    ct)
                .ConfigureAwait(false);
        }
        else
        {
            current = await results.ReadCurrentAsync(documentId, periodKey, ct).ConfigureAwait(false);
        }

        if (current.Count == 0)
        {
            return output;
        }

        // Рядок × вихід × версія → значення; речовини одного виходу сумуються нижче.
        var byRow = current
            .Where(r => r.SourceRowKey is not null)
            .ToLookup(r => (Row: r.SourceRowKey!, Output: r.OutputCode.ToUpperInvariant()));

        var key = new PeriodKey(periodKey);
        var ambiguous = 0;
        var samples = new List<string>(AmbiguousSamples);

        foreach (var table in withCalculated)
        {
            var tableBindings = bindings
                .Where(b => b.TableDefId == table.TableDefId && table.CalculatedColumnIds.Contains(b.ColumnDefId))
                .ToList();

            if (tableBindings.Count == 0)
            {
                continue;
            }

            var cellsByRow = table.Cells
                .GroupBy(c => c.Address.TableRowId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var overlaid = new List<CellRecord>(table.Cells.Count);
            var bound = tableBindings.Select(b => b.ColumnDefId).ToHashSet();

            // ⚠ Введене значення в колонці методології — неможливий стан
            // (`ECR-CELL-4221` відхиляє ручний запис у `Calculated`), але якщо
            // воно є, правда тут одна — число методології.
            overlaid.AddRange(table.Cells.Where(c => !bound.Contains(c.Address.ColumnDefId)));

            foreach (var (rowKey, rowId) in table.RowIdsByKey)
            {
                var rowCells = cellsByRow.GetValueOrDefault(rowId) ?? [];
                IReadOnlyDictionary<string, string?>? matchValues = null;
                var filled = new HashSet<int>();

                // ⛔ P2-1: дві активні прив'язки на ОДНУ колонку (конфігураційна помилка
                // або законні взаємовиключні предикати) давали дві комірки з тією самою
                // адресою, і зріз падав на `ToDictionary` (500). Правило вибору, одне на
                // всі шляхи: комірку колонки дає прив'язка з НАЙБІЛЬШИМ `Id`, яка в цьому
                // рядку має число й збігається з предикатом (список прийшов за зростанням
                // `Id`, тож іде з кінця). Інші — не сумуються: сума різних виходів
                // у одній комірці була б вигаданим числом.
                foreach (var binding in Enumerable.Reverse(tableBindings))
                {
                    if (filled.Contains(binding.ColumnDefId))
                    {
                        continue;
                    }

                    // ⛔ D-401, правило відбору: A — результат ЦЬОГО екземпляра таблиці;
                    // B — без екземпляра (прогони до D-401) → тимчасове правило нижче;
                    // C — чужий екземпляр → ігноруємо. Є хоч одне A — B не береться: прогін
                    // з екземпляром точніший за застарілий.
                    var candidates = byRow[(rowKey, binding.OutputCode.ToUpperInvariant())]
                        .Where(r => binding.VersionIds.Contains(r.MethodologyVersionId))
                        .ToList();
                    var values = candidates.Where(r => r.TableInstanceId == table.TableInstanceId).ToList();
                    if (values.Count == 0)
                    {
                        values = [.. candidates.Where(r => r.TableInstanceId is null)];
                    }

                    if (values.Count == 0)
                    {
                        continue;
                    }

                    if (!IsCatchAll(binding.MatchJson))
                    {
                        matchValues ??= rowCells.ToDictionary(
                            c => c.Address.ColumnDefId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            c => MethodologyRuleMatcher.Text(c.Value),
                            StringComparer.Ordinal);

                        if (!MethodologyRuleMatcher.Matches(binding.MatchJson, matchValues))
                        {
                            continue;
                        }
                    }

                    filled.Add(binding.ColumnDefId);

                    // ⛔ D-401 (тимчасовий захід до `TableInstanceId` в результаті): `RowKey`
                    // унікальний лише в межах екземпляра таблиці, а результат ключується без
                    // таблиці — рядки двох таблиць з однаковим ключем потрапляли в одну
                    // комірку й сумувалися. Для ОДНІЄЇ речовини в рядку законне значення
                    // рівно одне: збіг — одне значення (не подвоєне), розбіжність — комірку
                    // не формуємо (хибне число гірше за відсутнє) і пишемо попередження.
                    if (!TryCombine(values, out var total, out var unitId))
                    {
                        ambiguous++;
                        if (samples.Count < AmbiguousSamples)
                        {
                            samples.Add($"{rowKey}/{binding.OutputCode}");
                        }

                        continue;
                    }

                    overlaid.Add(new CellRecord(
                        new CellAddress(key, rowId, binding.ColumnDefId),
                        table.TableDefId,
                        new CellValueData
                        {
                            ValueNumeric = total,
                            ValueUnitId = unitId,
                            IsCalculated = true,
                        }));
                }
            }

            output[table.TableInstanceId] = overlaid;
        }

        if (ambiguous > 0)
        {
            if (log is not null)
            {
                LogAmbiguous(log, documentId, periodKey, ambiguous, string.Join(", ", samples));
            }
        }

        return output;
    }

    /// <summary>
    /// Зводить значення комірки: різні речовини сумуються (одиниця виходу одна), а кілька
    /// різних значень ОДНІЄЇ речовини — колізія рядків двох таблиць, тоді <c>false</c>.
    /// </summary>
    private static bool TryCombine(List<CalculationResultRow> values, out decimal total, out int unitId)
    {
        total = 0m;
        unitId = values[0].UnitId;

        foreach (var group in values.GroupBy(v => v.SubstanceEntryId))
        {
            var first = group.First();
            if (group.Any(v => v.Value != first.Value || v.UnitId != first.UnitId))
            {
                return false;
            }

            total += first.Value;
        }

        return true;
    }

    /// <summary>Те саме для всього документа — шлях експорту (xlsx, csv, json).</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="snapshot">Структура версії: звідси — які колонки <c>Calculated</c>.</param>
    /// <param name="instances">Екземпляри таблиць документа за період.</param>
    /// <param name="rowIds">Рядки кожного екземпляра.</param>
    /// <param name="slices">Комірки кожного екземпляра, прочитані зі сховища.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Комірки кожного екземпляра разом із накладеними результатами.</returns>
    public Task<IReadOnlyDictionary<long, IReadOnlyList<CellRecord>>> ApplyAsync(
        long documentId,
        int periodKey,
        Domain.Entities.Configuration.TemplateVersionSnapshot snapshot,
        IReadOnlyList<TableInstanceRef> instances,
        IReadOnlyDictionary<long, IReadOnlyDictionary<string, long>> rowIds,
        IReadOnlyDictionary<long, IReadOnlyList<CellRecord>> slices,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(rowIds);
        ArgumentNullException.ThrowIfNull(slices);

        var calculatedByTable = snapshot.ColumnsById.Values
            .Where(c => !c.IsDeleted && c.DataType == Domain.Enums.CellDataType.Calculated)
            .GroupBy(c => c.TableDefId)
            .ToDictionary(g => g.Key, g => (IReadOnlySet<int>)g.Select(c => c.Id).ToHashSet());

        IReadOnlySet<int> none = new HashSet<int>();
        IReadOnlyDictionary<string, long> noRows = new Dictionary<string, long>(StringComparer.Ordinal);

        return ApplyAsync(
            documentId,
            periodKey,
            [.. instances.Select(i => new OverlayTable(
                i.TableInstanceId,
                i.TableDefId,
                rowIds.GetValueOrDefault(i.TableInstanceId) ?? noRows,
                slices.GetValueOrDefault(i.TableInstanceId) ?? [],
                calculatedByTable.GetValueOrDefault(i.TableDefId) ?? none))],
            ct);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Неоднозначні результати розрахунку не накладено (D-401): документ {DocumentId}, період {PeriodKey}, комірок {Count}, приклади (рядок/вихід): {Samples}")]
    private static partial void LogAmbiguous(ILogger logger, long documentId, int periodKey, int count, string samples);

    /// <summary>Чи предикат — «уся таблиця» (<c>{}</c>): тоді значення рядка не потрібні.</summary>
    private static bool IsCatchAll(string matchJson)
        => string.Equals(matchJson.Replace(" ", string.Empty, StringComparison.Ordinal), "{}", StringComparison.Ordinal);
}

/// <summary>Один екземпляр таблиці для <see cref="CalculatedCellOverlay"/>.</summary>
/// <param name="TableInstanceId">Екземпляр.</param>
/// <param name="TableDefId">Опис таблиці.</param>
/// <param name="RowIdsByKey">Рядки екземпляра: <c>RowKey</c> → <c>TableRowId</c>.</param>
/// <param name="Cells">Комірки, прочитані зі сховища.</param>
/// <param name="CalculatedColumnIds">Не видалені колонки типу <c>Calculated</c>.</param>
public sealed record OverlayTable(
    long TableInstanceId,
    int TableDefId,
    IReadOnlyDictionary<string, long> RowIdsByKey,
    IReadOnlyList<CellRecord> Cells,
    IReadOnlySet<int> CalculatedColumnIds);
