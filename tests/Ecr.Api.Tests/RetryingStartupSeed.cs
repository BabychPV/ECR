using Ecr.Api.Startup;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecr.Api.Tests;

/// <summary>
/// Seed на старті ХОСТА ТЕСТІВ: таймаут команди як у фікстури (300 с) і повтор на перехідних помилках
/// (<see cref="TransientSqlRetry.SeedErrors"/>: тайм-аут <c>-2</c>, взаємоблокування, замки).
/// </summary>
/// <remarks>
/// ⛔ B166: під навантаженням машини сід на старті <c>WebApplicationFactory</c> падав «Execution Timeout Expired» із
/// таймаутом застосунку (<c>Database:CommandTimeoutSeconds</c>, 60 с) — і з ним усі тести класу, жоден асерт не впав.
/// Той самий клас, що <c>B133</c> у фікстурі (там сід уже має 300 с). Продуктивний старт цієї служби не має й кличе
/// <see cref="SeedRunner"/> як раніше (<see cref="IStartupSeedRunner"/>). Сід — одна транзакція, тож невдала спроба
/// відкочується цілком, і повтор безпечний.
/// </remarks>
public sealed class RetryingStartupSeed : IStartupSeedRunner
{
    /// <summary>Таймаут команди сіду в хості тестів, с (= <c>SqlServerFixture.SetupCommandTimeoutSeconds</c>).</summary>
    public const int CommandTimeoutSeconds = 300;

    /// <summary>До 3 спроб; стеля — два повні таймаути з паузами.</summary>
    public static readonly RetryPolicy Policy = new(
        MaxAttempts: 3,
        BaseDelay: TimeSpan.FromSeconds(2),
        MaxDelay: TimeSpan.FromSeconds(8),
        Budget: TimeSpan.FromSeconds((2 * CommandTimeoutSeconds) + 60));

    /// <summary>Реєструє службу в хості тестів (кличуть усі фабрики <c>WebApplicationFactory&lt;Program&gt;</c>).</summary>
    /// <param name="services">Служби хоста.</param>
    public static void Register(IServiceCollection services) => services.AddSingleton<IStartupSeedRunner, RetryingStartupSeed>();

    /// <inheritdoc />
    public async Task RunAsync(EcrDbContext db, ILogger logger, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        // ⚠ Лише на час сіду: далі той самий контекст старту працює з таймаутом застосунку.
        var previous = db.Database.GetCommandTimeout();
        db.Database.SetCommandTimeout(CommandTimeoutSeconds);
        try
        {
            await TransientSqlRetry.RunAsync(
                    "seed на старті хоста тестів",
                    _ => new SeedRunner(db, logger).RunAsync(ct),
                    TransientSqlRetry.SqlClassifier(TransientSqlRetry.SeedErrors),
                    Policy,
                    ct: ct)
                .ConfigureAwait(false);
        }
        finally
        {
            db.Database.SetCommandTimeout(previous);
        }
    }
}
