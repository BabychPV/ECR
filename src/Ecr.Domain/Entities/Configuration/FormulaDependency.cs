using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// Розкрита залежність формули. Діапазони рядків матеріалізуються в явний
/// список <c>RowKey</c> на момент <c>Publish</c> — **у рантаймі діапазонів
/// не існує** (B03 §4).
/// </summary>
/// <remarks>
/// Саме тому зміна <c>Ordinal</c> після публікації не змінює результат:
/// формула вже посилається на конкретні рядки, а не на позиції.
/// </remarks>
public sealed class FormulaDependency : Entity<long>
{
    private FormulaDependency() { }

    private FormulaDependency(byte sourceKind, byte dependsOnKind, int sortOrder)
    {
        SourceKind = sourceKind;
        DependsOnKind = dependsOnKind;
        SortOrder = sortOrder;
    }

    /// <summary>Залежність ФОРМУЛИ шаблону.</summary>
    /// <param name="formulaDefId">Формула, яка залежить.</param>
    /// <param name="dependsOnKind">Від чого: комірка, шапка, довідник, інший період.</param>
    /// <param name="tableDefId">Таблиця джерела.</param>
    /// <param name="rowKey"><b>Конкретний</b> рядок; <c>null</c> для предиката.</param>
    /// <param name="columnDefId">Колонка джерела.</param>
    /// <param name="filterJson">Предикат для <c>RowMode = Dynamic</c>.</param>
    /// <param name="periodOffset">Зсув періоду: <c>[Period:-1]</c> → −1.</param>
    /// <param name="sortOrder">Позиція в розкритому діапазоні.</param>
    /// <remarks>
    /// ⛔ Конструктор приймав ЛИШЕ вид і порядок, а решта полів не мала
    /// сетерів узагалі — сутність неможливо було створити з її власними
    /// даними. Тобто таблиця <c>cfg.FormulaDependency</c> не наповнювалася б,
    /// навіть якби хтось спробував: граф залежностей лишався порожній, а
    /// каскадний перерахунок не бачив похідних комірок (<c>A7-63</c>).
    /// </remarks>
    public static FormulaDependency ForFormula(
        int formulaDefId,
        byte dependsOnKind,
        int? tableDefId,
        string? rowKey,
        int? columnDefId,
        string? filterJson,
        short? periodOffset,
        int sortOrder)
        => new(sourceKind: 0, dependsOnKind, sortOrder)
        {
            FormulaDefId = formulaDefId,
            TableDefId = tableDefId,
            RowKey = rowKey,
            ColumnDefId = columnDefId,
            FilterJson = filterJson,
            PeriodOffset = periodOffset,
        };

    /// <summary>Залежність ПРИВ'ЯЗКИ методології.</summary>
    /// <param name="bindingId">Прив'язка, яка залежить.</param>
    /// <param name="dependsOnKind">Від чого.</param>
    /// <param name="tableDefId">Таблиця джерела.</param>
    /// <param name="rowKey">Конкретний рядок.</param>
    /// <param name="columnDefId">Колонка джерела.</param>
    /// <param name="sortOrder">Позиція.</param>
    public static FormulaDependency ForBinding(
        int bindingId,
        byte dependsOnKind,
        int? tableDefId,
        string? rowKey,
        int? columnDefId,
        int sortOrder)
        => new(sourceKind: 1, dependsOnKind, sortOrder)
        {
            BindingId = bindingId,
            TableDefId = tableDefId,
            RowKey = rowKey,
            ColumnDefId = columnDefId,
        };

    /// <summary>0 — формула, 1 — прив'язка розрахунку.</summary>
    public byte SourceKind { get; private set; }

    public int? FormulaDefId { get; private init; }
    public int? BindingId { get; private init; }

    /// <summary>0 Cell, 1 Header, 2 Registry, 3 CrossPeriod, 4 CrossProject.</summary>
    public byte DependsOnKind { get; private set; }

    public int? TableDefId { get; private init; }

    /// <summary>**Конкретний** рядок, не діапазон.</summary>
    public string? RowKey { get; private init; }

    public int? ColumnDefId { get; private init; }

    /// <summary>Предикат для <c>RowMode = Dynamic</c>: списку рядків наперед не існує.</summary>
    public string? FilterJson { get; private init; }

    /// <summary>Зсув періоду: <c>[Period:-1]</c> → −1.</summary>
    public short? PeriodOffset { get; private init; }

    /// <summary>Позиція в розкритому діапазоні.</summary>
    public int SortOrder { get; private set; }
}
