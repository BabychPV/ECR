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
    IClock clock,
    Security.IAccessDecisionService access,
    Common.ICurrentUser currentUser,
    IAccessProfileInvalidator profileCache,
    Microsoft.Extensions.Logging.ILogger<CloneTemplateVersionHandler> log)
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
        // ⛔ Право перевіряється ТУТ (`A7-53`). До цього ендпоінт мав лише
        // `[Authorize]`, тобто оголошене контрактом право не перевіряв ніхто.
        //
        // ⛔ B-04: і ПЕРШИМ. `ArgumentException.ThrowIfNullOrWhiteSpace` стояв
        // перед ним і давав `500` на порожній номер будь-кому, ще до права.
        await Security.PermissionCheck
            .RequireAsync(access, currentUser, CreateTemplateVersionHandler.Permission, ct)
            .ConfigureAwait(false);

        CreateTemplateVersionHandler.RequireVersionNumber(newVersion);

        var versionId = await versions
            .CloneAsync(sourceVersionId, newVersion, userId, clock.UtcNow, ct)
            .ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        // ⛔ A1-04: клон скопіював гранти (Deny) на нові Id, а відбиток груп у ключі кешу профілів
        // бачить лише групи сесії — користувач із прямою роллю тримав би застарілий профіль без
        // заборон на колонки клону до 60 хв. Клон рідкісний, тож скидається весь кеш (fail-closed).
        Documents.VersionMigration.GrantProfileInvalidation.Run(
            profileCache, log, new Ports.GrantedUsers([], Overflow: true));

        return versionId;
    }
}
