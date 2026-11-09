namespace Ecr.Application.Ports;

/// <summary>
/// Чинна межа бездіяльності сеансу (<c>D-471</c>).
/// </summary>
/// <remarks>
/// ⚠ Читається на КОЖНОМУ автентифікованому запиті (перевірка cookie), тому значення кешується коротко;
/// зміна з екрана скидає кеш цього вузла одразу (<see cref="Invalidate"/>), інших — за строком кешу.
/// </remarks>
public interface IIdleTimeoutPolicy
{
    /// <summary>Найменше допустиме значення в хвилинах (5; стенд може знизити конфігурацією).</summary>
    public int MinimumMinutes { get; }

    /// <summary>Чинне значення в хвилинах (збережене або за замовчуванням).</summary>
    /// <param name="ct">Токен скасування.</param>
    public ValueTask<int> GetMinutesAsync(CancellationToken ct);

    /// <summary>Скидає кеш після зміни значення.</summary>
    public void Invalidate();
}

/// <summary>Сховище налаштувань сеансу (<c>sys_ecr.SystemSetting</c>).</summary>
public interface ISessionSettingsStore
{
    /// <summary>Збережене значення; <c>null</c> — рядка немає або він не число.</summary>
    /// <param name="ct">Токен скасування.</param>
    public Task<int?> ReadIdleTimeoutMinutesAsync(CancellationToken ct);

    /// <summary>Записує значення (вставка або оновлення) у поточній транзакції.</summary>
    /// <param name="minutes">Хвилини.</param>
    /// <param name="byUserId">Хто змінив.</param>
    /// <param name="at">Момент у UTC.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task WriteIdleTimeoutMinutesAsync(int minutes, int byUserId, DateTime at, CancellationToken ct);
}
