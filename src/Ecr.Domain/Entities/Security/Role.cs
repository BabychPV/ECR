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

    /// <summary>Змінює код і, за потреби, назву (директива №15, BE-14).</summary>
    /// <param name="code">Новий код.</param>
    /// <param name="name">Нова назва; <c>null</c> — лишити чинну.</param>
    /// <remarks>
    /// ⚠ Призначення й гранти тримаються за <c>Id</c>, тож перейменування їх
    /// не чіпає. Заборону для вбудованих ролей тримає обробник: на їхні коди
    /// посилається сід, і саме він знає, якою відмовою відповісти.
    /// </remarks>
    public void Rename(EcrCode code, LocalizedText? name)
    {
        Code = code.Value;
        if (name is not null)
        {
            NameL10n = name;
        }
    }
}
