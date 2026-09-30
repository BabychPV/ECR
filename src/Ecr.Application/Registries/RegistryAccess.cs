// src/Ecr.Application/Registries/RegistryAccess.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Security;
using Ecr.Domain.Enums;

namespace Ecr.Application.Registries;

/// <summary>
/// Перевірка доступу до ДАНИХ конкретного довідника: глобальне функціональне
/// право (<c>Registry.View</c>/<c>Registry.EditData</c>) АБО ресурсний грант
/// на цей самий <c>RegistryDefId</c> (<see cref="ResourceKind.Registry"/>) —
/// і явна ЗАБОРОНА на довідник, яка перекриває обидва (S18).
/// </summary>
/// <remarks>
/// ⚠ Глобальне право лишається основним шляхом і НЕ звужується: хто його має,
/// бачить/редагує УСІ довідники, як і раніше, — крім тих, на які йому дано
/// явну заборону (<c>IsDeny</c>, ФВ-6.6: заборона виграє на будь-якому рівні).
/// Ресурсний грант — ДОДАТКОВИЙ, вужчий шлях для користувача БЕЗ глобального
/// права; видається на конкретний <c>RegistryDefId</c> тим самим маршрутом, що
/// вже видає гранти на проєкт/аркуш/таблицю/колонку —
/// <c>PUT /api/v1/roles/{id}/grants</c> (<see cref="ReplaceResourceGrantsHandler"/>),
/// новий контролер не потрібен.
/// <para>
/// Той самий клас "OR", що вже є для <c>Integration.Manage</c>/<c>Integration.View</c>
/// (<see cref="PermissionCheck.RequireAnyAsync"/>) — тут лише альтернатива не
/// друге глобальне право, а ресурсний грант, тож рішення не зводиться до
/// списку кодів права: перевіряються <see cref="AccessProfile.Has(string)"/> і
/// <see cref="AccessProfile.LevelFor"/> — те саме, чим уже перевіряються
/// гранти на проєкт (<c>ResourceKind.Project</c>) у документах.
/// </para>
/// <para>
/// ⛔ S18: до цього глобальне право повертало доступ ДО перевірки заборони, тож
/// заборона на довідник діяла лише на користувача без глобального права — тобто
/// ні на кого. Тепер єдине місце рішення «чи заборонений довідник» — тут
/// (<see cref="IsDenied"/>, <see cref="DeniedIds"/>); переліки й опис довідника
/// (<c>ListRegistriesHandler</c>, пошук, <c>GET …/definition</c>) питають його ж.
/// Заборонений довідник відповідає <c>404</c>, як неіснуючий (<see cref="NotFound(string)"/>),
/// — так само, як невидимий документ (B-08): різниця з 403 розкривала б, що він є.
/// </para>
/// </remarks>
public static class RegistryAccess
{
    private static readonly string KeyPrefix = ResourceKind.Registry.ToString() + ":";

    /// <summary>Чи має профіль явну заборону на довідник (заборона виграє над правом і грантом).</summary>
    /// <param name="profile">Профіль користувача.</param>
    /// <param name="registryDefId">Довідник.</param>
    public static bool IsDenied(AccessProfile profile, int registryDefId)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return profile.Denies.Contains(KeyPrefix + registryDefId.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Довідники, на які профіль має явну заборону; порожньо — заборон немає.</summary>
    /// <param name="profile">Профіль користувача.</param>
    public static IReadOnlyList<int> DeniedIds(AccessProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var ids = new List<int>();
        foreach (var key in profile.Denies)
        {
            if (key.StartsWith(KeyPrefix, StringComparison.Ordinal)
                && int.TryParse(key.AsSpan(KeyPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>
    /// <c>404 err.ECR-REG-0404.registry</c> — відповідь і на довідник, якого немає, і на
    /// довідник, схований забороною: єдина форма, щоб їх не можна було розрізнити.
    /// </summary>
    /// <param name="registryCode">Код довідника з запиту, як його передав клієнт.</param>
    internal static NotFoundException NotFound(string registryCode)
        => new(
            "ECR-REG-0404",
            $"Довідника «{registryCode}» не існує.",
            new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REG-0404.registry", ["registryCode"] = registryCode });

    /// <summary>
    /// Те саме для звернення за ідентифікатором — форма, якою обробники відповідають на
    /// неіснуючий <c>RegistryDefId</c> (<c>err.ECR-REG-0404.registryId</c>).
    /// </summary>
    /// <param name="registryDefId">Довідник з запиту.</param>
    internal static NotFoundException NotFound(int registryDefId)
        => new(
            "ECR-REG-0404",
            $"Довідника {registryDefId.ToString(CultureInfo.InvariantCulture)} не існує.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-REG-0404.registryId",
                ["registryDefId"] = registryDefId.ToString(CultureInfo.InvariantCulture),
            });

    /// <summary>
    /// Для обробників, що вимагають ГЛОБАЛЬНЕ право (опис, історія, чернетка): довідник,
    /// на який є заборона, — <c>404</c>, як неіснуючий.
    /// </summary>
    /// <param name="profile">Профіль, який повернула перевірка права.</param>
    /// <param name="registryDefId">Довідник, уже знайдений за кодом.</param>
    /// <param name="registryCode">Код з запиту — у відповіді той самий, що й для неіснуючого.</param>
    /// <exception cref="NotFoundException">Заборона на довідник — <c>ECR-REG-0404</c>.</exception>
    internal static void EnsureNotDenied(AccessProfile profile, int registryDefId, string registryCode)
    {
        if (IsDenied(profile, registryDefId))
        {
            throw NotFound(registryCode);
        }
    }

    /// <summary>
    /// Вимагає глобальне <paramref name="permission"/> АБО ресурсний грант
    /// рівня <paramref name="minLevel"/>+ на довідник <paramref name="registryDefId"/>,
    /// уже відомий викликачу (без додаткового походу в базу); явна заборона на нього
    /// відмовляє в обох випадках.
    /// </summary>
    /// <param name="access">Служба рішень про доступ.</param>
    /// <param name="currentUser">Поточний користувач запиту.</param>
    /// <param name="permission">Глобальне право — основний, ширший шлях.</param>
    /// <param name="minLevel">Мінімальний рівень гранта: <c>Read</c> для перегляду, <c>Write</c> для зміни даних.</param>
    /// <param name="registryDefId">Довідник, на який перевіряється ресурсний грант.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="AccessDeniedException">Анонім, або немає ні права, ні гранта.</exception>
    /// <exception cref="NotFoundException">Заборона на довідник — <c>ECR-REG-0404</c>.</exception>
    public static Task<AccessProfile> RequireAsync(
        IAccessDecisionService access,
        ICurrentUser currentUser,
        string permission,
        GrantLevel minLevel,
        int registryDefId,
        CancellationToken ct)
        => RequireCoreAsync(
            access, currentUser, permission, minLevel, _ => Task.FromResult<int?>(registryDefId), () => NotFound(registryDefId), ct);

    /// <summary>
    /// Те саме, що інша перевантаженість, але довідник невідомий заздалегідь
    /// (лише код довідника з маршруту) і резолвиться через <paramref name="registry"/>
    /// ЛІНИВО — тільки тоді, коли глобального права нема, або коли в профілі є заборони
    /// на довідники.
    /// </summary>
    /// <remarks>
    /// ⛔ Ледача резолюція — не оптимізація, а контракт: власник глобального
    /// права без жодної заборони на довідник не мусить платити зайвим походом у
    /// базу за довідник, якого й так показано УВЕСЬ (<c>RegistryEntriesAsOfValidationTests</c>
    /// фіксує це для суміжної перевірки — власна валідація параметра йде РАНІШЕ будь-
    /// якого <c>FindDefinitionAsync</c>, і ця перевірка не повинна ламати той
    /// порядок для того самого користувача).
    /// </remarks>
    /// <param name="access">Служба рішень про доступ.</param>
    /// <param name="currentUser">Поточний користувач запиту.</param>
    /// <param name="permission">Глобальне право — основний, ширший шлях.</param>
    /// <param name="minLevel">Мінімальний рівень гранта.</param>
    /// <param name="registry">
    /// Довідник за кодом з маршруту; викликається щонайбільше раз. Немає довідника —
    /// сама перевірка відмовляє (без права) чи мовчить (з правом): «не існує» лишається
    /// турботою виклику нижче за течією, як і раніше.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="AccessDeniedException">Анонім, або немає ні права, ні гранта.</exception>
    /// <exception cref="NotFoundException">Заборона на довідник — <c>ECR-REG-0404</c>.</exception>
    internal static Task<AccessProfile> RequireAsync(
        IAccessDecisionService access,
        ICurrentUser currentUser,
        string permission,
        GrantLevel minLevel,
        RegistryLookup registry,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(registry);

        return RequireCoreAsync(access, currentUser, permission, minLevel, registry.IdAsync, registry.NotFound, ct);
    }

    private static async Task<AccessProfile> RequireCoreAsync(
        IAccessDecisionService access,
        ICurrentUser currentUser,
        string permission,
        GrantLevel minLevel,
        Func<CancellationToken, Task<int?>> resolveRegistryDefId,
        Func<NotFoundException> hidden,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(currentUser);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(
                "ECR-AUTH-0401", "Потрібна автентифікація.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);

        if (profile.Has(permission))
        {
            // ⛔ S18: глобальне право НЕ перекриває заборону на довідник. Резолвер
            // питається лише коли заборони на довідники в профілі взагалі є: решта
            // користувачів не платить зайвим походом у базу.
            if (DeniedIds(profile) is { Count: > 0 } denied
                && await resolveRegistryDefId(ct).ConfigureAwait(false) is { } deniedId
                && denied.Contains(deniedId))
            {
                throw hidden();
            }

            return profile;
        }

        var registryDefId = await resolveRegistryDefId(ct).ConfigureAwait(false);

        // `LevelFor` уже враховує заборону: грант на довідник із забороною на нього — None.
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
