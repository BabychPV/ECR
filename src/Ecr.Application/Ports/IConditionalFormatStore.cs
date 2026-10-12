using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Ports;

/// <summary>Правила умовного форматування версії шаблону (<c>cfg.ConditionalFormatRule</c>, ФВ-2.6/2.7).</summary>
public interface IConditionalFormatStore
{
    /// <summary>Усі правила версії, у порядку колонка → порядок застосування.</summary>
    /// <param name="templateVersionId">Версія шаблону.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <param name="tableCode">
    /// L6: лише правила цієї таблиці та правила без таблиці (<c>TableCode IS NULL</c>); <c>null</c> — усі. Слайс
    /// таблиці не повинен тягнути тисячі правил чужих таблиць (<c>IX_CondFmt_Table</c>).
    /// </param>
    public Task<IReadOnlyList<ConditionalFormatRule>> GetAsync(
        int templateVersionId, CancellationToken ct, string? tableCode = null);

    /// <summary>
    /// Замінює весь набір правил версії: наявні видаляються, нові додаються.
    /// Без негайного запису — коміт робить <c>IUnitOfWork.SaveChangesAsync</c> обробника.
    /// </summary>
    public Task ReplaceAsync(
        int templateVersionId, IReadOnlyList<ConditionalFormatRule> rules, CancellationToken ct);
}