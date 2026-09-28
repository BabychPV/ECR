// tests/Ecr.Api.Tests/PeriodStateStartupLockTests.cs
using Ecr.Api.Startup;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Стартове вирівнювання станів періодів і погодинний прогін
/// <see cref="PeriodStateJob"/> конкурують за ОДИН лок.
/// </summary>
/// <remarks>
/// ⛔ Було дві назви: старт брав <c>Ecr.Job.PeriodStateJob:startup</c>, а тик
/// розкладу — <c>Ecr.Job.{ключ задачі}</c>
/// (<see cref="QuartzJobScheduler.LockNameOf"/>). Тобто старт одного інстанса й
/// погодинний прогін іншого бігли паралельно над тими самими періодами.
/// <para>
/// ⚠ Лок тут тримається під ім'ям, яке бере АДАПТЕР на тик розкладу
/// (<see cref="QuartzJobScheduler.RecurringLockName{TJob}"/> з тим самим
/// payload <c>null</c>, з яким розклад ставить старт); що це ім'я і справді
/// адаптерове — стереже <c>RecurringJobSurvivesRunTests</c> в
/// <c>Ecr.Infrastructure.Tests</c>. Без Quartz: у спільному прогоні Api його
/// статичний <c>LogProvider</c> прив'язаний до закритого хоста.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class PeriodStateStartupLockTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Погодинний_прогін_тримає_лок_стартове_вирівнювання_пропускається()
    {
        // "Інший інстанс" посеред погодинного тику PeriodStateJob.
        await using var hourlyTick = await SqlDistributedLock.TryAcquireAsync(
            sql.ConnectionString,
            QuartzJobScheduler.RecurringLockName<PeriodStateJob>(null),
            CancellationToken.None);
        Assert.NotNull(hourlyTick);

        var services = new ServiceCollection();
        services.AddDbContext<EcrDbContext>(o => o.UseSqlServer(sql.ConnectionString));
        await using var provider = services.BuildServiceProvider();
        var logger = new RecordingLogger<RecurringScheduleService>();

        await RecurringScheduleService.RunPeriodStateOnceAsync(provider, logger);

        // ⛔ Головне: вирівнювання НЕ почалося. З окремою назвою локу старт узяв
        // би свій, пішов би виконувати задачу — і тут (задача не зареєстрована)
        // упав би з Warning «не вдалося», а не пропустив би.
        Assert.Contains(
            logger.OfLevel(LogLevel.Information),
            r => r.Message.Contains("пропущено", StringComparison.Ordinal));
        Assert.Empty(logger.OfLevel(LogLevel.Warning));
    }
}
