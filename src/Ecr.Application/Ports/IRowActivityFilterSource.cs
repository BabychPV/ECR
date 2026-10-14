// src/Ecr.Application/Ports/IRowActivityFilterSource.cs
namespace Ecr.Application.Ports;

/// <summary>
/// Фільтр активності рядків методології (D-425), розв'язаний до колонок і Id запису.
/// </summary>
/// <remarks>
/// ⛔ Окремий порт, а не метод <c>IMethodologyStore</c>: у цього порту десятки тестових реалізацій, а фільтр —
/// opt-in. Споживачі приймають його необов'язковим параметром (<c>null</c> — фільтра немає, поведінка до D-425),
/// як <c>IColumnPathMapper</c>.
/// </remarks>
public interface IRowActivityFilterSource
{
    /// <summary>Фільтр методології для таблиць; <c>null</c> — на методології прапорця немає.</summary>
    /// <param name="methodologyId">Методологія.</param>
    /// <param name="tableDefIds">Таблиці (описи таблиць тієї версії шаблону, яку читає документ).</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<RowActivityFilterResolution?> ResolveForMethodologyAsync(
        int methodologyId, IReadOnlyCollection<int> tableDefIds, CancellationToken ct);

    /// <summary>Те саме за версією методології.</summary>
    /// <param name="methodologyVersionId">Версія методології.</param>
    /// <param name="tableDefIds">Таблиці.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<RowActivityFilterResolution?> ResolveForVersionAsync(
        int methodologyVersionId, IReadOnlyCollection<int> tableDefIds, CancellationToken ct);
}

/// <summary>Розв'язаний фільтр: Id активного запису текстом і колонки статусу по таблицях.</summary>
/// <param name="ActiveEntryIdText">
/// Id активного запису довідника так, як його бачить предикат правила (<c>MethodologyRuleMatcher.Text</c>);
/// <c>null</c> — довідник або запис не знайдено, фільтр не діє.
/// </param>
/// <param name="StatusColumnsByTable">Таблиця → колонки з цим довідником; таблиці без колонок немає в словнику.</param>
public sealed record RowActivityFilterResolution(
    string? ActiveEntryIdText,
    IReadOnlyDictionary<int, IReadOnlyList<int>> StatusColumnsByTable)
{
    /// <summary>Колонки статусу таблиці; порожньо — фільтр на ній не діє (немає колонки статусу).</summary>
    /// <param name="tableDefId">Таблиця.</param>
    public IReadOnlyList<int> ColumnsOf(int tableDefId)
        => ActiveEntryIdText is not null && StatusColumnsByTable.TryGetValue(tableDefId, out var columns)
            ? columns
            : [];

    /// <summary>
    /// Чи проходить рядок (значення його комірок за <c>ColumnDefId</c>) фільтр на таблиці.
    /// </summary>
    /// <param name="tableDefId">Таблиця рядка.</param>
    /// <param name="valueOf">Текст значення комірки колонки; <c>null</c> — комірки немає.</param>
    /// <returns><c>true</c> — рядок рахується (також коли фільтр на таблиці не діє).</returns>
    public bool Admits(int tableDefId, Func<int, string?> valueOf)
    {
        ArgumentNullException.ThrowIfNull(valueOf);

        var columns = ColumnsOf(tableDefId);
        if (columns.Count == 0)
        {
            return true;
        }

        return columns.All(column => string.Equals(valueOf(column), ActiveEntryIdText, StringComparison.Ordinal));
    }
}
