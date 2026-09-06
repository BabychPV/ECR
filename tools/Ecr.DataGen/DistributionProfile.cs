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

    /// <summary>Рядків у контрольній таблиці гейта.</summary>
    /// <remarks>
    /// ⛔ 500×60 — це не частина профілю, а **сам критерій №1 BR-07**
    /// (`B02` §7: «<c>ReadSliceAsync</c> 500×60 &lt; 600 мс»). Хвіст профілю
    /// доходить лише до 471 рядка, тому в згенерованому обсязі зрізу 500×60 не
    /// існувало **жодного** — і замір №1 брав довільну таблицю на ~30×33, тобто
    /// в тридцять разів легшу за ту, під яку писався бюджет.
    ///
    /// ⚠ Таблиця додається ПОНАД <see cref="TablesPerDocument"/>, а не замість
    /// однієї з них: підмінити довільну означало б зіпсувати розподіл, знятий
    /// із чинного шаблону.
    /// </remarks>
    public int GateSliceRows { get; init; } = 500;

    /// <summary>Колонок у контрольній таблиці гейта.</summary>
    public int GateSliceColumns { get; init; } = 60;
}
