// src/Ecr.Application/Registries/RegistryAccess.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Security;
using Ecr.Domain.Enums;

namespace Ecr.Application.Registries;

/// <summary>
/// Перевірка доступу до ДАНИХ конкретного довідника: глобальне функціональне
/// право (<c>Registry.View</c>/<c>Registry.EditData</c>) АБО ресурсний грант
/// на цей самий <c>RegistryDefId</c> (<see cref="ResourceKind.Registry"/>).
/// </summary>
/// <remarks>
/// ⚠ Глобальне право лишається основним шляхом і НЕ звужується: хто його має,
/// бачить/редагує УСІ довідники, як і раніше. Ресурсний грант — ДОДАТКОВИЙ,
/// вужчий шлях для користувача БЕЗ глобального права; видається на конкретний
/// <c>RegistryDefId</c> тим самим маршрутом, що вже видає гранти на
/// проєкт/аркуш/таблицю/колонку — <c>PUT /api/v1/roles/{id}/grants</c>
/// (<see cref="ReplaceResourceGrantsHandler"/>), новий контролер не потрібен.
/// <para>
/// Той самий клас "OR", що вже є для <c>Integration.Manage</c>/<c>Integration.View</c>
/// (<see cref="PermissionCheck.RequireAnyAsync"/>) — тут лише альтернатива не
/// друге глобальне право, а ресурсний грант, тож рішення не зводиться до
/// списку кодів права: перевіряються <see cref="AccessProfile.Has"/> і
/// <see cref="AccessProfile.LevelFor"/> — те саме, чим уже перевіряються
/// гранти на проєкт (<c>ResourceKind.Project</c>) у документах.
/// </para>
/// </remarks>
public static class RegistryAccess
{
    /// <summary>
    /// Вимагає глобальне <paramref name="permission"/> АБО ресурсний грант
    /// рівня <paramref name="minLevel"/>+ на довідник <paramref name="registryDefId"/>,
    /// уже відомий викликачу (без додаткового походу в базу).
    /// </summary>
    /// <param name="access">Служба рішень про доступ.</param>
    /// <param name="currentUser">Поточний користувач запиту.</param>
    /// <param name="permission">Глобальне право — основний, ширший шлях.</param>
    /// <param name="minLevel">Мінімальний рівень гранта: <c>Read</c> для перегляду, <c>Write</c> для зміни даних.</param>
    /// <param name="registryDefId">Довідник, на який перевіряється ресурсний грант.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="AccessDeniedException">Анонім, або немає ні права, ні гранта.</exception>
    public static Task<AccessProfile> RequireAsync(
        IAccessDecisionService access,
        ICurrentUser currentUser,
        string permission,
        GrantLevel minLevel,
        int registryDefId,
        CancellationToken ct)
        => RequireAsync(access, currentUser, permission, minLevel, _ => Task.FromResult<int?>(registryDefId), ct);

    /// <summary>
    /// Те саме, що інша перевантаженість, але довідник невідомий заздалегідь
    /// (лише код довідника з маршруту) і резолвиться через
    /// <paramref name="resolveRegistryDefId"/> ЛІНИВО — тільки тоді, коли
    /// глобального права нема.
    /// </summary>
    /// <remarks>
    /// ⛔ Ледача резолюція — не оптимізація, а контракт: власник глобального
    /// права не мусить платити зайвим походом у базу за довідник, якого й так
    /// показано УВЕСЬ (<c>RegistryEntriesAsOfValidationTests</c> фіксує це
    /// для суміжної перевірки — власна валідація параметра йде РАНІШЕ будь-
    /// якого <c>FindDefinitionAsync</c>, і ця перевірка не повинна ламати той
    /// порядок для того самого користувача).
    /// </remarks>
    /// <param name="access">Служба рішень про доступ.</param>
    /// <param name="currentUser">Поточний користувач запиту.</param>
    /// <param name="permission">Глобальне право — основний, ширший шлях.</param>
    /// <param name="minLevel">Мінімальний рівень гранта.</param>
    /// <param name="resolveRegistryDefId">
    /// Резолвер довідника; викликається щонайбільше раз, лише коли
    /// <paramref name="permission"/> відсутнє. <c>null</c> — довідника немає
    /// (сама перевірка тоді просто відмовляє — «не існує» лишається турботою
    /// виклику нижче за течією, як і раніше).
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="AccessDeniedException">Анонім, або немає ні права, ні гранта.</exception>
    public static async Task<AccessProfile> RequireAsync(
        IAccessDecisionService access,
        ICurrentUser currentUser,
        string permission,
        GrantLevel minLevel,
        Func<CancellationToken, Task<int?>> resolveRegistryDefId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(resolveRegistryDefId);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(
                "ECR-AUTH-0401", "Потрібна автентифікація.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);

        if (profile.Has(permission))
        {
            return profile;
        }

        var registryDefId = await resolveRegistryDefId(ct).ConfigureAwait(false);

        if (registryDefId is { } id && profile.LevelFor(ResourceKind.Registry, id) >= minLevel)
        {
            return profile;
        }

        // ⚠ Той самий код, messageKey і формат Details, що в РЕШТІ відмов
        // «немає права» (err.ECR-AUTH-0403.permission — RoleAndUserHandlers,
        // ProjectQueryHandlers та інші): клієнт читає код права з
        // Details["permission"] однаково для обох класів відмови. Про сам
        // ресурсний грант користувачу знати не треба — повідомлення про
        // ГЛОБАЛЬНЕ право лишається зрозумілим і тоді, коли насправді
        // бракує саме гранта.
        throw new AccessDeniedException(
            "ECR-AUTH-0403",
            $"Потрібне право {permission}.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-AUTH-0403.permission",
                ["permission"] = permission,
            });
    }
}
