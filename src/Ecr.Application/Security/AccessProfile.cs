// src/Ecr.Application/Security/AccessProfile.cs

using Ecr.Domain.Enums;

namespace Ecr.Application.Security;

/// <summary>
/// Ефективні права користувача, обчислені <b>раз на сесію</b> (ФВ-6.10).
/// Резолвити права на кожну комірку — гарантована смерть продуктивності:
/// бюджет відкриття таблиці 500×60 дає на права 50 мс на весь запит.
/// </summary>
public sealed class AccessProfile
{
    /// <summary>Ключ кешу: змінюється при зміні ролей або пароля.</summary>
    public required string CacheKey { get; init; }

    public required int UserId { get; init; }
    public required string SecurityStamp { get; init; }

    /// <summary>Функціональні права (<c>sec.Permission.Code</c>).</summary>
    public required IReadOnlySet<string> Permissions { get; init; }

    /// <summary>
    /// Ресурсні гранти: ключ — <c>"{ResourceKind}:{ResourceId}"</c>.
    /// Успадкування <c>Project → Sheet → Table → Column</c> уже розгорнуте.
    /// </summary>
    public required IReadOnlyDictionary<string, GrantLevel> Grants { get; init; }

    /// <summary>Явні заборони. <c>IsDeny</c> виграє завжди, на будь-якому рівні (ФВ-6.6).</summary>
    public required IReadOnlySet<string> Denies { get; init; }

    /// <summary>
    /// Ролі користувача. Потрібні правилам доступу до періоду, обмеженим
    /// роллю (<c>ФВ-2.15</c>).
    /// </summary>
    /// <remarks>
    /// ⛔ <c>required</c>, а не порожній набір за замовчуванням. «Забув
    /// заповнити» тут означає «правило, обмежене роллю, не спрацювало», а не
    /// спрацювало воно — це ДОЗВІЛ. Тихий дозвіл у моделі доступу коштує
    /// дорожче за помилку складання.
    /// </remarks>
    public required IReadOnlySet<int> RoleIds { get; init; }

    /// <summary>
    /// Ролі з призначень БЕЗ області дії (ФВ-6.14); <c>null</c> — областей у
    /// користувача немає, і це те саме, що <see cref="RoleIds"/>.
    /// </summary>
    public IReadOnlySet<int>? UnscopedRoleIds { get; init; }

    /// <summary>
    /// Те, що ролі з ОБЛАСТЮ дії дають у конкретному проєкті: ключ —
    /// <c>ProjectId</c> (ФВ-6.14).
    /// </summary>
    /// <remarks>
    /// ⛔ Гранти на аркуш, таблицю й колонку ролі з областю НЕ потрапляють у
    /// <see cref="Grants"/>: ідентифікатори аркушів спільні для всіх проєктів
    /// шаблону, тож у спільній мапі «аркуш S у проєкті A» діяв би й у
    /// проєкті B. Грант на сам проєкт області — навпаки, у <see cref="Grants"/>
    /// (ключ і так називає проєкт); на проєкт поза областю — нікуди.
    ///
    /// ⚠ Порожньо за замовчуванням — безпечний бік: профіль, складений без
    /// цього поля, просто не має прав з областю.
    /// </remarks>
    public IReadOnlyDictionary<int, ScopedProjectAccess> Scoped { get; init; } = NoScoped;

    private static readonly IReadOnlyDictionary<int, ScopedProjectAccess> NoScoped
        = new Dictionary<int, ScopedProjectAccess>();

    /// <summary>
    /// Ролі, чинні в проєкті: без області — скрізь, з областю — лише в ній.
    /// </summary>
    /// <param name="projectId">Проєкт.</param>
    /// <remarks>
    /// ⛔ Для рішень у межах проєкту (роль кроку маршруту, правила періоду,
    /// обмежені роллю) — саме цей метод, а не <see cref="RoleIds"/>: там ролі
    /// з областю в УСІХ проєктах.
    /// </remarks>
    public IReadOnlySet<int> RoleIdsIn(int projectId)
        => Scoped.TryGetValue(projectId, out var scoped) ? scoped.RoleIds : UnscopedRoleIds ?? RoleIds;

    /// <summary>
    /// Профіль побудований у сеансі симуляції «очима користувача» (D-96).
    /// Права беруться повністю від <see cref="SimulatedForUserId"/>, але
    /// <b>будь-яка</b> дія запису відхиляється з
    /// <see cref="EditDenyReason.SimulationReadOnly"/>. Клієнт зобов'язаний
    /// показувати банер увесь час, поки прапорець стоїть.
    /// </summary>
    public bool IsSimulation { get; init; }

    /// <summary>
    /// Профіль технічного автора задачі інтеграції: пише значення збору в
    /// будь-який проєкт БЕЗ грантів, але в межах усіх заборон (<see cref="EditRules"/>).
    /// </summary>
    /// <remarks>
    /// ⛔ Ставиться ЛИШЕ поверх кешованого профілю, у мить побудови, з
    /// контексту виконання (<c>ICurrentUser.IsIntegrationJob</c>) — ніколи не
    /// кладеться в кеш профілів за <c>userId</c>. Інакше хто перший зігрів би
    /// кеш (HTTP чи задача), той і визначив би права іншого.
    ///
    /// ⚠ Не дає ні функціональних прав (<see cref="Permissions"/> порожні), ні
    /// подання, затвердження чи повернення в роботу: інтеграція лише ПИШЕ
    /// значення, відповідальність за звіт лишається на людині.
    /// </remarks>
    public bool IsIntegrationWriter { get; init; }

    /// <summary>Чиїми очима; <c>null</c> поза симуляцією.</summary>
    public int? SimulatedForUserId { get; init; }

    /// <summary>Хто симулює. Автор в аудиті — саме він, не суб'єкт.</summary>
    public int? SimulationActorUserId { get; init; }

    /// <summary>Чи має користувач функціональне право.</summary>
    public bool Has(string permissionCode) => Permissions.Contains(permissionCode);

    /// <summary>Ефективний рівень гранта на ресурс з урахуванням заборон.</summary>
    public GrantLevel LevelFor(ResourceKind kind, int resourceId)
    {
        var key = $"{kind}:{resourceId}";
        return Resolve(Denies.Contains(key), Grants.TryGetValue(key, out var level) ? level : null);
    }

    /// <summary>
    /// Єдине місце, де живе правило «заборона виграє на будь-якому рівні
    /// (ФВ-6.6), інакше — грант, інакше — <see cref="GrantLevel.None"/>».
    /// </summary>
    /// <remarks>
    /// І <see cref="LevelFor"/> над доменною формою (<see cref="Grants"/>/
    /// <see cref="Denies"/>), і клієнтська проєкція
    /// <see cref="GetCurrentUserHandler.LevelForProject"/> над
    /// серіалізованим у рядки <c>CurrentUserView</c> викликають саме цей
    /// метод. До <c>Q-188</c> це були дві незалежні реалізації одного
    /// правила над різними формами тих самих даних — правка порядку
    /// перевірки чи формату ключа в одній не гарантовано потрапляла б у
    /// другу (той самий клас дефекту, що вже стався з <c>DenyReason</c> на
    /// клієнті до <c>A7-02</c>). Кожен виклик відповідає лише за побудову
    /// ключа й пошук у своїй формі даних; саме рішення — тут і тільки тут.
    /// </remarks>
    internal static GrantLevel Resolve(bool isDenied, GrantLevel? grant)
        => isDenied ? GrantLevel.None : grant ?? GrantLevel.None;
}

/// <summary>Що ролі з областю дії дають в одному проєкті (ФВ-6.14).</summary>
/// <param name="Grants">Гранти на аркуш/таблицю/колонку: ключ <c>"{ResourceKind}:{ResourceId}"</c>.</param>
/// <param name="Denies">Заборони на аркуш/таблицю/колонку.</param>
/// <param name="RoleIds">Ролі, чинні в проєкті: без області плюс ті, чия область його містить.</param>
public sealed record ScopedProjectAccess(
    IReadOnlyDictionary<string, GrantLevel> Grants,
    IReadOnlySet<string> Denies,
    IReadOnlySet<int> RoleIds);
