// src/Ecr.Domain/Entities/Security/Permission.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Security;

/// <summary>
/// Функціональне право. Каталог фіксований і приходить із seed — на відміну
/// від ролей, права оголошує код, бо саме він їх перевіряє.
/// </summary>
public sealed class Permission : Entity<string>
{
    private Permission() { }

    public Permission(string code, string group, LocalizedText nameL10n, bool isDangerous)
    {
        Id = code;
        Group = group;
        NameL10n = nameL10n;
        IsDangerous = isDangerous;
    }

    /// <summary>Група для UI: `Template`, `Document`, `Calculation`, `Security`.</summary>
    public string Group { get; private set; } = null!;

    /// <summary>Підпис права в UI. У схемі <c>NOT NULL</c> (`Q-040`).</summary>
    public LocalizedText NameL10n { get; private set; } = null!;

    /// <summary>
    /// Небезпечне право (ФВ-6.12, D-40): у складені ролі **не входить**, seed
    /// лишає ролі порожніми за ним навмисно. Це не пропуск конфігурації.
    /// </summary>
    public bool IsDangerous { get; private set; }
}
