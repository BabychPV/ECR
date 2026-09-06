// src/Ecr.Domain/Entities/Security/User.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;

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

    /// <summary>
    /// Чи отримує ця людина алерти про збої (<c>D-125</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ Адресати — **дані, а не конфігурація**: список у змінних оточення
    /// довелося б міняти розгортанням щоразу, коли хтось іде у відпустку.
    ///
    /// ⛔ Прапорець без пошти беззмістовний і тому заборонений: він виглядав би
    /// як налаштований адресат, якому нічого не надсилається.
    /// </remarks>
    public bool ReceivesAlerts { get; private set; }

    /// <summary>Задає адресу для сповіщень.</summary>
    /// <param name="email">Адреса; <c>null</c> або порожньо — прибрати.</param>
    /// <remarks>
    /// ⛔ Сетера не було ЖОДНОГО, і поле не присвоювалося ніде в системі.
    /// Наслідок мовчазний і повний: <c>NotificationJob</c> завжди отримував
    /// порожній перелік адресатів, тобто сповіщення (<c>ФВ-12</c>) не
    /// надходили нікому — а перемикач «отримувати сповіщення» в інтерфейсі
    /// був вічно неактивним і виглядав як налаштування, яке просто вимкнули.
    ///
    /// ⚠ Адреса НЕ перевіряється на існування. Перевірка формату тут була б
    /// самообманом: адреса, яка виглядає правильно і нікому не належить,
    /// мовчить так само. Достовірність дає лише перший надісланий лист.
    /// </remarks>
    public void SetEmail(string? email)
        => Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();

    /// <summary>Вмикає або вимикає отримання алертів.</summary>
    /// <param name="value">Чи отримує.</param>
    /// <exception cref="Abstractions.DomainException">Увімкнено без пошти.</exception>
    public void SetReceivesAlerts(bool value)
    {
        // ⛔ Родина USR, а не ROW. ROW — це помилки РЯДКА ТАБЛИЦІ ДОКУМЕНТА, і
        // клієнт маршрутизує саме за родиною: відмова облікового запису
        // приходила в обробник помилок сітки, де для неї немає ні місця, ні
        // тексту, — користувач бачив би її як збій редактора документа.
        if (value && string.IsNullOrWhiteSpace(Email))
        {
            throw new Abstractions.DomainException(
                ErrorCodes.UserInvalid,
                $"Користувач «{UserName}» не має пошти: вмикати отримання алертів немає куди.");
        }

        ReceivesAlerts = value;
    }
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

    /// <summary>
    /// Створює технічний запис первинного налаштування (ФВ-6.18, D-97, D-115).
    /// </summary>
    /// <param name="userName">Ім'я запису; фіксоване, щоб було видно в аудиті.</param>
    /// <param name="passwordHash">Готовий хеш разового пароля.</param>
    /// <param name="utcNow">Момент створення.</param>
    /// <remarks>
    /// ⚠ Фабрика в домені, а не збирання полів у use-case. Три обмеження —
    /// <see cref="IsBootstrapAdmin"/>, <see cref="MustChangePassword"/> і
    /// відсутність автора — тримають цей запис безпечним; розкидані по
    /// викликачах, вони рано чи пізно розійдуться, і з'явиться другий, «майже
    /// такий самий» спосіб завести адміністратора.
    /// </remarks>
    public static User CreateBootstrap(string userName, string passwordHash, DateTime utcNow)
    {
        var user = new User(userName, "Bootstrap administrator", AuthProvider.Local)
        {
            IsBootstrapAdmin = true,
            CreatedAt = utcNow,

            // Автора немає навмисно: запис створює сама система (D-97), і
            // вигаданий CreatedByUserId зробив би аудит неправдивим.
            CreatedByUserId = null,
        };

        user.SetPassword(passwordHash);

        // ПІСЛЯ SetPassword: той знімає прапорець, бо у звичайному сценарії
        // зміна пароля і є виконанням вимоги.
        user.RequirePasswordChange();

        return user;
    }

    /// <summary>Заводить доменного користувача при першому вході (ФВ-6.2).</summary>
    /// <param name="userName">Ім'я входу.</param>
    /// <param name="displayName">Ім'я для показу.</param>
    /// <param name="windowsSid">SID у каталозі.</param>
    /// <param name="utcNow">Момент створення.</param>
    /// <remarks>
    /// Прав у нього немає жодних, поки адміністратор не призначить роль:
    /// членство в домені — це ідентичність, а не повноваження.
    /// </remarks>
    public static User CreateDomain(string userName, string displayName, string windowsSid, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowsSid);

        return new User(userName, displayName, AuthProvider.Windows)
        {
            WindowsSid = windowsSid,
            CreatedAt = utcNow,
        };
    }

    /// <summary>Змінює пароль і **обов'язково** крутить <c>SecurityStamp</c>.</summary>
    /// <param name="passwordHash">Готовий хеш; сам пароль сюди не потрапляє ніколи.</param>
    public void SetPassword(string passwordHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);

        PasswordHash = passwordHash;
        MustChangePassword = false;

        // ⚠ Без нового штампа старі сесії лишаться дійсними після зміни
        // пароля — і це буде тихою дірою (ФВ-6.7). Саме тому штамп крутиться
        // ТУТ, а не в use-case: забути його в одному з викликів неможливо.
        SecurityStamp = Guid.NewGuid().ToString("N");
    }

    /// <summary>Вимагає зміни пароля при наступному вході.</summary>
    /// <remarks>Ставиться bootstrap-запису і при адміністративному скиданні.</remarks>
    public void RequirePasswordChange() => MustChangePassword = true;

    /// <summary>Робить усі поточні сесії недійсними негайно (ФВ-6.7).</summary>
    /// <remarks>
    /// ⚠ Викликається при КОЖНІЙ зміні ролей, грантів або стану запису.
    /// <c>SecurityStamp</c> перевіряється на кожен запит, тому новий штамп —
    /// це і є «відкликати доступ зараз», а не «коли скінчиться cookie».
    /// Без нього відкликана роль жила б до кінця сесії, і побачити це було б
    /// нізвідки: у логах усе виглядало б штатно.
    /// </remarks>
    public void RefreshSecurityStamp() => SecurityStamp = Guid.NewGuid().ToString("N");

    /// <summary>Чи заблокований запис на момент <paramref name="utcNow"/>.</summary>
    /// <param name="utcNow">Поточний момент.</param>
    public bool IsLockedOut(DateTime utcNow) => LockedUntil is { } until && until > utcNow;

    /// <summary>Рахує невдалу спробу входу і блокує запис після межі (ФВ-6.4a).</summary>
    /// <param name="maxFailedAttempts">Скільки спроб дозволено.</param>
    /// <param name="lockoutMinutes">На скільки блокувати.</param>
    /// <param name="utcNow">Поточний момент.</param>
    /// <returns><c>true</c>, якщо цією спробою запис заблоковано.</returns>
    /// <remarks>
    /// ⚠ Лічильник живе в домені, а не в обробнику входу. Інакше кожен новий
    /// спосіб автентифікації довелося б навчати рахувати спроби заново — і
    /// один із них неминуче навчити забули б.
    /// </remarks>
    public bool RegisterFailedAttempt(int maxFailedAttempts, int lockoutMinutes, DateTime utcNow)
    {
        FailedAttempts++;

        if (maxFailedAttempts <= 0 || FailedAttempts < maxFailedAttempts)
        {
            return false;
        }

        LockedUntil = utcNow.AddMinutes(lockoutMinutes <= 0 ? 15 : lockoutMinutes);
        return true;
    }

    /// <summary>Скидає лічильник після вдалого входу.</summary>
    public void RegisterSuccessfulLogin()
    {
        FailedAttempts = 0;
        LockedUntil = null;
    }

    /// <summary>Вимикає bootstrap-запис. **Не видаляє**: він потрібен в аудиті.</summary>
    public void DisableAsBootstrap()
    {
        IsActive = false;
        SecurityStamp = Guid.NewGuid().ToString("N");

        // IsBootstrapAdmin лишається як є: за ним потім видно, звідки взявся
        // перший адміністратор. Видалення запису стерло б цю відповідь.
    }
}
