using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Security;

/// <summary>Роль із її правами.</summary>
/// <param name="Id">Ідентифікатор.</param>
/// <param name="Code">Код.</param>
/// <param name="IsBuiltIn">Вбудована роль із seed: видаленню не підлягає.</param>
/// <param name="IsActive">Чи діє.</param>
/// <param name="Permissions">Права ролі.</param>
/// <param name="DangerousPermissions">
/// Небезпечні права серед них — показуються окремо, бо їх видають поіменно.
/// </param>
/// <param name="NameL10n">
/// Назва мовами каталогу (<c>sec.Role.NameL10n</c>) — те, що адміністратор
/// задав при створенні чи перейменуванні. Сховище заповнює її завжди;
/// <c>null</c> лише в підробках, що назви не моделюють. Клієнт без назви
/// показує код.
/// </param>
public sealed record RoleView(
    int Id,
    string Code,
    bool IsBuiltIn,
    bool IsActive,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> DangerousPermissions,
    LocalizedText? NameL10n = null);

/// <summary>Межі чинності одного призначення — підміна ролі на час відпустки (ФВ-6.16).</summary>
/// <param name="ValidFrom">Початок дії; <c>null</c> — від завжди.</param>
/// <param name="ValidTo">Кінець дії; <c>null</c> — безстроково.</param>
/// <remarks>
/// ⚠ Відсутність запису під кодом ролі в словнику, де цей тип — значення,
/// означає те саме, що й запис із двома <c>null</c>: роль безстрокова. Два
/// способи сказати одне й те саме тут не розходяться, бо перевіряються в
/// ОДНОМУ місці (<see cref="ReplaceUserRolesHandler"/>).
/// </remarks>
public sealed record RoleValidityWindow(DateOnly? ValidFrom, DateOnly? ValidTo);

/// <summary>Особисте призначення ролі користувачу — з межами чинності й областю дії.</summary>
/// <param name="RoleCode">Код ролі.</param>
/// <param name="ValidFrom">Початок дії; <c>null</c> — від завжди.</param>
/// <param name="ValidTo">Кінець дії; <c>null</c> — безстроково.</param>
/// <param name="Scope">
/// Область дії (ФВ-6.14); <c>null</c> — роль діє в усіх проєктах. Порожній
/// перелік — збережена область не розбирається, і роль не діє ніде
/// (<see cref="Ecr.Domain.Entities.Security.RoleAssignment.ScopedProjectIds"/>).
/// </param>
public sealed record UserRoleAssignmentView(
    string RoleCode, DateOnly? ValidFrom, DateOnly? ValidTo, RoleScopeDto? Scope);

/// <summary>Обліковий запис у переліку.</summary>
/// <remarks>⛔ Ні хеша пароля, ні солі, ні <c>SecurityStamp</c> тут немає (ФВ-6.11).</remarks>
/// <param name="Id">Ідентифікатор.</param>
/// <param name="UserName">Ім'я входу.</param>
/// <param name="DisplayName">Ім'я для показу.</param>
/// <param name="Provider">Провайдер входу.</param>
/// <param name="IsActive">Чи діє запис.</param>
/// <param name="IsBootstrapAdmin">Технічний запис первинного налаштування.</param>
/// <param name="MustChangePassword">Пароль виданий разово.</param>
/// <param name="IsLockedOut">Заблокований після невдалих спроб.</param>
/// <param name="Email">Пошта; без неї отримання алертів увімкнути не можна.</param>
/// <param name="ReceivesAlerts">Чи отримує алерти про збої (`D-125`).</param>
/// <param name="LastSignInAt">Останній успішний вхід (UTC); <c>null</c> — не входив ніколи (BE-12).</param>
public sealed record UserView(
    int Id,
    string UserName,
    string DisplayName,
    AuthProvider Provider,
    bool IsActive,
    bool IsBootstrapAdmin,
    bool MustChangePassword,
    bool IsLockedOut,
    string? Email,
    bool ReceivesAlerts,
    DateTime? LastSignInAt);

/// <summary>Перелік ролей із правами. Право <c>Security.ManageRoles</c>.</summary>
public sealed class ListRolesHandler(IUserStore users, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Право, без якого перелік ролей не віддається.</summary>
    public const string Permission = "Security.ManageRoles";

    /// <summary>Повертає ролі з їхніми правами.</summary>
    /// <param name="ct">Токен скасування.</param>
    public async Task<IReadOnlyList<RoleView>> HandleAsync(CancellationToken ct)
    {
        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401", "Потрібна автентифікація.",
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        if (!PermissionCheck.IsGranted(profile, Permission))
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Потрібне право {Permission}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-AUTH-0403.permission",
                    ["permission"] = Permission,
                });
        }

        return await users.ListRolesAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>Створення ролі. Право <c>Security.ManageRoles</c>.</summary>
public sealed class CreateRoleHandler(
    IUserStore users,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Створює роль із набором прав.</summary>
    /// <param name="code">Код ролі.</param>
    /// <param name="name">Назва мовами каталогу.</param>
    /// <param name="permissionCodes">Права ролі.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Ідентифікатор створеної ролі.</returns>
    public async Task<int> HandleAsync(
        string code,
        IReadOnlyDictionary<string, string> name,
        IReadOnlyList<string> permissionCodes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(permissionCodes);

        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401", "Потрібна автентифікація.",
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        if (!PermissionCheck.IsGranted(profile, ListRolesHandler.Permission))
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Потрібне право {ListRolesHandler.Permission}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-AUTH-0403.permission",
                    ["permission"] = ListRolesHandler.Permission,
                });
        }

        // ⛔ Правила «видати можна лише те, що маєш» більше НЕМАЄ (`D-121`,
        // рішення замовника). Його не було й у пакеті: `ФВ-6.12` каже
        // «видаються ролям окремо» — склад ролей визначає адміністратор
        // замовника, іменних осіб у вимогах немає — і більше нічого.
        //
        // ⚠ Воно замикало коло. Bootstrap-запис вимикається автоматично,
        // щойно з'являється доменний адміністратор (`D-97`). Після цього
        // видати `Calculation.Publish` не міг уже НІХТО: у ролі
        // `SystemAdministrator` небезпечних прав немає за seed, а правило
        // забороняло видати те, чого не маєш. Той самий деадлок, що й `A7-17`,
        // тільки на день пізніше.
        //
        // ⚠ Захист «чотирьох очей» стоїть у точці ВИКОРИСТАННЯ, а не видачі:
        // `Calculation.Publish` вимагає не бути автором останньої правки
        // (`D-40`), `Period.Reopen` вимагає причини, симуляція пишеться в
        // аудит. Дублювати його ще й тут — і було тим, що замикало коло.
        //
        // Натомість кожна видача небезпечного права — окремий запис у журналі
        // безпеки: не заборона, а слід.
        // ⛔ Невідомі коди відсіюються ДО створення ролі (`H-21`, пункт 4
        // директиви №06 §5). Каталог прав закритий: його оголошує КОД, бо саме
        // код їх перевіряє, — тож код, якого в каталозі немає, не означає
        // нічого і ніколи не спрацює.
        //
        // ⚠ Мовчазним збереженням це не було лише завдяки зовнішньому ключу
        // `FK_RolePerm_Perm`: без перевірки запит помирав у SQL, і
        // адміністратор отримував `500` без жодної згадки, ЯКИЙ саме код
        // хибний. Тобто роль не створювалася, а причина лишалася в логах бази.
        //
        // ⚠ Відмова НАБОРОМ, як і в ролях (`ReplaceRolesAsync`): створити роль
        // «з того, що знайшлося» гірше за відмову — вона виглядала б робочою і
        // мовчки не давала частини повноважень.
        var unknown = await users.FilterUnknownAsync(permissionCodes, ct).ConfigureAwait(false);
        if (unknown.Count > 0)
        {
            throw new NotFoundException(
                "ECR-SEC-0404", $"Прав не існує в каталозі: {string.Join(", ", unknown)}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-SEC-0404.permissionsUnknown",
                    ["permissions"] = string.Join(", ", unknown),
                });
        }

        var dangerous = await users.FilterDangerousAsync(permissionCodes, ct).ConfigureAwait(false);

        var role = new Role(EcrCode.Create(code), new LocalizedText(name.ToDictionary(StringComparer.Ordinal)));
        var roleId = await users.AddRoleAsync(role, permissionCodes, ct).ConfigureAwait(false);

        // ⚠ Окремим записом і лише коли є що записувати: рядок «видано нуль
        // небезпечних прав» у журналі безпеки — шум, який ховає справжні.
        if (dangerous.Count > 0)
        {
            await audit.WriteSecurityEventAsync(
                new SecurityEventRecord(
                    clock.UtcNow,
                    "DangerousPermissionsGranted",
                    TargetUserId: null,
                    TargetRoleId: roleId,
                    DetailsJson: JsonSerializer.Serialize(new { role = code, permissions = dangerous }),
                    ChangedByUserId: userId,
                    CorrelationId: null),
                ct).ConfigureAwait(false);
        }

        await audit.WriteSecurityEventAsync(
            new SecurityEventRecord(
                clock.UtcNow,
                "RoleCreated",
                TargetUserId: null,
                TargetRoleId: roleId,
                DetailsJson: JsonSerializer.Serialize(new { code, permissions = permissionCodes }),
                ChangedByUserId: userId,
                CorrelationId: currentUser.CorrelationId),
            ct).ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return roleId;
    }
}

/// <summary>Перелік користувачів. Право <c>Security.ManageUsers</c>.</summary>
/// <summary>
/// Заміна набору ролей наявного користувача. Право <c>Security.ManageUsers</c>.
/// </summary>
/// <remarks>
/// ⛔ Способу призначити роль наявному користувачеві не існувало **взагалі**:
/// ролі видавалися лише при створенні, а форма створення надсилала порожній
/// перелік. Обліковий запис виходив працездатним на вигляд і безправним
/// насправді, і виправити це було нічим, крім прямого запису в базу.
///
/// ⚠ Заміна НАБОРОМ: набір ролей і є повноваженнями людини, і бачити його
/// треба цілком, а не як історію додавань.
/// </remarks>
public sealed class ReplaceUserRolesHandler(
    IUserStore users,
    IAccessDecisionService access,
    IUnitOfWork uow,
    ICurrentUser currentUser,
    IAuditWriter audit,
    Domain.Abstractions.IClock clock,
    DisableBootstrapAdminHandler disableBootstrap,
    IDocumentStore documents,
    IResourceNameResolver sheetCatalog)
{
    /// <summary>Право керування користувачами.</summary>
    public const string Permission = "Security.ManageUsers";

    /// <summary>Замінює ролі користувача.</summary>
    /// <param name="userId">Користувач.</param>
    /// <param name="roleCodes">Коди ролей; порожньо — прибрати всі.</param>
    /// <param name="validity">
    /// Межі чинності за кодом ролі (ФВ-6.16 — підміна на час відпустки); код
    /// без запису тут або відсутній словник — роль безстрокова, як і
    /// раніше.
    /// </param>
    /// <param name="scopes">
    /// Області дії за кодом ролі (ФВ-6.14). <c>null</c> — області наявних
    /// призначень ЗБЕРІГАЮТЬСЯ (клієнт, що про поле не знає, не має мовчки
    /// розширити роль до всіх проєктів); словник — повна відповідь: роль без
    /// запису в ньому діє в усіх проєктах.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Скільки ролей тепер призначено.</returns>
    public async Task<int> HandleAsync(
        int userId,
        IReadOnlyList<string> roleCodes,
        IReadOnlyDictionary<string, RoleValidityWindow>? validity,
        IReadOnlyDictionary<string, RoleScopeDto>? scopes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(roleCodes);

        var actorId = currentUser.UserId
                      ?? throw new AccessDeniedException(
                          "ECR-AUTH-0401", "Потрібна автентифікація.",
                          new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });

        var profile = await access.BuildProfileAsync(actorId, ct).ConfigureAwait(false);
        if (!PermissionCheck.IsGranted(profile, Permission))
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Потрібне право {Permission}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-AUTH-0403.permission",
                    ["permission"] = Permission,
                });
        }

        ValidateValidity(roleCodes, validity);

        // ⛔ ФВ-6.14: область перевіряється ДО будь-якої зміни — і на
        // існування проєкту, і на право ним керувати.
        Dictionary<string, RoleAssignmentScope>? domainScopes = null;
        if (scopes is not null)
        {
            domainScopes = new Dictionary<string, RoleAssignmentScope>(StringComparer.Ordinal);
            foreach (var (code, scope) in scopes)
            {
                if (!roleCodes.Contains(code, StringComparer.Ordinal))
                {
                    throw new BusinessRuleException(
                        ErrorCodes.RequestInvalid,
                        $"Область дії задано для ролі «{code}», якої немає в наборі, що призначається.",
                        new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REQ-0422", ["code"] = code });
                }

                domainScopes[code] = await RoleAssignmentScopeRules
                    .ValidateAsync(scope, code, profile, documents, sheetCatalog, ct)
                    .ConfigureAwait(false);
            }
        }

        var before = await users.ListUserRolesAsync(userId, ct).ConfigureAwait(false);
        var count = await users.ReplaceRolesAsync(userId, roleCodes, validity, domainScopes, ct).ConfigureAwait(false);

        // ⛔ Зміна повноважень — подія безпеки, і вона мусить бути в журналі
        // з обома наборами. «Хто це йому видав» — питання, на яке через рік
        // має бути відповідь, а не здогад.
        // ⛔ C4: подія й призначення — одним комітом; область дії — в події.
        await uow.ExecuteInTransactionAsync(
            async token =>
            {
                await audit.WriteSecurityEventAsync(
                    new SecurityEventRecord(
                        ChangedAt: clock.UtcNow,
                        EventType: "UserRolesReplaced",
                        TargetUserId: userId,
                        TargetRoleId: null,
                        DetailsJson: System.Text.Json.JsonSerializer.Serialize(new
                        {
                            from = before,
                            to = roleCodes,
                            scopes = domainScopes?.ToDictionary(
                                s => s.Key, s => s.Value.ProjectIds, StringComparer.Ordinal),

                            // D-214: звуження аркушами й періодами — окремим
                            // полем, щоб форма `scopes` для наявних читачів журналу
                            // не змінилась.
                            narrowing = domainScopes?
                                .Where(s => s.Value.IsNarrowed)
                                .ToDictionary(
                                    s => s.Key,
                                    s => new { sheets = s.Value.SheetCodes, from = s.Value.PeriodFrom?.Value, to = s.Value.PeriodTo?.Value },
                                    StringComparer.Ordinal),
                        }),
                        ChangedByUserId: actorId,
                        CorrelationId: currentUser.CorrelationId),
                    token).ConfigureAwait(false);

                await uow.SaveChangesAsync(token).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);

        // ⚠ Той самий виклик, що й після кожного призначення ролі при
        // створенні (`CreateUserHandler`, D-97): заміна набору — це так само
        // місце, де в системи міг з'явитися перший активний доменний
        // адміністратор. Без цього виклику bootstrap-запис лишався
        // технічно чинним НАЗАВЖДИ, якщо роль видавали через ЦЕЙ шлях, а не
        // через створення нового користувача (`#20`) — сам виклик ідемпотентний
        // і сам вирішує, чи справді настали умови (`DisableBootstrapAdminHandler`).
        await disableBootstrap.HandleAsync(ct).ConfigureAwait(false);

        return count;
    }

    /// <summary>Перевіряє межі чинності з тіла запиту (ФВ-6.16).</summary>
    /// <remarks>
    /// ⚠ Це помилка ЗАПиту, а не доменного інваріанта (переплутані дати в
    /// наказі про підміну), тому ловиться тут, до звернення до сховища —
    /// відповідь називає конкретне поле, а не абстрактний внутрішній стан.
    /// </remarks>
    private static void ValidateValidity(
        IReadOnlyList<string> roleCodes, IReadOnlyDictionary<string, RoleValidityWindow>? validity)
    {
        if (validity is null)
        {
            return;
        }

        foreach (var (code, window) in validity)
        {
            if (!roleCodes.Contains(code, StringComparer.Ordinal))
            {
                throw new BusinessRuleException(
                    ErrorCodes.RequestInvalid,
                    $"Межі чинності задано для ролі «{code}», якої немає в наборі, що призначається.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-REQ-0422.validityRoleNotAssigned",
                        ["code"] = code,
                    });
            }

            if (window.ValidFrom is { } from && window.ValidTo is { } to && from > to)
            {
                // ⚠ Той самий факт, що й у `AssignGroupRoleHandler`
                // (`GroupRoleAssignmentHandlers.cs`): ключ перевикористаний,
                // бо межі «початок пізніше за кінець» — одна причина
                // незалежно від того, як код до неї дійшов.
                throw new BusinessRuleException(
                    ErrorCodes.RequestInvalid,
                    $"Роль «{code}»: початок дії ({from}) пізніше за кінець ({to}).",
                    new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REQ-0422.validityOrder" });
            }
        }
    }
}

/// <summary>Ролі користувача. Право <c>Security.ManageUsers</c>.</summary>
/// <remarks>
/// ⚠ Потрібен формі: без нього редактор доступу відкривався б із порожнім
/// переліком, і збереження мовчки відібрало б усі права.
/// </remarks>
public sealed class ListUserRolesHandler(
    IUserStore users, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Право керування користувачами.</summary>
    public const string Permission = "Security.ManageUsers";

    /// <summary>Повертає коди ролей користувача.</summary>
    /// <param name="userId">Користувач.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<IReadOnlyList<string>> HandleAsync(int userId, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var roles = await users.ListUserRolesAsync(userId, ct).ConfigureAwait(false);

        // ⛔ B-07: неіснуючий користувач давав `200 []` — «ролей немає» на
        // адресі, якої не існує. Питаємо лише коли порожньо.
        if (roles.Count == 0 && await users.FindByIdAsync(userId, ct).ConfigureAwait(false) is null)
        {
            throw new NotFoundException(
                Domain.Errors.ErrorCodes.SecurityPrincipalNotFound,
                $"Користувача {userId} не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-SEC-0404.userNotFound",
                    ["userId"] = userId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        return roles;
    }

    /// <summary>
    /// Особисті призначення користувача з межами й областю дії (ФВ-6.14).
    /// </summary>
    /// <param name="userId">Користувач.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Без цього читання форма ролей не могла показати області, а
    /// <c>PUT …/roles</c> зі словником <c>scopes</c> — повна відповідь: клієнт,
    /// що шле його наосліп, мовчки знімав би чужі області. Право те саме, що й
    /// на запис набору ролей.
    /// </remarks>
    public async Task<IReadOnlyList<UserRoleAssignmentView>> ListAssignmentsAsync(int userId, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        if (await users.FindByIdAsync(userId, ct).ConfigureAwait(false) is null)
        {
            throw new NotFoundException(
                Domain.Errors.ErrorCodes.SecurityPrincipalNotFound,
                $"Користувача {userId} не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-SEC-0404.userNotFound",
                    ["userId"] = userId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        return await users.ListUserRoleAssignmentsAsync(userId, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Адреса користувача для сповіщень. Право <c>Security.ManageUsers</c>.
/// </summary>
/// <remarks>
/// ⛔ Поле <c>User.Email</c> існувало від Етапу 3 і **не присвоювалося ніде**.
/// Наслідок мовчазний і повний: <c>NotificationJob</c> завжди отримував
/// порожній перелік адресатів, тобто сповіщення (<c>ФВ-12</c>) не надходили
/// нікому, а перемикач «отримувати сповіщення» був вічно неактивним і
/// виглядав як налаштування, яке просто вимкнули.
///
/// ⛔ S20 (аудит безпеки): зміна адреси — подія безпеки <see cref="EventType"/>
/// в тій самій транзакції, що й зміна. Адреса — канал сповіщень і, отже,
/// спосіб перехопити їх; без сліду в журналі підміну не видно. Адреси в
/// журналі МАСКОВАНІ (<see cref="MaskEmail"/>): журнал читає ширше коло, ніж
/// картку користувача.
/// </remarks>
public sealed class SetUserEmailHandler(
    IUserStore users,
    IAccessDecisionService access,
    IUnitOfWork uow,
    ICurrentUser currentUser,
    IAuditWriter audit,
    IClock clock)
{
    /// <summary>Право керування користувачами.</summary>
    public const string Permission = "Security.ManageUsers";

    /// <summary>Тип події в <c>aud.SecurityEvent</c>.</summary>
    public const string EventType = "UserEmailChanged";

    /// <summary>
    /// Маска адреси для журналу: перші два символи локальної частини й домен
    /// (<c>jo***@example.com</c>); <c>null</c> — адреси немає.
    /// </summary>
    /// <param name="email">Адреса.</param>
    public static string? MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        var trimmed = email.Trim();
        var at = trimmed.LastIndexOf('@');
        var local = at < 0 ? trimmed : trimmed[..at];
        var domain = at < 0 ? string.Empty : trimmed[at..];

        return string.Concat(local.AsSpan(0, Math.Min(2, local.Length)), "***", domain);
    }

    /// <summary>Задає або прибирає адресу.</summary>
    /// <param name="userId">Користувач.</param>
    /// <param name="email">Адреса; порожньо — прибрати.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task HandleAsync(int userId, string? email, CancellationToken ct)
    {
        var actorId = currentUser.UserId
                      ?? throw new AccessDeniedException(
                          "ECR-AUTH-0401", "Потрібна автентифікація.",
                          new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });

        var profile = await access.BuildProfileAsync(actorId, ct).ConfigureAwait(false);
        if (!PermissionCheck.IsGranted(profile, Permission))
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Потрібне право {Permission}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-AUTH-0403.permission",
                    ["permission"] = Permission,
                });
        }

        var user = await users.FindByIdAsync(userId, ct).ConfigureAwait(false)
                   ?? throw new NotFoundException(
                       "ECR-SEC-0404", $"Користувача {userId} не знайдено.",
                       new Dictionary<string, object?>
                       {
                           ["messageKey"] = "err.ECR-SEC-0404.userNotFound",
                           ["userId"] = userId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                       });

        var before = user.Email;

        // ⚠ Прибирання адреси знімає і прапорець сповіщень: прапорець без
        // пошти беззмістовний і виглядав би як налаштований адресат, якому
        // нічого не надсилається.
        user.SetEmail(email);
        if (string.IsNullOrWhiteSpace(email))
        {
            user.SetReceivesAlerts(false);
        }

        // Та сама адреса — не зміна: подія без зміни засмічувала б журнал.
        if (string.Equals(before, user.Email, StringComparison.Ordinal))
        {
            await uow.SaveChangesAsync(ct).ConfigureAwait(false);
            return;
        }

        var after = user.Email;

        // ⛔ Зміна й подія — ОДНА транзакція: журнал не має казати про зміну,
        // якої не сталося, і зміна не має пройти без запису.
        await uow.ExecuteInTransactionAsync(
            async token =>
            {
                await audit.WriteSecurityEventAsync(
                    new SecurityEventRecord(
                        ChangedAt: clock.UtcNow,
                        EventType: EventType,
                        TargetUserId: userId,
                        TargetRoleId: null,
                        DetailsJson: JsonSerializer.Serialize(new
                        {
                            oldEmail = MaskEmail(before),
                            newEmail = MaskEmail(after),
                        }),
                        ChangedByUserId: actorId,
                        CorrelationId: currentUser.CorrelationId),
                    token).ConfigureAwait(false);

                await uow.SaveChangesAsync(token).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
    }
}

public sealed class ListUsersHandler(IUserStore users, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Право, без якого перелік не віддається.</summary>
    public const string Permission = "Security.ManageUsers";

    /// <summary>Повертає сторінку користувачів.</summary>
    /// <param name="page">Параметри курсорної пагінації.</param>
    /// <param name="utcNow">Момент для обчислення блокування.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<PagedResult<UserView>> HandleAsync(
        CursorRequest page, DateTime utcNow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);

        // ⛔ Розмір сторінки перевіряється ТУТ, а не в контролері. Там стояв
        // `BadRequest(new { error = "limit поза межами 1..N" })` — звичайний
        // JSON повз `ExceptionHandlingMiddleware`, українське речення і жодного
        // коду помилки, тобто клієнтові не було чого розрізняти
        // (`02-contracts.md` §7, `UI-WALKTHROUGH.md` F1/F4). Той самий сценарій
        // у `ListDocumentsHandler` уже вирішено саме так.
        if (!page.IsValid)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Розмір сторінки поза межами 1..{CursorRequest.MaxLimit}.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "err.ECR-REQ-0422.pageSizeOutOfRange",
                    ["max"] = CursorRequest.MaxLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401", "Потрібна автентифікація.",
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        if (!PermissionCheck.IsGranted(profile, Permission))
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Потрібне право {Permission}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-AUTH-0403.permission",
                    ["permission"] = Permission,
                });
        }

        return await users.ListAsync(page, utcNow, ct).ConfigureAwait(false);
    }
}

/// <summary>Створення користувача. Право <c>Security.ManageUsers</c>.</summary>
public sealed class CreateUserHandler(
    IUserStore users,
    IPasswordHasher hasher,
    IAccessDecisionService access,
    DisableBootstrapAdminHandler disableBootstrap,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Створює локального або доменного користувача.</summary>
    /// <param name="userName">Ім'я входу.</param>
    /// <param name="displayName">Ім'я для показу.</param>
    /// <param name="provider">Провайдер входу.</param>
    /// <param name="windowsSid">SID; лише для доменного.</param>
    /// <param name="initialPassword">Разовий пароль; лише для локального.</param>
    /// <param name="roleCodes">Ролі, які призначити одразу.</param>
    /// <param name="email">Адреса для сповіщень; <c>null</c> — без адреси.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<int> HandleAsync(
        string userName,
        string displayName,
        AuthProvider provider,
        string? windowsSid,
        string? initialPassword,
        IReadOnlyList<string> roleCodes,
        string? email,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(roleCodes);

        var actorId = currentUser.UserId
                      ?? throw new AccessDeniedException(
                          "ECR-AUTH-0401", "Потрібна автентифікація.",
                          new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });

        var profile = await access.BuildProfileAsync(actorId, ct).ConfigureAwait(false);
        if (!PermissionCheck.IsGranted(profile, ListUsersHandler.Permission))
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Потрібне право {ListUsersHandler.Permission}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-AUTH-0403.permission",
                    ["permission"] = ListUsersHandler.Permission,
                });
        }

        if (await users.FindByUserNameAsync(userName, ct).ConfigureAwait(false) is not null)
        {
            // ⛔ Родина USR, а не ROW (`P-25`, рядок 3). Дублікат ОБЛІКОВОГО
            // ЗАПИСУ подавався як дублікат `RowKey` у таблиці документа: форма
            // створення користувача не має сітки, і відмова доїжджала в
            // обробник, у якого для неї немає ні місця, ні тексту.
            throw new BusinessRuleException(
                ErrorCodes.UserDuplicate, $"Користувач з іменем «{userName}» уже існує.",
                // ⛔ Q-30x: без цього словника подробиця доїжджала клієнту
                // сирим українським реченням незалежно від мови інтерфейсу —
                // той самий клас дефекту, що ECR-AUTH-0403 до Q-300, тепер
                // через узагальнений шлях (ExceptionHandlingMiddleware,
                // messageKey), а не точковий арм на цей один код.
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-USR-0409.userNameTaken", ["userName"] = userName });
        }

        // ⛔ Ролі перевіряються ТУТ і ДО створення чого-небудь. Сховище
        // виходить із того, що роль приходить із seed, і на невідому кидає
        // «виконайте seed» — вірно для старту й безглуздо для запиту людини:
        // друкарська помилка в коді ролі поверталася як `ECR-SYS-0500`
        // «зверніться до адміністратора» (`A7-18`), хоча адміністратор — це
        // якраз той, хто її щойно зробив.
        //
        // ⚠ Перевірка перед створенням, а не всередині циклу: інакше друга
        // роль із помилкою лишила б користувача створеним із першою.
        if (roleCodes.Count > 0)
        {
            // ⛔ Прямі запити за кодом: ListRolesAsync обрізає Take(500).
            var unknown = new List<string>();
            foreach (var code in roleCodes)
            {
                if (!await users.RoleCodeExistsAsync(code, 0, ct).ConfigureAwait(false))
                {
                    unknown.Add(code);
                }
            }

            if (unknown.Count > 0)
            {
                // ⚠ Саме `NotFoundException`: статус відповіді береться з ТИПУ
                // винятку, а не з коду. `BusinessRuleException` дав би 422 при
                // коді `...0404` — відповідь, що суперечить сама собі.
                // ⛔ Родина SEC, а не ROW (`P-25`, рядок 2): невідома роль —
                // це відсутній запис каталогу безпеки, а не рядок сітки
                // документа.
                throw new NotFoundException(
                    ErrorCodes.SecurityPrincipalNotFound,
                    $"Ролей не існує: {string.Join(", ", unknown)}.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-SEC-0404.rolesUnknown",
                        ["roles"] = string.Join(", ", unknown),
                    });
            }
        }

        var now = clock.UtcNow;
        User user;

        if (provider == AuthProvider.Windows)
        {
            // Доменний запис — без пароля взагалі: пароль живе в каталозі, і
            // друга його копія тут була б і зайвою, і небезпечною.
            user = User.CreateDomain(
                userName, displayName,
                // ⛔ Родина USR, а не CELL: суб'єкт відмови — обліковий запис,
                // а не комірка документа. З ECR-CELL-0422 відмова створення
                // користувача приходила в обробник помилок сітки.
                windowsSid ?? throw new BusinessRuleException(
                    ErrorCodes.UserInvalid, "Для доменного запису потрібен SID.",
                    new Dictionary<string, object?> { ["messageKey"] = "err.ECR-USR-0422.windowsSidRequired" }),
                now);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(initialPassword))
            {
                throw new BusinessRuleException(
                    ErrorCodes.UserInvalid, "Для локального запису потрібен разовий пароль.",
                    new Dictionary<string, object?> { ["messageKey"] = "err.ECR-USR-0422.initialPasswordRequired" });
            }

            user = new User(userName, displayName, AuthProvider.Local);

            // ⛔ Аудит-пас 5: `ChangePasswordHandler` перевіряє
            // `PasswordPolicy.MinLength` (щойно свій пароль міняє чинний
            // власник), а `CreateUserHandler` — ні. Розрив: адмін міг
            // видати НОВОМУ користувачу разовий пароль коротший за політику,
            // і саме цей пароль (не обраний самим користувачем) ніколи
            // повторно не перевіряється довжиною — до першої зміни він і є
            // чинним паролем облікового запису.
            //
            // ⚠ S15: та сама перевірка, що й у `ChangePasswordHandler` —
            // правило не залежить від того, чи пароль разовий (видає
            // адміністратор), чи свій.
            var policy = await users.GetPolicyAsync(user, ct).ConfigureAwait(false);
            PasswordPolicyCheck.Ensure(policy, initialPassword, userName, "Разовий пароль");

            user.SetPassword(hasher.Hash(initialPassword));

            // ⚠ Разовий пароль знає той, хто створював. Доки його не змінили,
            // доступний лише сам обмін пароля (ФВ-6.18) — інакше адміністратор
            // назавжди лишається з чинним входом у чужий обліковий запис.
            user.RequirePasswordChange();
        }

        // ⚠ Адреса задається ОДРАЗУ. Без неї обліковий запис не отримує
        // сповіщень (`ФВ-12`), а дізнатися про це можна лише тоді, коли лист
        // не прийшов.
        user.SetEmail(email);

        users.Add(user);
        foreach (var roleCode in roleCodes)
        {
            await users.GrantRoleAsync(user, roleCode, ct).ConfigureAwait(false);
        }

        await audit.WriteSecurityEventAsync(
            new SecurityEventRecord(
                now,
                "UserCreated",
                TargetUserId: null,
                TargetRoleId: null,

                // ⛔ Пароль сюди не потрапляє ні в якому вигляді (ФВ-6.11).
                DetailsJson: JsonSerializer.Serialize(new { userName, provider = provider.ToString(), roleCodes }),
                ChangedByUserId: actorId,
                CorrelationId: currentUser.CorrelationId),
            ct).ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        // ⚠ Після КОЖНОГО призначення ролі, а не за розкладом: вікно між
        // появою доменного адміністратора і вимкненням bootstrap-запису має
        // бути якомога коротшим (D-97).
        await disableBootstrap.HandleAsync(ct).ConfigureAwait(false);

        return user.Id;
    }
}

/// <summary>
/// Вмикає або вимикає отримання алертів. Право <c>Security.ManageUsers</c>.
/// </summary>
/// <remarks>
/// ⛔ Адресати алертів — **дані, а не конфігурація** (`D-125`). Перелік у
/// змінних оточення довелося б міняти розгортанням щоразу, коли хтось іде у
/// відпустку, — і саме тому його б не міняли.
///
/// ⚠ Нового права НЕ заводимо: керування користувачами і є те місце, де це
/// вмикають. Зайве право — це ще один рядок у матриці, який ніхто не видасть.
/// </remarks>
public sealed class SetReceivesAlertsHandler(
    IUserStore users, IAccessDecisionService access, ICurrentUser currentUser, IUnitOfWork uow)
{
    /// <summary>Право на зміну.</summary>
    public const string Permission = "Security.ManageUsers";

    /// <summary>Змінює прапорець отримання алертів.</summary>
    /// <param name="userId">Користувач.</param>
    /// <param name="value">Чи отримує.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Користувача немає.</exception>
    /// <exception cref="Domain.Abstractions.DomainException">Увімкнено без пошти.</exception>
    public async Task HandleAsync(int userId, bool value, CancellationToken ct)
    {
        var actorId = currentUser.UserId
            ?? throw new AccessDeniedException(
                "ECR-AUTH-0401", "Потрібна автентифікація.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });

        var profile = await access.BuildProfileAsync(actorId, ct).ConfigureAwait(false);
        if (!PermissionCheck.IsGranted(profile, Permission))
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Потрібне право {Permission}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-AUTH-0403.permission",
                    ["permission"] = Permission,
                });
        }

        var user = await users.FindByIdAsync(userId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                ErrorCodes.SecurityPrincipalNotFound, $"Користувача {userId} не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-SEC-0404.userNotFound",
                    ["userId"] = userId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });

        // ⚠ Правило «без пошти не можна» живе в домені, а не тут: інакше його
        // обійшов би будь-який інший шлях запису.
        user.SetReceivesAlerts(value);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
