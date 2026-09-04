using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Templates;

/// <summary>
/// Створює нову чернетку на основі опублікованої версії. **Єдиний спосіб**
/// внести структурну зміну після публікації (ФВ-7.1).
/// </summary>
public sealed class CloneTemplateVersionHandler(
    ITemplateVersionStore versions,
    IUnitOfWork uow,
    IClock clock)
{
    /// <summary>Клонує версію.</summary>
    /// <param name="sourceVersionId">Версія-джерело.</param>
    /// <param name="newVersion">Номер нової версії.</param>
    /// <param name="userId">Автор.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Ідентифікатор створеної чернетки.</returns>
    /// <remarks>
    /// Клонується вся структура: аркуші, таблиці, колонки, рядки, стилі,
    /// формули і правила валідації. <c>Code</c> і <c>RowKey</c> зберігаються —
    /// на них посилаються формули.
    ///
    /// ⛔ Дані документів **не** копіюються: вони лишаються на старій версії
    /// до явної міграції (ФВ-7.7). Скопійовані, вони перетворилися б на другу
    /// копію тих самих чисел, яку ніхто не оновлює.
    /// </remarks>
    public async Task<int> CloneAsync(int sourceVersionId, string newVersion, int userId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newVersion);

        var versionId = await versions
            .CloneAsync(sourceVersionId, newVersion, userId, clock.UtcNow, ct)
            .ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return versionId;
    }
}
