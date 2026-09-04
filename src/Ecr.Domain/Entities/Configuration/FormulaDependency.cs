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

    public FormulaDependency(byte sourceKind, byte dependsOnKind, int sortOrder)
    {
        SourceKind = sourceKind;
        DependsOnKind = dependsOnKind;
        SortOrder = sortOrder;
    }

    /// <summary>0 — формула, 1 — прив'язка розрахунку.</summary>
    public byte SourceKind { get; private set; }

    public int? FormulaDefId { get; private set; }
    public int? BindingId { get; private set; }

    /// <summary>0 Cell, 1 Header, 2 Registry, 3 CrossPeriod, 4 CrossProject.</summary>
    public byte DependsOnKind { get; private set; }

    public int? TableDefId { get; private set; }

    /// <summary>**Конкретний** рядок, не діапазон.</summary>
    public string? RowKey { get; private set; }

    public int? ColumnDefId { get; private set; }

    /// <summary>Предикат для <c>RowMode = Dynamic</c>: списку рядків наперед не існує.</summary>
    public string? FilterJson { get; private set; }

    /// <summary>Зсув періоду: <c>[Period:-1]</c> → −1.</summary>
    public short? PeriodOffset { get; private set; }

    /// <summary>Позиція в розкритому діапазоні.</summary>
    public int SortOrder { get; private set; }
}
