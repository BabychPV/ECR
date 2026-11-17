using Ecr.Infrastructure.Persistence;

namespace Ecr.Api.Startup;

/// <summary>
/// Шов кроку 4 (seed) послідовності старту — ЛИШЕ для хостів тестів.
/// </summary>
/// <remarks>
/// ⛔ B166. Продуктивний застосунок цю службу НЕ реєструє: без неї <see cref="StartupSequence"/> кличе
/// <see cref="SeedRunner"/> рівно так, як і раніше (той самий таймаут контексту, без повтору). Хост тестів
/// (<c>WebApplicationFactory</c>) підставляє реалізацію з довшим таймаутом і повтором на перехідних помилках: під
/// навантаженням машини сід на старті хоста падав «Execution Timeout Expired», хоча жоден тест іще не почався.
/// </remarks>
public interface IStartupSeedRunner
{
    /// <summary>Виконує seed на старті.</summary>
    /// <param name="db">Контекст старту.</param>
    /// <param name="logger">Журнал старту.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Завдання.</returns>
    public Task RunAsync(EcrDbContext db, ILogger logger, CancellationToken ct);
}
