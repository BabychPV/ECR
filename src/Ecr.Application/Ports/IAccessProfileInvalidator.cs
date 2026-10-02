namespace Ecr.Application.Ports;

/// <summary>
/// Скидання кешу профілів доступу ПРОЦЕСУ без зміни штампа безпеки — для змін
/// грантів, яких відбиток груп у ключі кешу не бачить (перенос версії шаблону).
/// </summary>
/// <remarks>
/// ⚠ Кеш процесний: інші інстанси додатку скидаються лише за TTL
/// (<c>Cache:AccessProfileSlidingMinutes</c>, 60 хв).
/// </remarks>
public interface IAccessProfileInvalidator
{
    /// <summary>Видаляє ВСІ записи профілю користувача — за будь-якого штампа й відбитку груп.</summary>
    /// <param name="userId">Користувач.</param>
    public void InvalidateUser(int userId);

    /// <summary>Скидає весь кеш профілів (fail-closed).</summary>
    public void InvalidateAll();
}
