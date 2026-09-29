// src/Ecr.Application/Security/ResourceGrantHandlers.cs
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Microsoft.Extensions.Logging;

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

/// <summary>Проєкт у довіднику для видачі грантів: лише ідентичність.</summary>
/// <param name="Id">Ідентифікатор — те, що йде в <c>resourceId</c> гранта.</param>
/// <param name="Code">Код проєкту.</param>
/// <param name="NameL10n">Назва мовами каталогу.</param>
/// <remarks>
/// ⛔ Рішення людини 2026-09-29 (D-207 п.2, варіант B): адміністратор безпеки
/// без грантів на проєкти бачить КОД і НАЗВУ всіх проєктів — і нічого більше.
/// Стан, пояс, поточний період, власник — поза цим записом навмисно.
/// </remarks>
public sealed record GrantableProject(int Id, string Code, Ecr.Domain.ValueObjects.LocalizedText NameL10n);

/// <summary>Версія набору грантів ролі — для <c>ETag</c> / <c>If-Match</c>.</summary>
/// <remarks>
/// ⚠ Хеш НАБОРУ, а не лічильник: токена конкурентності в <c>sec.Role</c> і
/// <c>sec.ResourceGrant</c> немає, а міграція поза цією задачею (токен
/// міграцій черговий). Той самий прийом, що <c>HeaderVersion.Of</c> для шапки
/// документа (C2). Наслідок чесний: правка, що повернула рівно той самий
/// набір (A → B → A), конфліктом не вважається — затирати тут нічого.
///
/// ⚠ Хешуються лише чотири поля гранта: <c>ResourceName</c> — розв'язана
/// назва для показу, вона змінюється від перейменування ресурсу, а не від
/// правки грантів.
/// </remarks>
public static class ResourceGrantsVersion
{
    /// <summary>Хеш відсортованого набору грантів (hex, 32 символи).</summary>
    /// <param name="grants">Набір грантів ролі.</param>
    public static string Of(IEnumerable<ResourceGrantDto> grants)
    {
        ArgumentNullException.ThrowIfNull(grants);

        var text = new System.Text.StringBuilder();
        foreach (var grant in grants
                     .OrderBy(g => (int)g.ResourceKind)
                     .ThenBy(g => g.ResourceId)
                     .ThenBy(g => (int)g.Level)
                     .ThenBy(g => g.IsDeny))
        {
            text.Append(((int)grant.ResourceKind).ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append('|').Append(grant.ResourceId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append('|').Append(((int)grant.Level).ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append('|').Append(grant.IsDeny ? '1' : '0')
                .Append('\n');
        }

        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString()));
        return Convert.ToHexString(hash.AsSpan(0, 16));
    }
}

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

    /// <summary>
    /// Код і назва всіх проєктів — для вибору ресурсу гранта. Право
    /// <c>Security.ManageRoles</c> (глобальне, <c>PermissionScopes.Global</c>).
    /// </summary>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ D-207 п.2, варіант B. Доти адміністратор безпеки без грантів на
    /// проєкти бачив ПОРОЖНІЙ вибір проєкту (<c>GET /projects</c> фільтрує за
    /// грантами й вимагає <c>Document.View</c>) — і не міг видати грант на
    /// проєкт нікому, включно з собою. Перелік НЕ відкриває даних: зріз,
    /// документи й перелік документів проєкту лишаються за грантами.
    ///
    /// ⚠ Метод цього обробника, а не окремий обробник: той самий порт
    /// (<see cref="IResourceNameResolver"/>), те саме право і той самий екран —
    /// вибір ресурсу гранта.
    /// </remarks>
    public async Task<IReadOnlyList<GrantableProject>> ListProjectsAsync(CancellationToken ct)
    {
        await RequireAsync(access, currentUser, ct).ConfigureAwait(false);

        return await nameResolver.ListProjectsAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Перевіряє право поточного користувача.</summary>
    /// <param name="access">Служба рішень доступу.</param>
    /// <param name="currentUser">Поточний користувач.</param>
    /// <param name="ct">Токен скасування.</param>
    internal static async Task<int> RequireAsync(
        IAccessDecisionService access, ICurrentUser currentUser, CancellationToken ct)
    {
        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(
                "ECR-AUTH-0401", "Потрібна автентифікація.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);

        return profile.Has(Permission)
            ? userId
            : throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Потрібне право {Permission}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-AUTH-0403.permission",
                    ["permission"] = Permission,
                });
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
///
/// ⛔ Конкурентність (lost update). Доти два адміністратори, що відкрили
/// гранти однієї ролі, зберігали по черзі — і другий мовчки затирав набір
/// першого: заміна цілком не лишала від чужої правки нічого. Тепер клієнт
/// шле <c>If-Match</c> з версією набору (<see cref="ResourceGrantsVersion"/>,
/// <c>ETag</c> відповіді <c>GET</c>), і звірка йде ПІД <c>UPDLOCK</c> на рядку
/// <c>sec.Role</c> у тій самій транзакції, що й запис, штампи й аудит: дві
/// одночасні заміни з однієї версії — одна <c>204</c>, друга <c>409
/// ECR-SEC-0409</c>. Без блокування обидві проходять звірку до коміту одна
/// одної і пишуть обидві (об'єднання наборів — теж втрачена правка).
///
/// ⚠ Версія поки НЕОБОВ'ЯЗКОВА: відсутній <c>If-Match</c> — як раніше, із
/// записом Warning у журнал. Клієнт екрана грантів оновлюється в іншій
/// гілці (U6a), обов'язковість (<c>422</c>, як C2) вмикається разом із ним.
/// </remarks>
public sealed partial class ReplaceResourceGrantsHandler(
    IUserStore users,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IAuditWriter audit,
    IUnitOfWork uow,
    IClock clock,
    ILogger<ReplaceResourceGrantsHandler>? logger = null)
{
    /// <summary>Замінює набір грантів ролі без звірки версії.</summary>
    /// <param name="roleId">Роль.</param>
    /// <param name="grants">Новий набір; порожній — прибрати всі.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Версія щойно збереженого набору.</returns>
    public Task<string> HandleAsync(
        int roleId, IReadOnlyList<ResourceGrantDto> grants, CancellationToken ct)
        => HandleAsync(roleId, grants, ifMatch: null, ct);

    /// <summary>Замінює набір грантів ролі.</summary>
    /// <param name="roleId">Роль.</param>
    /// <param name="grants">Новий набір; порожній — прибрати всі.</param>
    /// <param name="ifMatch">
    /// Значення <c>If-Match</c> — версія, з якої почалася правка; <c>null</c>
    /// чи порожнє — без звірки (перехідний режим, див. remarks класу).
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Версія щойно збереженого набору (новий <c>ETag</c>).</returns>
    /// <exception cref="NotFoundException">Ролі немає.</exception>
    /// <exception cref="BusinessRuleException">Дублікат ресурсу в наборі.</exception>
    /// <exception cref="ConcurrencyConflictException">
    /// Версія застаріла — <c>ECR-SEC-0409</c>, актуальна у <c>details.version</c>.
    /// </exception>
    public async Task<string> HandleAsync(
        int roleId, IReadOnlyList<ResourceGrantDto> grants, string? ifMatch, CancellationToken ct)
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
            ?? throw new NotFoundException(
                ErrorCodes.SecurityPrincipalNotFound, $"Ролі {roleId} не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-SEC-0404.roleNotFound",
                    ["roleId"] = roleId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });

        // ⛔ Дублікат ловиться ТУТ, а не унікальним індексом: `UQ_ResourceGrant`
        // дав би 500 «внутрішня помилка» замість пояснення, який саме ресурс
        // названо двічі (той самий клас, що й `A7-19`).
        var duplicate = grants
            .GroupBy(g => (g.ResourceKind, g.ResourceId))
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicate is not null)
        {
            // ⛔ Родина SEC, а не ROW (`P-25`, рядок 3, той самий прецедент,
            // що й `UserDuplicate` вище в `ErrorCodes.cs`): суб'єкт конфлікту —
            // запис каталогу безпеки (роль/грант), а не рядок таблиці
            // документа. `ROW` тут раніше маршрутизував на клієнті в
            // обробник помилок сітки документа (`DocumentGrid.tsx`), де
            // сторінки грантів ролі немає взагалі.
            throw new BusinessRuleException(
                ErrorCodes.SecurityConflict,
                $"Ресурс {duplicate.Key.ResourceKind} {duplicate.Key.ResourceId} названо в наборі двічі.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-SEC-0409.resourceGrantDuplicate",
                    ["resourceKind"] = duplicate.Key.ResourceKind.ToString(),
                    ["resourceId"] = duplicate.Key.ResourceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        var expected = Integration.ListCollectionSchedulesHandler.NormalizeETag(ifMatch);
        if (expected is null && logger is not null)
        {
            LogWithoutIfMatch(logger, roleId);
        }

        // ⛔ Звірка, заміна, штампи й аудит — ОДНА транзакція під UPDLOCK на
        // рядку ролі (C4 для аудиту). Блокування береться ПЕРШИМ: друга
        // одночасна заміна чекає тут, а не після звірки, і читає вже
        // зафіксований набір першої.
        await uow.ExecuteInTransactionAsync(
            async token =>
            {
                await LockAndCheckVersionAsync(roleId, expected, token).ConfigureAwait(false);

                await WriteAsync(role, roleId, grants, actorId, token).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);

        return ResourceGrantsVersion.Of(grants);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "PUT грантів ролі {RoleId} без If-Match: звірки версії не було, можлива втрачена правка.")]
    private static partial void LogWithoutIfMatch(ILogger logger, int roleId);

    private async Task LockAndCheckVersionAsync(int roleId, string? expected, CancellationToken ct)
    {
        if (!await users.LockRoleForUpdateAsync(roleId, ct).ConfigureAwait(false))
        {
            throw new NotFoundException(
                ErrorCodes.SecurityPrincipalNotFound, $"Ролі {roleId} не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-SEC-0404.roleNotFound",
                    ["roleId"] = roleId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        if (expected is null)
        {
            return;
        }

        // ⚠ Читання ПІСЛЯ блокування: під RCSI знімок береться на початку
        // оператора, тож він бачить заміну, що зафіксувалася, поки ми чекали.
        var actual = ResourceGrantsVersion.Of(
            await users.ListGrantsAsync(roleId, ct).ConfigureAwait(false));

        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            // ⚠ Ключ — загальний `err.ECR-SEC-0409` («конфлікт із налаштуваннями
            // безпеки»): окремого ключа «гранти застаріли» в каталозі немає, а
            // нових ключів ця правка не заводить. Актуальна версія — у details.
            throw new ConcurrencyConflictException(
                ErrorCodes.SecurityConflict,
                $"Гранти ролі {roleId} змінили після того, як їх прочитали.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-SEC-0409",
                    ["roleId"] = roleId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["version"] = actual,
                });
        }
    }

    private async Task WriteAsync(
        RoleView role, int roleId, IReadOnlyList<ResourceGrantDto> grants, int actorId, CancellationToken ct)
    {
        await users.ReplaceGrantsAsync(roleId, grants, ct).ConfigureAwait(false);

        // ⛔ Обов'язково і в тій самій транзакції. Профіль доступу кешується
        // під ключем «користувач + штамп» на 30 хвилин, і без прокрутки штампа
        // зміна не діяла б: виданий доступ не з'являвся, знятий не зникав
        // (`A7-23`). Другий випадок — це доступ, який адміністратор уже
        // вважає закритим.
        // ⚠ Штамп крутиться лише ПРЯМИМ носіям. Носіїв через групу AD система
        // поіменно не знає — для них зміну ловить ревізія грантів у відбитку
        // груп ключа профілю (`GroupsFingerprintAsync`) на наступному запиті.
        await users.RotateStampsForRoleAsync(roleId, ct).ConfigureAwait(false);

        // ⚠ Зміна доступу пишеться в журнал безпеки ЗАВЖДИ і повним набором:
        // «хто тепер це бачить» відновлюється лише так. Різницю не рахуємо —
        // попередній стан уже є в попередньому записі журналу.
        // ⛔ C4: подія, гранти й штампи — одним комітом (транзакція — у
        // виклику, разом зі звіркою версії).
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
