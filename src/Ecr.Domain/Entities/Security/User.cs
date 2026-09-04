// src/Ecr.Domain/Entities/Security/User.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Domain.Entities.Security;

/// <summary>
/// Користувач. Один із двох провайдерів, але **одна ідентичність застосунку**
/// (ФВ-6.1, ФВ-6.2): нижче рівня входу різниці немає ніде.
/// </summary>
/// <remarks>
/// <see cref="WindowsSid"/> **або** <see cref="PasswordHash"/>, ніколи обидва —
/// це перевіряє <c>CK_User_Provider</c> у базі. Саме тому автором дії в аудиті
/// є <c>Id</c>, а не SID: у локального користувача SID не існує (D-37, D-86).
/// </remarks>
public sealed class User : Entity<int>
{
    private User() { }

    public User(string userName, string displayName, AuthProvider provider)
    {
        UserName = userName;
        DisplayName = displayName;
        Provider = provider;
        SecurityStamp = Guid.NewGuid().ToString("N");
        IsActive = true;
    }

    public string UserName { get; private set; } = null!;
    public string DisplayName { get; private set; } = null!;
    public string? Email { get; private set; }
    public AuthProvider Provider { get; private set; }

    /// <summary>Лише для доменних. Це **не** авторство, а зіставлення з каталогом.</summary>
    public string? WindowsSid { get; private set; }

    public string? PasswordHash { get; private set; }
    public int? PasswordPolicyId { get; private set; }

    /// <summary>Перевіряється на КОЖЕН запит: відкликання прав діє негайно (ФВ-6.7).</summary>
    public string SecurityStamp { get; private set; } = null!;

    public int FailedAttempts { get; private set; }
    public DateTime? LockedUntil { get; private set; }

    /// <summary>Пароль виданий разово; доки прапорець стоїть — лише зміна пароля і вихід (ФВ-6.18).</summary>
    public bool MustChangePassword { get; private set; }

    /// <summary>Технічний запис первинного налаштування (D-97, D-115). Один на систему.</summary>
    public bool IsBootstrapAdmin { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>Момент створення запису.</summary>
    /// <remarks>
    /// Разом із <see cref="CreatedByUserId"/> відповідає на питання «хто і коли
    /// завів цього користувача». Для bootstrap-запису автора немає — його
    /// створює сама система (D-97), тому поле обнуляється.
    /// </remarks>
    public DateTime CreatedAt { get; private set; }

    /// <summary>Автор створення; <c>null</c> для bootstrap-запису.</summary>
    public int? CreatedByUserId { get; private set; }

    /// <summary>Змінює пароль і **обов'язково** крутить <c>SecurityStamp</c>.</summary>
    public void SetPassword(string passwordHash)
        => throw new NotImplementedException(
            "TODO: PasswordHash = hash; MustChangePassword = false; " +
            "SecurityStamp = новий GUID — інакше старі сесії лишаться дійсними " +
            "після зміни пароля, і це буде тихою дірою (ФВ-6.7).");

    /// <summary>Вимикає bootstrap-запис. **Не видаляє**: він потрібен в аудиті.</summary>
    public void DisableAsBootstrap()
        => throw new NotImplementedException(
            "TODO: IsActive = false, SecurityStamp = новий. IsBootstrapAdmin " +
            "лишити як є — за ним потім видно, звідки взявся перший адміністратор.");
}
