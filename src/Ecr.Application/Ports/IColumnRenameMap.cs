namespace Ecr.Application.Ports;

/// <summary>
/// Збережена мапа перейменування колонок між версіями шаблону (<c>cfg.ColumnRename</c>, RPT-STABLE Part B): псевдонім, за яким
/// міжперіодна формула на межі версій знаходить колонку, що змінила код. ✎ R-2: мапа несе й пари ВСІХ колонок таблиці,
/// чий код змінено мапою переносу (<c>tableRenames</c>), — за ними знаходиться таблиця-відповідник.
/// </summary>
public interface IColumnRenameMap
{
    /// <summary>Пари «старa колонка → нова» і зворотні (читання скоупу: мапа мала, читається раз на проєкт).</summary>
    /// <param name="documentId">
    /// ✎ B127: документ, для якого читається мапа, — лише пари, що застосував перенос ЙОГО проєкту (плюс застарілі
    /// глобальні, записані до B127). <c>null</c> — усі пари.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    public Task<(IReadOnlyDictionary<int, int> Forward, IReadOnlyDictionary<int, int> Backward)> GetAsync(
        long? documentId, CancellationToken ct);
}
