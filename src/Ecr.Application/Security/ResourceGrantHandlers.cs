// src/Ecr.Application/Security/ResourceGrantHandlers.cs
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;

namespace Ecr.Application.Security;

/// <summary>
/// Ресурсний грант у вигляді, придатному для передавання.
/// </summary>
/// <param name="ResourceKind">Вид ресурсу: <c>Project</c>, <c>Sheet</c>, <c>Table</c>, <c>Column</c>.</param>
/// <param name="ResourceId">Ідентифікатор ресурсу.</param>
/// <param name="Level">Рівень: <c>Read</c>…<c>Manage</c>.</param>
/// <param name="IsDeny">Явна заборона; перекриває будь-який дозвіл (ФВ-6.6).</param>
public sealed record ResourceGrantDto(
    ResourceKind ResourceKind, int ResourceId, GrantLevel Level, bool IsDeny);

/// <summary>Перелік грантів ролі. Право <c>Security.ManageRoles</c>.</summary>
public sealed class ListResourceGrantsHandler(
    IUserStore users, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Право на читання й зміну грантів.</summary>
    public const string Permission = "Security.ManageRoles";

    /// <summary>Повертає гранти ролі.</summary>
    /// <param name="roleId">Роль.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<IReadOnlyList<ResourceGrantDto>> HandleAsync(int roleId, CancellationToken ct)
    {
        await RequireAsync(access, currentUser, ct).ConfigureAwait(false);

        return await users.ListGrantsAsync(roleId, ct).ConfigureAwait(false);
    }

    /// <summary>Перевіряє право поточного користувача.</summary>
    /// <param name="access">Служба рішень доступу.</param>
    /// <param name="currentUser">Поточний користувач.</param>
    /// <param name="ct">Токен скасування.</param>
    internal static async Task<int> RequireAsync(
        IAccessDecisionService access, ICurrentUser currentUser, CancellationToken ct)
    {
        var userId = currentUser.UserId
            ?? throw new AccessDeniedException("ECR-AUTH-0401", "Потрібна автентифікація.");

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);

        return profile.Has(Permission)
            ? userId
            : throw new AccessDeniedException("ECR-AUTH-0403", $"Потрібне право {Permission}.");
    }
}

/// <summary>
/// Заміна набору грантів ролі. Право <c>Security.ManageRoles</c>.
/// </summary>
/// <remarks>
/// ⛔ Обробник з'явився після `A7-22`. Таблиця <c>sec.ResourceGrant</c>,
/// сутність, правило «IsDeny виграє завжди» і фільтрація за грантами існували
/// від Етапу 3 — а <b>створити грант не міг ніхто</b>: ані ендпоінта, ані
/// обробника, ані рядка seed. Наслідок був повний і мовчазний: користувач із
/// усіма 38 правами бачив ПОРОЖНІЙ перелік проєктів, бо доступ до ресурсу
/// вимагає гранта, а гранта не існувало. Система збиралася, тести були зелені,
/// показати дані вона не могла нікому й ніколи.
///
/// ⚠ Набір замінюється ЦІЛКОМ, а не правиться по одному. Причина не в
/// зручності: гранти — це відповідь на питання «що покриває ця роль», і
/// відповідь має бути видима одним поглядом. Часткові правки дають стан, у
/// якому ніхто не скаже напевно, звідки саме взявся доступ, — а це рівно те,
/// проти чого написане ФВ-6.6.
/// </remarks>
public sealed class ReplaceResourceGrantsHandler(
    IUserStore users,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IAuditWriter audit,
    IUnitOfWork uow,
    IClock clock)
{
    /// <summary>Замінює набір грантів ролі.</summary>
    /// <param name="roleId">Роль.</param>
    /// <param name="grants">Новий набір; порожній — прибрати всі.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Ролі немає.</exception>
    /// <exception cref="BusinessRuleException">Дублікат ресурсу в наборі.</exception>
    public async Task HandleAsync(
        int roleId, IReadOnlyList<ResourceGrantDto> grants, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(grants);

        var actorId = await ListResourceGrantsHandler
            .RequireAsync(access, currentUser, ct)
            .ConfigureAwait(false);

        var role = (await users.ListRolesAsync(ct).ConfigureAwait(false))
            .FirstOrDefault(r => r.Id == roleId)
            ?? throw new NotFoundException("ECR-ROW-0404", $"Ролі {roleId} не існує.");

        // ⛔ Дублікат ловиться ТУТ, а не унікальним індексом: `UQ_ResourceGrant`
        // дав би 500 «внутрішня помилка» замість пояснення, який саме ресурс
        // названо двічі (той самий клас, що й `A7-19`).
        var duplicate = grants
            .GroupBy(g => (g.ResourceKind, g.ResourceId))
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicate is not null)
        {
            throw new BusinessRuleException(
                "ECR-ROW-0409",
                $"Ресурс {duplicate.Key.ResourceKind} {duplicate.Key.ResourceId} названо в наборі двічі.");
        }

        await users.ReplaceGrantsAsync(roleId, grants, ct).ConfigureAwait(false);

        // ⛔ Обов'язково і в тій самій транзакції. Профіль доступу кешується
        // під ключем «користувач + штамп» на 30 хвилин, і без прокрутки штампа
        // зміна не діяла б: виданий доступ не з'являвся, знятий не зникав
        // (`A7-23`). Другий випадок — це доступ, який адміністратор уже
        // вважає закритим.
        await users.RotateStampsForRoleAsync(roleId, ct).ConfigureAwait(false);

        // ⚠ Зміна доступу пишеться в журнал безпеки ЗАВЖДИ і повним набором:
        // «хто тепер це бачить» відновлюється лише так. Різницю не рахуємо —
        // попередній стан уже є в попередньому записі журналу.
        await audit.WriteSecurityEventAsync(
            new SecurityEventRecord(
                clock.UtcNow,
                "ResourceGrantsReplaced",
                TargetUserId: null,
                TargetRoleId: roleId,
                DetailsJson: JsonSerializer.Serialize(new { role = role.Code, grants }),
                ChangedByUserId: actorId,
                CorrelationId: null),
            ct).ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
