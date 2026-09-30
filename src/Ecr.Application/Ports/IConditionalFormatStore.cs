using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Ports;

/// <summary>Правила умовного форматування версії шаблону (<c>cfg.ConditionalFormatRule</c>, ФВ-2.6/2.7).</summary>
public interface IConditionalFormatStore
{
    /// <summary>Усі правила версії, у порядку колонка → порядок застосування.</summary>
    public Task<IReadOnlyList<ConditionalFormatRule>> GetAsync(int templateVersionId, CancellationToken ct);

    /// <summary>
    /// Замінює весь набір правил версії: наявні видаляються, нові додаються.
    /// Без негайного запису — коміт робить <c>IUnitOfWork.SaveChangesAsync</c> обробника.
    /// </summary>
    public Task ReplaceAsync(
        int templateVersionId, IReadOnlyList<ConditionalFormatRule> rules, CancellationToken ct);
}