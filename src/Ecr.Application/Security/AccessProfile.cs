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
    /// Профіль побудований у сеансі симуляції «очима користувача» (D-96).
    /// Права беруться повністю від <see cref="SimulatedForUserId"/>, але
    /// <b>будь-яка</b> дія запису відхиляється з
    /// <see cref="EditDenyReason.SimulationReadOnly"/>. Клієнт зобов'язаний
    /// показувати банер увесь час, поки прапорець стоїть.
    /// </summary>
    public bool IsSimulation { get; init; }

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
