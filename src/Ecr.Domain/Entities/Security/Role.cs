// src/Ecr.Domain/Entities/Security/Role.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Security;

/// <summary>
/// Роль — **дані, а не enum** (ФВ-6.6). Адміністратор створює роль і набирає
/// їй права; новий обов'язок не потребує релізу.
/// </summary>
public sealed class Role : Entity<int>
{
    private Role() { }

    public Role(EcrCode code, LocalizedText name)
    {
        Code = code.Value;
        NameL10n = name;
        IsActive = true;
    }

    public string Code { get; private set; } = null!;
    public LocalizedText NameL10n { get; private set; } = null!;

    /// <summary>Вбудована роль із seed. Видаленню не підлягає, зміні — так.</summary>
    public bool IsBuiltIn { get; private set; }

    public bool IsActive { get; private set; }
}
