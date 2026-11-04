namespace Ecr.Application.Ports;

/// <summary>
/// Збережена мапа перейменування колонок між версіями шаблону (<c>cfg.ColumnRename</c>, RPT-STABLE Part B): псевдонім, за яким
/// міжперіодна формула на межі версій знаходить колонку, що змінила код.
/// </summary>
public interface IColumnRenameMap
{
    /// <summary>Пари «старa колонка → нова» і зворотні (читання скоупу: мапа мала, читається раз).</summary>
    /// <param name="ct">Токен скасування.</param>
    public Task<(IReadOnlyDictionary<int, int> Forward, IReadOnlyDictionary<int, int> Backward)> GetAsync(CancellationToken ct);
}
