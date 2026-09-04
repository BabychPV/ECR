// src/Ecr.Domain/ValueObjects/CellValueData.cs

using Ecr.Domain.Enums;

namespace Ecr.Domain.ValueObjects;

/// <summary>
/// Типізоване значення комірки. Рівно одне з полів <c>Value*</c> заповнене,
/// або жодне — тоді <c>IsEmpty = true</c> (явна порожнеча, R-B4).
/// </summary>
public sealed record CellValueData
{
    public string? ValueString { get; init; }
    public decimal? ValueNumeric { get; init; }
    public DateTime? ValueDate { get; init; }
    public bool? ValueBool { get; init; }
    public int? ValueRegistryEntryId { get; init; }

    /// <summary>Одиниця вимірювання; лише для <see cref="CellDataType.Unit"/> (R-A4).</summary>
    public int? ValueUnitId { get; init; }

    /// <summary>Значення обчислене формулою шаблону, а не введене користувачем.</summary>
    public bool IsCalculated { get; init; }

    /// <summary>Явна порожнеча: користувач свідомо лишив комірку порожньою.</summary>
    public bool IsEmpty { get; init; }

    /// <summary>Порожня комірка як явний стан.</summary>
    public static CellValueData Empty { get; } = new() { IsEmpty = true };

    /// <summary>Перевіряє, що заповнене рівно одне поле значення (або жодного при <c>IsEmpty</c>).</summary>
    public bool IsWellFormed()
    {
        var filled = 0;
        if (ValueString is not null)
        {
            filled++;
        }
        if (ValueNumeric is not null)
        {
            filled++;
        }
        if (ValueDate is not null)
        {
            filled++;
        }
        if (ValueBool is not null)
        {
            filled++;
        }
        if (ValueRegistryEntryId is not null)
        {
            filled++;
        }
        if (ValueUnitId is not null)
        {
            filled++;
        }
        return IsEmpty ? filled == 0 : filled == 1;
    }
}
