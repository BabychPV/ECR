using Ecr.Domain.Entities.Security;

namespace Ecr.Application.Security;

/// <summary>
/// Життєвий цикл bootstrap-адміністратора (ФВ-6.18, D-97, D-115).
/// </summary>
/// <remarks>
/// ⚠ Найтонше місце — не створення, а **невтручання**. Запис створюється
/// рівно один раз і лише коли адміністратора немає взагалі; повторний старт із
/// тією самою змінною середовища не має ані перезаписати пароль, ані
/// «полагодити» вимкнений запис. Інакше змінна оточення, яку забули прибрати з
/// конфігурації, назавжди лишається чинним входом.
/// </remarks>
public static class BootstrapAdmin
{
    /// <summary>Ім'я запису; фіксоване, щоб його було видно в аудиті.</summary>
    public const string UserName = "bootstrap";

    /// <summary>
    /// Роль, яку отримує запис при створенні.
    /// </summary>
    /// <remarks>
    /// ⛔ Це <b>окрема</b> роль, а не <c>SystemAdministrator</c>. Причина в
    /// правилі seed-а: жодна звичайна вбудована роль не отримує небезпечних
    /// прав (ФВ-6.12, D-40), а <see cref="AdminPermission"/> саме таке. Тому
    /// <c>SystemAdministrator</c> не вміє створити користувача, і bootstrap із
    /// цією роллю входив би в систему, якої не може налаштувати.
    ///
    /// ⛔ Назва мусить дослівно збігатися з роллю в <c>09-seed.sql</c>. До
    /// `A7-13` тут стояло <c>Administrator</c> — роль, якої seed не створює
    /// ніколи; старт падав уже після того, як усі тести проходили, бо в
    /// тестах роль створювала фікстура.
    /// </remarks>
    public const string RoleCode = "BootstrapAdministrator";

    /// <summary>Право, за яким доменний користувач вважається адміністратором.</summary>
    public const string AdminPermission = "Security.ManageUsers";

    /// <summary>Що робити зі станом системи на старті.</summary>
    public enum Outcome : byte
    {
        /// <summary>Створити запис.</summary>
        Create = 0,

        /// <summary>Нічого: запис уже є або система вже має адміністратора.</summary>
        Skip = 1,

        /// <summary>Вимкнути bootstrap: з'явився доменний адміністратор.</summary>
        Disable = 2,

        /// <summary>Пароля не задано — попередження, не помилка.</summary>
        Warn = 3,
    }

    /// <summary>Вирішує, що робити на старті.</summary>
    /// <param name="hasPassword">Чи задано <c>ECR_Bootstrap__Password</c>.</param>
    /// <param name="existing">Наявний bootstrap-запис; <c>null</c> — немає.</param>
    /// <param name="hasDomainAdmin">Чи є активний доменний адміністратор.</param>
    public static Outcome Decide(bool hasPassword, User? existing, bool hasDomainAdmin)
    {
        // ⚠ Поява доменного адміністратора вимикає bootstrap, але НЕ видаляє
        // його (D-97): запис лишається в аудиті як відповідь на питання
        // «звідки взявся перший адміністратор».
        if (hasDomainAdmin)
        {
            return existing is { IsActive: true } ? Outcome.Disable : Outcome.Skip;
        }

        // ⛔ Наявний запис не чіпається НІКОЛИ — навіть якщо змінна середовища
        // задана і відрізняється. Інакше кожен перезапуск скидав би пароль
        // працюючої системи на той, що лишився в конфігурації.
        if (existing is not null)
        {
            return Outcome.Skip;
        }

        // Відсутня змінна — попередження, а не помилка: це нормальний стан
        // контуру, де вхід уже переведено на домен (D-115).
        return hasPassword ? Outcome.Create : Outcome.Warn;
    }

    /// <summary>Створює запис із вимогою змінити пароль при першому вході.</summary>
    /// <param name="passwordHash">Готовий хеш; сам пароль сюди не потрапляє.</param>
    /// <param name="utcNow">Момент створення.</param>
    public static User Create(string passwordHash, DateTime utcNow)
        => User.CreateBootstrap(UserName, passwordHash, utcNow);
}
