using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Recalculation;

/// <summary>
/// Набір комірок, які треба перерахувати. Інкрементний перерахунок — не
/// оптимізація, а вимога: перераховувати весь документ на кожну зміну означає
/// вийти з бюджету на порядок.
/// </summary>
public sealed class DirtySet
{
    private readonly HashSet<CellAddress> _cells = [];

    /// <summary>Додає змінену комірку.</summary>
    public void Add(CellAddress address) => _cells.Add(address);

    /// <summary>Комірки, з яких починається розкриття графа.</summary>
    public IReadOnlyCollection<CellAddress> Seeds => _cells;

    /// <summary>Чи є що перераховувати.</summary>
    public bool IsEmpty => _cells.Count == 0;
}
