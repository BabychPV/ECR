using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Services;

namespace Ecr.Application.Templates;

/// <summary>
/// Патчить презентаційний шар **опублікованої** версії «на льоту»: підписи,
/// стилі, <c>Ordinal</c>, формати (ФВ-7.2).
/// </summary>
/// <remarks>
/// Це та операція, заради якої існує <c>PresentationRevision</c>: користувач
/// може виправити підпис колонки без клонування версії і без міграції даних.
/// </remarks>
public sealed class PatchPresentationHandler(
    IRepository<Domain.Entities.Configuration.TemplateVersion, int> versions,
    ChangeClassifier classifier,
    IAuditWriter audit,
    IUnitOfWork uow,
    IClock clock)
{
    /// <summary>Застосовує презентаційні зміни.</summary>
    /// <param name="templateVersionId">Версія.</param>
    /// <param name="patchJson">Перелік змін у форматі <c>{entityType, entityId, field, value}</c>.</param>
    /// <param name="userId">Автор.</param>
    /// <returns>Нове значення <c>PresentationRevision</c>.</returns>
    /// <exception cref="Errors.BusinessRuleException">
    /// Серед змін є структурна — <c>ECR-TMPL-0409</c>.
    /// </exception>
    public Task<int> PatchAsync(int templateVersionId, string patchJson, int userId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) розібрати patchJson; " +
            "2) для КОЖНОЇ зміни викликати classifier.Classify і переконатися, що клас = Presentation; " +
            "   будь-що інше → BusinessRuleException('ECR-TMPL-0409') з переліком порушень; " +
            "3) застосувати зміни; " +
            "4) інкрементувати PresentationRevision ОДНИМ statement з OUTPUT (R-B7) — " +
            "   read-modify-write у застосунку заборонений, бо інстансів ≥2; " +
            "5) version.ApplyPresentationRevision(нове значення); " +
            "6) audit.WriteStructureChangeAsync з ChangeClass.Presentation.");
}
