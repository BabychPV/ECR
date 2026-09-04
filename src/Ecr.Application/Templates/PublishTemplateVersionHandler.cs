using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Templates;

/// <summary>
/// Публікує версію шаблону. Це найважливіша операція конфігуратора: після неї
/// структура заморожена, а всі перевірки, які можна зробити наперед, уже
/// зроблені.
/// </summary>
/// <remarks>
/// Публікація або проходить цілком, або відхиляється з переліком проблем.
/// Часткова публікація неможлива за побудовою.
/// </remarks>
public sealed class PublishTemplateVersionHandler(
    IRepository<Domain.Entities.Configuration.TemplateVersion, int> versions,
    IFormulaEngine formulaEngine,
    IMetadataCache metadataCache,
    IAuditWriter audit,
    IUnitOfWork uow,
    IClock clock)
{
    /// <summary>Виконує публікацію.</summary>
    /// <param name="templateVersionId">Версія.</param>
    /// <param name="userId">Хто публікує.</param>
    /// <exception cref="Errors.BusinessRuleException">
    /// Валідація не пройдена; у <c>Details</c> — перелік діагностик.
    /// </exception>
    public Task PublishAsync(int templateVersionId, int userId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) завантажити версію з усіма аркушами, таблицями, колонками, рядками, формулами; " +
            "2) виконати ВСІ 12 перевірок із 02b-expressions.md#publish-checks — синтаксис, резолвінг " +
            "посилань, типи, ациклічність графа, розкриття діапазонів у списки RowKey, набір функцій " +
            "діалекту, сигнатури, СУМІСНІСТЬ ОДИНИЦЬ (ECR-TMPL-4223), предикати динамічних діапазонів; " +
            "3) зібрати всі діагностики і, якщо є хоч одна — кинути BusinessRuleException('ECR-TMPL-0422') " +
            "зі списком, НЕ зупиняючись на першій: користувач має побачити всі проблеми одразу; " +
            "4) зберегти FormulaDependency і EvaluationOrder; " +
            "5) version.Publish(userId, clock.UtcNow); " +
            "6) audit.WritePublicationEventAsync; 7) uow.SaveChangesAsync; " +
            "8) metadataCache.InvalidateAsync.");
}
