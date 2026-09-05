// src/Ecr.Domain/Entities/Security/ResourceGrant.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Domain.Entities.Security;

/// <summary>
/// Ресурсний грант із успадкуванням <c>Project → Sheet → Table → Column</c>.
/// </summary>
/// <remarks>
/// ⚠ <see cref="IsDeny"/> **виграє завжди**, на будь-якому рівні (ФВ-6.6). Це
/// свідома жорсткість: альтернатива «конкретніший рівень перемагає» дає
/// ситуації, де людина має доступ і ніхто не може пояснити чому.
/// <para>
/// Рядкового фільтра тут **немає** (D-92): найдрібніший рівень обмеження —
/// колонка, не рядок.
/// </para>
/// </remarks>
public sealed class ResourceGrant : Entity<int>
{
    private ResourceGrant() { }

    /// <summary>Створює грант.</summary>
    /// <param name="roleId">Роль, якій він належить.</param>
    /// <param name="resourceKind">Вид ресурсу.</param>
    /// <param name="resourceId">Ідентифікатор ресурсу.</param>
    /// <param name="level">Рівень доступу.</param>
    /// <param name="isDeny">Явна заборона; перекриває будь-який дозвіл.</param>
    /// <remarks>
    /// ⛔ Заборона задається САМЕ ТУТ і більше ніде. До того, як з'явилося
    /// керування грантами (`A7-22`), параметра не було зовсім: правило «IsDeny
    /// виграє завжди» було реалізоване в читанні, а записати заборону не міг
    /// ніхто — тобто половина правила існувала лише на папері.
    ///
    /// ⚠ Заборона з рівнем, вищим за <see cref="GrantLevel.None"/>, — не
    /// помилка і не суперечність: рівень у забороні просто не читається.
    /// Забороняти «частково» не можна за побудовою (ФВ-6.6).
    /// </remarks>
    public ResourceGrant(
        int roleId, ResourceKind resourceKind, int resourceId, GrantLevel level, bool isDeny = false)
    {
        RoleId = roleId;
        ResourceKind = resourceKind;
        ResourceId = resourceId;
        Level = level;
        IsDeny = isDeny;
    }

    public int RoleId { get; private set; }
    public ResourceKind ResourceKind { get; private set; }
    public int ResourceId { get; private set; }

    /// <summary>`None` → `Read` → `Write` → `Submit` → `Approve` → `Manage` (ФВ-6.13).</summary>
    public GrantLevel Level { get; private set; }

    /// <summary>Явна заборона. Перекриває будь-який дозвіл будь-якого рівня.</summary>
    public bool IsDeny { get; private set; }
}
