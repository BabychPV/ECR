// src/Ecr.Application/Recalculation/FormulaErrorTracker.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;

namespace Ecr.Application.Recalculation;

/// <summary>✎ C-1: комірка-джерело недовіри (власна помилка або її корінь для «не оновлено»).</summary>
/// <param name="PeriodKey">Період джерела.</param>
/// <param name="TableDefId">Таблиця джерела.</param>
/// <param name="RowKey">Рядок джерела.</param>
/// <param name="TableRowId">Ідентифікатор рядка джерела; <c>0</c> — невідомий (засів зі збереженого Blocked).</param>
/// <param name="ColumnDefId">Колонка джерела.</param>
/// <param name="ErrorCode">Код помилки джерела.</param>
public sealed record FormulaErrorSource(
    int PeriodKey, int TableDefId, string RowKey, long TableRowId, int ColumnDefId, string ErrorCode);

/// <summary>✎ C-1: підсумок обчислення однієї цілі прогону.</summary>
/// <param name="Key">Ключ комірки в контексті.</param>
/// <param name="Address">Адреса комірки.</param>
/// <param name="TableDefId">Таблиця.</param>
/// <param name="RowKey">Рядок.</param>
/// <param name="FormulaDefId">Формула.</param>
/// <param name="Kind"><c>null</c> — порахувалася; <see cref="FormulaError.ErrorKind"/> / <see cref="FormulaError.BlockedKind"/>.</param>
/// <param name="ErrorCode">Код помилки.</param>
/// <param name="Source">Джерело для Blocked.</param>
/// <param name="InputsJson">Входи помилки (Error).</param>
public sealed record FormulaCellOutcome(
    CellKey Key, CellAddress Address, int TableDefId, string RowKey, int FormulaDefId, string? Kind,
    string? ErrorCode, FormulaErrorSource? Source, string? InputsJson);

/// <summary>✎ C-1: комірка аркуша з помилкою — для блокування подання й «Перевірити».</summary>
/// <param name="TableDefId">Таблиця.</param>
/// <param name="SheetCopyId">Копія аркуша; <c>null</c> — оригінал.</param>
/// <param name="RowKey">Рядок.</param>
/// <param name="ColumnDefId">Колонка.</param>
/// <param name="Kind"><c>Error</c> або <c>Blocked</c>.</param>
/// <param name="ErrorCode">Код помилки.</param>
/// <param name="SourcePeriodKey">Період джерела (Blocked).</param>
/// <param name="SourceTableDefId">Таблиця джерела (Blocked).</param>
/// <param name="SourceRowKey">Рядок джерела (Blocked).</param>
/// <param name="SourceColumnDefId">Колонка джерела (Blocked).</param>
public sealed record FormulaErrorCell(
    int TableDefId, long? SheetCopyId, string RowKey, int ColumnDefId, string Kind, string ErrorCode,
    int? SourcePeriodKey, int? SourceTableDefId, string? SourceRowKey, int? SourceColumnDefId);

/// <summary>
/// ✎ C-1 (рішення PO 11.11, D-475): стан помилок ОДНОГО прогону. Комірка з помилкою та всі, що (транзитивно) її
/// читають, лежать у <see cref="Blocked"/>; контекст бачить читання такої комірки
/// (<see cref="SliceEvaluationContext.BlockedRead"/>), і ціль не перераховується.
/// </summary>
internal sealed class FormulaErrorTracker
{
    /// <summary>Скільки входів помилки зберігається (JSON у <c>calc.FormulaError.InputsJson</c>).</summary>
    public const int MaxInputs = 20;

    /// <summary>Недовірені комірки прогону (ключ контексту → корінь помилки).</summary>
    public Dictionary<CellKey, FormulaErrorSource> Blocked { get; } = [];

    /// <summary>Підсумок кожної обчисленої цілі поточного періоду.</summary>
    public Dictionary<CellAddress, FormulaCellOutcome> Outcomes { get; } = [];

    /// <summary>Засіває збережений стан (поточний період — <paramref name="offset"/> 0, сусідній — його зсув).</summary>
    /// <param name="rows">Збережені рядки стану.</param>
    /// <param name="offset">Зсув періоду рядків відносно прогону.</param>
    public void Seed(IEnumerable<FormulaErrorRow> rows, int offset)
    {
        foreach (var row in rows)
        {
            var source = row.SourceRowKey is { } sourceRow && row.SourceTableDefId is { } sourceTable
                         && row.SourceColumnDefId is { } sourceColumn && row.SourcePeriodKey is { } sourcePeriod
                ? new FormulaErrorSource(sourcePeriod, sourceTable, sourceRow, 0, sourceColumn, row.ErrorCode)
                : new FormulaErrorSource(row.PeriodKey, row.TableDefId, row.RowKey, row.TableRowId, row.ColumnDefId, row.ErrorCode);
            Blocked[new CellKey(offset, row.TableDefId, row.RowKey, row.ColumnDefId)] = source;
        }
    }

    /// <summary>Ціль порахувалася (або це ручне значення) — її стан помилки знімається.</summary>
    public void Ok(CellKey key, CellAddress address, int tableDefId, string rowKey, int formulaDefId)
        => Outcomes[address] = new FormulaCellOutcome(key, address, tableDefId, rowKey, formulaDefId, null, null, null, null);

    /// <summary>Власна помилка формули цілі.</summary>
    public void Fail(CellKey key, CellAddress address, int tableDefId, string rowKey, int formulaDefId, string errorCode, string? inputsJson)
    {
        // Код — у межах колонки сховища: інакше збережений (обрізаний) і поточний «різнилися» б на кожному прогоні.
        errorCode = errorCode.Length > FormulaError.MaxErrorCodeLength ? errorCode[..FormulaError.MaxErrorCodeLength] : errorCode;
        Blocked[key] = new FormulaErrorSource(address.PeriodKey.Value, tableDefId, rowKey, address.TableRowId, address.ColumnDefId, errorCode);
        Outcomes[address] = new FormulaCellOutcome(
            key, address, tableDefId, rowKey, formulaDefId, FormulaError.ErrorKind, errorCode, null, inputsJson);
    }

    /// <summary>Ціль прочитала недовірену комірку — не оновлюється.</summary>
    public void Block(CellKey key, CellAddress address, int tableDefId, string rowKey, int formulaDefId, FormulaErrorSource source)
    {
        Blocked[key] = source;
        Outcomes[address] = new FormulaCellOutcome(
            key, address, tableDefId, rowKey, formulaDefId, FormulaError.BlockedKind, source.ErrorCode, source, null);
    }
}

/// <summary>✎ C-1: вхідне значення помилки — запис JSON <c>calc.FormulaError.InputsJson</c>.</summary>
/// <param name="TableDefId">Таблиця.</param>
/// <param name="TableCode">Код таблиці.</param>
/// <param name="RowKey">Рядок.</param>
/// <param name="ColumnDefId">Колонка.</param>
/// <param name="ColumnCode">Код колонки.</param>
/// <param name="PeriodOffset">Зсув періоду (0 — поточний).</param>
/// <param name="Value">Значення текстом; <c>null</c> — порожньо.</param>
internal sealed record FormulaErrorInputRecord(
    int TableDefId, string? TableCode, string RowKey, int ColumnDefId, string? ColumnCode, int PeriodOffset, string? Value);

/// <summary>✎ C-1: входи помилки ↔ JSON (до <see cref="FormulaErrorTracker.MaxInputs"/> записів, ≤ 2000 символів).</summary>
internal static class FormulaErrorInputs
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>Прочитані комірки → JSON; порожньо — <c>null</c>.</summary>
    public static string? Format(
        IReadOnlyList<(CellKey Key, ExpressionValue Value)> reads, IReadOnlyDictionary<int, TableDef> tables)
    {
        if (reads.Count == 0)
        {
            return null;
        }

        var items = reads
            .DistinctBy(r => r.Key)
            .Select(r =>
            {
                tables.TryGetValue(r.Key.TableDefId, out var table);
                return new FormulaErrorInputRecord(
                    r.Key.TableDefId,
                    table?.Code,
                    r.Key.RowKey,
                    r.Key.ColumnDefId,
                    table?.Columns.FirstOrDefault(c => c.Id == r.Key.ColumnDefId)?.Code,
                    r.Key.PeriodOffset,
                    r.Value.IsNull ? null
                    : r.Value.IsError ? r.Value.ErrorCode
                    : Text(r.Value.Value));
            })
            .ToList();

        var json = JsonSerializer.Serialize(items, Web);
        while (json.Length > FormulaError.MaxInputsLength && items.Count > 1)
        {
            items.RemoveAt(items.Count - 1);
            json = JsonSerializer.Serialize(items, Web);
        }

        return json.Length <= FormulaError.MaxInputsLength ? json : null;
    }

    /// <summary>
    /// Значення входу текстом (інваріантна культура). ⚠ Число зі сховища має масштаб 16 (<c>decimal(34,16)</c>) —
    /// без хвостових нулів («10», а не «10.0000000000000000»): так його бачить людина в сітці.
    /// </summary>
    /// <param name="value">Значення виразу.</param>
    internal static string? Text(object? value)
        => value is decimal number
            ? number.ToString("G29", CultureInfo.InvariantCulture)
            : Convert.ToString(value, CultureInfo.InvariantCulture);

    /// <summary>JSON → записи; <c>null</c> чи битий JSON — порожньо.</summary>
    public static IReadOnlyList<FormulaErrorInputRecord> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<FormulaErrorInputRecord>>(json, Web) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>✎ C-1: текст <c>aud.CellChange.Reason</c> події помилки розрахунку (≤ 1000 символів).</summary>
internal static class FormulaErrorAuditReason
{
    /// <summary>Межа <c>aud.CellChange.Reason</c>.</summary>
    public const int MaxLength = 1000;

    /// <summary>Поява — «код · колонка=значення; …»; усунення — колишній код.</summary>
    public static string Of(FormulaErrorRow row, bool resolved)
    {
        if (resolved)
        {
            return row.ErrorCode;
        }

        var inputs = FormulaErrorInputs.Parse(row.InputsJson);
        var text = inputs.Count == 0
            ? row.ErrorCode
            : row.ErrorCode + " · " + string.Join("; ", inputs.Select(i => $"{i.ColumnCode ?? i.ColumnDefId.ToString(CultureInfo.InvariantCulture)}={i.Value ?? "∅"}"));
        return text.Length > MaxLength ? text[..MaxLength] : text;
    }
}

/// <summary>✎ C-1: що змінити в calc.FormulaError і журналі за підсумками прогону.</summary>
internal sealed class FormulaErrorDelta
{
    public static FormulaErrorDelta Empty { get; } = new([], [], [], []);

    private FormulaErrorDelta(
        List<FormulaErrorRow> upserts, List<FormulaErrorRow> deletes,
        List<(FormulaErrorRow Row, bool Resolved)> events, HashSet<long> instances)
    {
        Upserts = upserts;
        Deletes = deletes;
        Events = events;
        Instances = instances;
    }

    /// <summary>Нові стани комірок.</summary>
    public List<FormulaErrorRow> Upserts { get; }

    /// <summary>Рядки, що зникають (з повним рядком — для журналу).</summary>
    public List<FormulaErrorRow> Deletes { get; }

    /// <summary>Події журналу: поява помилки (<c>Resolved = false</c>) і усунення (<c>true</c>, рядок — колишній стан).</summary>
    public List<(FormulaErrorRow Row, bool Resolved)> Events { get; }

    /// <summary>Екземпляри, яких торкається зміна стану.</summary>
    public HashSet<long> Instances { get; }

    /// <summary>Змін стану немає.</summary>
    public bool IsEmpty => Upserts.Count == 0 && Deletes.Count == 0;

    /// <summary>Різниця «підсумок прогону − збережений стан».</summary>
    /// <param name="tracker">Підсумок прогону.</param>
    /// <param name="persisted">Збережений стан екземплярів прогону (поточний період).</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="instanceByTable">Екземпляр «виду» прогону для таблиці.</param>
    /// <param name="instances">Екземпляри «виду».</param>
    /// <param name="fullRun">Повний прогін: невідвідані комірки формул-цілей застаріли.</param>
    /// <param name="targetFormulas">Цілі прогону.</param>
    /// <param name="liveFormulas">Живі формули версії.</param>
    /// <param name="now">Мить прогону.</param>
    public static FormulaErrorDelta Build(
        FormulaErrorTracker tracker, IReadOnlyList<FormulaErrorRow> persisted, long documentId,
        IReadOnlyDictionary<int, long> instanceByTable, IReadOnlyList<TableInstanceRef> instances,
        bool fullRun, IReadOnlySet<int> targetFormulas, IReadOnlySet<int> liveFormulas, DateTime now)
    {
        var byAddress = new Dictionary<CellAddress, FormulaErrorRow>();
        foreach (var row in persisted)
        {
            byAddress[row.Address] = row;
        }

        var upserts = new List<FormulaErrorRow>();
        var deletes = new List<FormulaErrorRow>();
        var events = new List<(FormulaErrorRow, bool)>();

        foreach (var outcome in tracker.Outcomes.Values)
        {
            byAddress.TryGetValue(outcome.Address, out var was);
            if (outcome.Kind is null)
            {
                if (was is null)
                {
                    continue;
                }

                deletes.Add(was);
                if (was.Kind == FormulaError.ErrorKind)
                {
                    events.Add((was, true));
                }

                continue;
            }

            if (!instanceByTable.TryGetValue(outcome.TableDefId, out var instanceId))
            {
                continue;
            }

            var same = was is not null && was.Kind == outcome.Kind && was.ErrorCode == outcome.ErrorCode
                       && was.SourcePeriodKey == outcome.Source?.PeriodKey
                       && was.SourceTableDefId == outcome.Source?.TableDefId
                       && was.SourceRowKey == outcome.Source?.RowKey
                       && was.SourceColumnDefId == outcome.Source?.ColumnDefId;
            if (same && was!.InputsJson == outcome.InputsJson)
            {
                continue;   // ⛔ та сама помилка триває — ні запису, ні події (рішення PO п. 4)
            }

            var copy = instances.FirstOrDefault(i => i.TableInstanceId == instanceId)?.SheetCopyId;
            var row = new FormulaErrorRow(
                documentId, outcome.Address.PeriodKey.Value, instanceId, outcome.TableDefId, copy,
                outcome.Address.TableRowId, outcome.RowKey, outcome.Address.ColumnDefId, outcome.FormulaDefId,
                outcome.Kind, outcome.ErrorCode!, outcome.InputsJson,
                outcome.Source?.PeriodKey, outcome.Source?.TableDefId, outcome.Source?.RowKey, outcome.Source?.ColumnDefId,
                same ? was!.Since : now);
            upserts.Add(row);

            // Та сама помилка з іншими входами — лише оновлення входів, без події (без дублів у журналі).
            if (same)
            {
                continue;
            }

            if (outcome.Kind == FormulaError.ErrorKind)
            {
                events.Add((row, false));
            }
            else if (was?.Kind == FormulaError.ErrorKind)
            {
                events.Add((was, true));
            }
        }

        // Повний прогін: рядки формул, які він рахував (або яких уже немає), але комірок яких не відвідав (рядок
        // видалено, ціль змінилась після переносу версії) — застарілі.
        if (fullRun)
        {
            foreach (var stale in persisted.Where(p => !tracker.Outcomes.ContainsKey(p.Address)
                                                        && (targetFormulas.Contains(p.FormulaDefId) || !liveFormulas.Contains(p.FormulaDefId))))
            {
                deletes.Add(stale);
                if (stale.Kind == FormulaError.ErrorKind)
                {
                    events.Add((stale, true));
                }
            }
        }

        var touched = upserts.Select(u => u.TableInstanceId).Concat(deletes.Select(d => d.TableInstanceId)).ToHashSet();
        return new FormulaErrorDelta(upserts, deletes, events, touched);
    }
}
