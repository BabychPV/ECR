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

    public ResourceGrant(int roleId, ResourceKind resourceKind, int resourceId, GrantLevel level)
    {
        RoleId = roleId;
        ResourceKind = resourceKind;
        ResourceId = resourceId;
        Level = level;
    }

    public int RoleId { get; private set; }
    public ResourceKind ResourceKind { get; private set; }
    public int ResourceId { get; private set; }

    /// <summary>`None` → `Read` → `Write` → `Submit` → `Approve` → `Manage` (ФВ-6.13).</summary>
    public GrantLevel Level { get; private set; }

    /// <summary>Явна заборона. Перекриває будь-який дозвіл будь-якого рівня.</summary>
    public bool IsDeny { get; private set; }
}
