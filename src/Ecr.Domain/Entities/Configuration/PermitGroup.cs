// src/Ecr.Domain/Entities/Configuration/PermitGroup.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// Група типів дозволів («Вода» = WATER_DISCHARGE + WATER_INTAKE + WATER_GENERAL): ім'я, опис і склад типів.
/// Група є ціллю Lookup через прихований реєстр <see cref="RegistryDefId"/> (<c>PRMG_&lt;КОД&gt;</c>) без записів
/// (Permits, PRMG).
/// </summary>
/// <remarks>
/// <para>
/// ⚠ Реєстр групи - лише «ідентичність цілі»: <c>ColumnDef.LookupRegistryDefId</c> вказує на нього, а записи, що
/// належать цілі, - це дозволи типів зі складу групи. Перевірку «запис належить цілі» лишає порівнянням
/// <c>RegistryDefId</c>; тільки при НЕзбігу кличеться <c>IPermitGroupResolver.Accepts</c>.
/// </para>
/// <para>
/// ⛔ Використану групу не видаляють - лише деактивують (<see cref="IsActive"/>). Склад типів у групі змінюється
/// завжди; уже обрані значення лишаються в комірках.
/// </para>
/// </remarks>
public sealed class PermitGroup : Entity<int>
{
    private readonly List<PermitGroupMember> _members = [];

    private PermitGroup() { }

    /// <summary>Створює активну групу без типів.</summary>
    /// <param name="code">Код групи; унікальний (<c>UQ_PermitGroup</c>), не змінюється.</param>
    /// <param name="name">Назва мовами каталогу.</param>
    /// <param name="description">Опис мовами каталогу (показується під час вибору цілі); порожній - без опису.</param>
    /// <param name="registryDefId">Прихований реєстр-«ідентичність» групи.</param>
    /// <param name="userId">Автор.</param>
    /// <param name="utcNow">Момент створення в UTC.</param>
    public PermitGroup(EcrCode code, LocalizedText name, LocalizedText description, int registryDefId, int userId, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(registryDefId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);
        EnsureUtc(utcNow);

        Code = code.Value;
        NameL10n = name;
        DescriptionL10n = description;
        RegistryDefId = registryDefId;
        IsActive = true;
        CreatedAt = utcNow;
        CreatedByUserId = userId;
        UpdatedAt = utcNow;
        UpdatedByUserId = userId;
    }

    /// <summary>Код групи; не змінюється.</summary>
    public string Code { get; private set; } = null!;

    /// <summary>Назва мовами каталогу.</summary>
    public LocalizedText NameL10n { get; private set; } = null!;

    /// <summary>Опис мовами каталогу; порожній - без опису.</summary>
    public LocalizedText DescriptionL10n { get; private set; } = null!;

    /// <summary>Прихований реєстр-«ідентичність» групи (<c>PRMG_&lt;КОД&gt;</c>).</summary>
    public int RegistryDefId { get; private set; }

    /// <summary>Чи групу можна обрати новою ціллю; деактивована лишається читабельною.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Момент створення в UTC.</summary>
    public DateTime CreatedAt { get; private set; }

    /// <summary>Хто створив.</summary>
    public int CreatedByUserId { get; private set; }

    /// <summary>Момент останньої зміни в UTC.</summary>
    public DateTime UpdatedAt { get; private set; }

    /// <summary>Хто змінив востаннє.</summary>
    public int UpdatedByUserId { get; private set; }

    /// <summary>Токен оптимістичної конкуренції (<c>If-Match</c>); ставить база.</summary>
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>Склад типів у порядку показу.</summary>
    public IReadOnlyList<PermitGroupMember> Members => _members;

    /// <summary>Змінює назву, опис і активність (код і реєстр лишаються).</summary>
    /// <param name="name">Назва.</param>
    /// <param name="description">Опис.</param>
    /// <param name="isActive">Активність.</param>
    /// <param name="userId">Автор.</param>
    /// <param name="utcNow">Момент у UTC.</param>
    public void Update(LocalizedText name, LocalizedText description, bool isActive, int userId, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);
        EnsureUtc(utcNow);

        NameL10n = name;
        DescriptionL10n = description;
        IsActive = isActive;
        MarkChanged(userId, utcNow);
    }

    /// <summary>Замінює склад типів цілком; порядок - за позицією в переданому списку.</summary>
    /// <param name="permitTypeDefIds">Типи без повторів.</param>
    /// <param name="userId">Автор.</param>
    /// <param name="utcNow">Момент у UTC.</param>
    /// <exception cref="ArgumentException">Тип повторюється.</exception>
    public void ReplaceMembers(IReadOnlyList<int> permitTypeDefIds, int userId, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(permitTypeDefIds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);
        EnsureUtc(utcNow);

        if (permitTypeDefIds.Distinct().Count() != permitTypeDefIds.Count)
        {
            throw new ArgumentException("Тип дозволу в групі не може повторюватися.", nameof(permitTypeDefIds));
        }

        _members.RemoveAll(m => !permitTypeDefIds.Contains(m.PermitTypeDefId));

        for (var index = 0; index < permitTypeDefIds.Count; index++)
        {
            var typeId = permitTypeDefIds[index];
            var existing = _members.Find(m => m.PermitTypeDefId == typeId);
            if (existing is null)
            {
                _members.Add(new PermitGroupMember(typeId, index + 1));
            }
            else
            {
                existing.MoveTo(index + 1);
            }
        }

        MarkChanged(userId, utcNow);
    }

    private void MarkChanged(int userId, DateTime utcNow)
    {
        UpdatedAt = utcNow;
        UpdatedByUserId = userId;
    }

    private static void EnsureUtc(DateTime utcNow)
    {
        if (utcNow.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Момент має бути в UTC.", nameof(utcNow));
        }
    }
}

/// <summary>Тип дозволу в складі групи (<c>cfg.PermitGroupMember</c>); ключ - пара (група, тип).</summary>
public sealed class PermitGroupMember
{
    private PermitGroupMember() { }

    internal PermitGroupMember(int permitTypeDefId, int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(permitTypeDefId);
        PermitTypeDefId = permitTypeDefId;
        Ordinal = ordinal;
    }

    /// <summary>Група.</summary>
    public int PermitGroupId { get; private set; }

    /// <summary>Тип дозволу.</summary>
    public int PermitTypeDefId { get; private set; }

    /// <summary>Позиція в складі групи (порядок показу).</summary>
    public int Ordinal { get; private set; }

    internal void MoveTo(int ordinal) => Ordinal = ordinal;
}
