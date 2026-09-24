// src/Ecr.Application/Security/ResourceGrantHandlers.cs
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;

namespace Ecr.Application.Security;

/// <summary>
/// Ресурсний грант у вигляді, придатному для передавання.
/// </summary>
/// <param name="ResourceKind">Вид ресурсу: <c>Project</c>, <c>Sheet</c>, <c>Table</c>, <c>Column</c>.</param>
/// <param name="ResourceId">Ідентифікатор ресурсу.</param>
/// <param name="Level">Рівень: <c>Read</c>…<c>Manage</c>.</param>
/// <param name="IsDeny">Явна заборона; перекриває будь-який дозвіл (ФВ-6.6).</param>
/// <param name="ResourceName">
/// Розв'язаний код ресурсу (<c>Q-299</c>) — лише у ВІДПОВІДІ <see
/// cref="ListResourceGrantsHandler"/>; <c>null</c>, якщо ресурс уже видалено
/// або посилання «осиротіло». Поле ІГНОРУЄТЬСЯ на запис
/// (<see cref="ReplaceResourceGrantsHandler"/> і <c>IUserStore.ReplaceGrantsAsync</c>
/// читають лише перші чотири поля) — клієнту не треба вирізати його з
/// чернетки перед збереженням.
/// </param>
public sealed record ResourceGrantDto(
    ResourceKind ResourceKind, int ResourceId, GrantLevel Level, bool IsDeny,
    string? ResourceName = null);

/// <summary>Перелік грантів ролі. Право <c>Security.ManageRoles</c>.</summary>
/// <remarks>
/// ⛔ <c>Q-299</c>: `Sheet`/`Table`/`Column` адресуються лише в дереві
/// структури КОНКРЕТНОЇ версії шаблону (<c>GET
/// …/template-versions/{id}/structure</c>) — а перелік грантів ролі показує
/// ресурси БЕЗ версії. До цієї правки адміністратор бачив голий
/// <c>resourceId</c> і не мав жодного способу дізнатися, якому аркушу,
/// таблиці чи колонці він відповідає, не перебираючи вручну версії шаблонів.
/// Розв'язання можливе однозначно БЕЗ підказки версії, бо
/// <c>SheetDef.Id</c>/<c>TableDef.Id</c>/<c>ColumnDef.Id</c> — суцільні
/// IDENTITY-ключі таблиці (<c>TemplateStructureConfiguration.cs</c>:
/// <c>HasKey(x => x.Id)</c>), а не складові з <c>TemplateVersionId</c> —
/// той самий <c>Id</c> не повторюється у двох версіях одразу.
/// </remarks>
public sealed class ListResourceGrantsHandler(
    IUserStore users, IAccessDecisionService access, ICurrentUser currentUser,
    IResourceNameResolver nameResolver)
{
    /// <summary>Право на читання й зміну грантів.</summary>
    public const string Permission = "Security.ManageRoles";

    /// <summary>Повертає гранти ролі з розв'язаними назвами ресурсів.</summary>
    /// <param name="roleId">Роль.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<IReadOnlyList<ResourceGrantDto>> HandleAsync(int roleId, CancellationToken ct)
    {
        await RequireAsync(access, currentUser, ct).ConfigureAwait(false);

        var grants = await users.ListGrantsAsync(roleId, ct).ConfigureAwait(false);

        if (grants.Count == 0)
        {
            // ⛔ B-07: неіснуюча роль давала `200 []` — «грантів немає» на
            // адресі, якої не існує. Роль із грантами існує за побудовою, тож
            // питаємо лише тут.
            if ((await users.ListRolesAsync(ct).ConfigureAwait(false)).All(r => r.Id != roleId))
            {
                throw new NotFoundException(
                    ErrorCodes.SecurityPrincipalNotFound,
                    $"Ролі {roleId} не існує.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-SEC-0404.roleNotFound",
                        ["roleId"] = roleId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    });
            }

            return grants;
        }

        var names = await nameResolver
            .ResolveAsync([.. grants.Select(g => (g.ResourceKind, g.ResourceId))], ct)
            .ConfigureAwait(false);

        return [.. grants.Select(g => g with
        {
            ResourceName = names.GetValueOrDefault((g.ResourceKind, g.ResourceId)),
        })];
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
            : throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Потрібне право {Permission}.",
                new Dictionary<string, object?> { ["permission"] = Permission });
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
            // ⛔ Родина SEC, а не ROW (`P-25`, рядок 2): суб'єкт відмови —
            // запис каталогу безпеки, а `ROW` маршрутизує на клієнті в
            // обробник помилок рядка таблиці документа.
            ?? throw new NotFoundException(ErrorCodes.SecurityPrincipalNotFound, $"Ролі {roleId} не існує.");

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
