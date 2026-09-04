using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Templates;

/// <summary>
/// Створює нову чернетку на основі опублікованої версії. **Єдиний спосіб**
/// внести структурну зміну після публікації (ФВ-7.1).
/// </summary>
public sealed class CloneTemplateVersionHandler(
    IRepository<Domain.Entities.Configuration.TemplateVersion, int> versions,
    IUnitOfWork uow,
    IClock clock)
{
    /// <summary>Клонує версію.</summary>
    /// <param name="sourceVersionId">Версія-джерело.</param>
    /// <param name="newVersion">Номер нової версії.</param>
    /// <param name="userId">Автор.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Ідентифікатор створеної чернетки.</returns>
    public Task<int> CloneAsync(int sourceVersionId, string newVersion, int userId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: глибоко скопіювати аркуші, таблиці, колонки, рядки, стилі, формули, " +
            "правила валідації, зв'язки таблиць і правила періодів; " +
            "ClonedFromVersionId = sourceVersionId; Status = Draft; PresentationRevision = 0; " +
            "нові Id призначає БД. Дані документів НЕ копіюються — вони лишаються на старій версії " +
            "до явної міграції (ФВ-7.7).");
}
