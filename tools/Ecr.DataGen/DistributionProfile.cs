namespace Ecr.DataGen;

/// <summary>
/// Профіль розподілу, знятий із чинного шаблону.
/// </summary>
/// <remarks>
/// Числа не вигадані: вони походять із аналізу реального `.xlsm`
/// (`docs/01-as-is-overview.md`). Змінювати їх без нового аналізу означає
/// міряти не ту систему.
/// </remarks>
public sealed record DistributionProfile
{
    /// <summary>Таблиць на документ.</summary>
    public int TablesPerDocument { get; init; } = 90;

    /// <summary>Медіана рядків у таблиці.</summary>
    public int MedianRowsPerTable { get; init; } = 30;

    /// <summary>Максимум рядків (найважча таблиця чинного шаблону).</summary>
    public int MaxRowsPerTable { get; init; } = 471;

    /// <summary>Мінімум колонок.</summary>
    public int MinColumns { get; init; } = 7;

    /// <summary>Максимум колонок.</summary>
    public int MaxColumns { get; init; } = 60;

    /// <summary>Періодів у році.</summary>
    public int PeriodsPerYear { get; init; } = 12;

    /// <summary>Частка заповнених комірок, %.</summary>
    public int FillPercent { get; init; } = 90;
}
