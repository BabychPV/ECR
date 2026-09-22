using Ecr.Domain.Entities.Security;

namespace Ecr.Application.Ports;

/// <summary>Налаштування інтерфейсу користувача (<c>sec.UserPreference</c>, <c>BE-20</c>).</summary>
/// <remarks>⛔ Кожен метод бере <c>userId</c> явно: чужих налаштувань порт не бачить.</remarks>
public interface IUserPreferenceStore
{
    /// <summary>Усі налаштування користувача, за ключем.</summary>
    public Task<IReadOnlyList<UserPreference>> ListAsync(int userId, CancellationToken ct);

    /// <summary>Налаштування користувача за ключем; <c>null</c> — немає.</summary>
    public Task<UserPreference?> FindAsync(int userId, string key, CancellationToken ct);

    /// <summary>Скільки налаштувань у користувача.</summary>
    public Task<int> CountAsync(int userId, CancellationToken ct);

    /// <summary>Ставить нове налаштування в чергу на вставку.</summary>
    public void Add(UserPreference preference);

    /// <summary>Ставить налаштування в чергу на видалення.</summary>
    public void Remove(UserPreference preference);
}
