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
        await using var hourlyTick = await AcquireAsync(QuartzJobScheduler.RecurringLockName<PeriodStateJob>(null));

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

    /// <summary>Скільки чекати, поки лок відпустить інший учасник того самого прогону.</summary>
    private static readonly TimeSpan AcquireWait = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Бере лок, ДОЧЕКАВШИСЬ, поки його відпустить сусід по прогону.
    /// </summary>
    /// <remarks>
    /// ⛔ Негайна спроба (як у самого застосунку) давала плаваючий провал
    /// «<c>Assert.NotNull() Failure: Value is null</c>» у повному прогоні Api:
    /// база <c>EcrTest_Api_*</c> спільна для всього прогону, і той самий лок
    /// законно тримали стартове вирівнювання іншої <c>EcrApiFactory</c> (кожен
    /// хост кличе <see cref="RecurringScheduleService.RunPeriodStateOnceAsync"/>)
    /// або погодинний тик <see cref="PeriodStateJob"/> у ще живому хості.
    /// Окремо тест був зелений — бо сусідів не було.
    /// <para>
    /// ⚠ Суть тесту не послаблено: ім'я локу те саме, і перевірка йде лише
    /// тоді, коли лок у руках тесту. Чекання — лише ПІДГОТОВКА: без нього тест
    /// перевіряв не «старт пропускає зайнятий лок», а «ніхто інший саме зараз
    /// не стартує». Тримачі — короткі (вирівнювання, тик), тож хвилина — з
    /// великим запасом; довше — провал із причиною, а не мовчазне очікування.
    /// </para>
    /// </remarks>
    private async Task<SqlDistributedLock> AcquireAsync(string resource)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();

        while (true)
        {
            var held = await SqlDistributedLock.TryAcquireAsync(sql.ConnectionString, resource, CancellationToken.None);
            if (held is not null)
            {
                return held;
            }

            Assert.True(
                waited.Elapsed < AcquireWait,
                $"Лок «{resource}» тримає інший учасник прогону довше за {AcquireWait.TotalSeconds:0} с.");

            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }
    }
}
