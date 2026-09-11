// tests/Ecr.Infrastructure.Tests/Jobs/CollectionJobRecurringRegistrationTests.cs
using Ecr.Application.Ports;
using Ecr.Infrastructure.Integration;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Q-235: збір за розкладом (<c>ext.CollectionSchedule</c>) ставиться
/// <c>QuartzJobScheduler.ScheduleAsync&lt;TJob&gt;</c>, а виконує його
/// <see cref="QuartzJobAdapter"/>, що резолвить задачу з контейнера за
/// <c>typeof(TJob).FullName</c> — рядком, який `ScheduleAsync` сам і поклав у
/// <c>JobDataMap</c>. Ці два боки мусять називати ОДИН і той самий тип, інакше
/// резолв мовчки повертає <c>null</c>, і `QuartzJobAdapter.Execute` падає
/// РАНІШЕ, ніж встигає хоч раз записати щось у <c>itg.JobProgress</c>.
/// </summary>
/// <remarks>
/// ⛔ Регресія, яку тест ловить: <c>RecurringScheduleService.ScheduleAsync</c>
/// ставив збір через <c>ScheduleAsync&lt;Infrastructure.Jobs.CollectionJob&gt;</c>
/// — КОНКРЕТНИЙ клас, — тоді як <c>Ecr.Infrastructure.DependencyInjection</c>
/// реєструє цю задачу ЛИШЕ під портом:
/// <c>services.AddScoped&lt;ICollectionJob, Jobs.CollectionJob&gt;()</c>. Той
/// самий порт, і лише він, використовує й ручний запуск «зібрати зараз»
/// (<c>IntegrationHandlers.cs</c>: <c>EnqueueAsync&lt;ICollectionJob&gt;</c>).
/// Наслідок розбіжності — не повільніший збір і не втрачені точки: збору за
/// розкладом не було ЖОДНОГО РАЗУ, ні для одного джерела, і жоден з
/// механізмів, які мали б це показати (ретраї `QuartzJobAdapter`,
/// <c>itg.CollectionRun</c>, зведення <c>NotificationJob</c>), про це не знав
/// — задача падала до того, як будь-який із них встигав хоч щось побачити.
///
/// ⚠ Реєстрація тут — ТОЧНО той самий рядок, що й у продовому
/// <c>Ecr.Infrastructure.DependencyInjection</c>, а не спрощена заглушка:
/// тест має ловити саме розбіжність між тим, ПІД ЧИМ зареєстрована задача, і
/// тим, ЯКИЙ тип нею планує <c>RecurringScheduleService</c> — а не власну
/// вигадану відповідність.
/// </remarks>
[Collection("SqlServer")]
public sealed class CollectionJobRecurringRegistrationTests(SqlServerFixture sql)
{
    private ServiceProvider BuildProviderWithProductionCollectionJobRegistration()
    {
        var services = new ServiceCollection();

        services.AddDbContext<EcrDbContext>(o => o.UseSqlServer(sql.ConnectionString));
        services.AddSingleton<Ecr.Domain.Abstractions.IClock>(
            new TestClock(new DateTime(2026, 9, 11, 0, 0, 0, DateTimeKind.Utc)));
        services.AddSingleton(Substitute.For<ICollectionRunner>());
        services.AddSingleton(Substitute.For<IBackgroundJobScheduler>());
        services.AddSingleton(Substitute.For<INotificationOutbox>());
        services.AddSingleton(Substitute.For<INotificationSender>());
        services.AddScoped<OutboxDispatcher>();

        // ⚠ ТОЧНО рядок із `Ecr.Infrastructure.DependencyInjection`
        // (`services.AddScoped<ICollectionJob, Jobs.CollectionJob>();`):
        // задача зареєстрована ЛИШЕ під портом, не сама собою.
        services.AddScoped<ICollectionJob, Ecr.Infrastructure.Jobs.CollectionJob>();

        return services.BuildServiceProvider();
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "Q-235")]
    public void Резолв_за_конкретним_класом_CollectionJob_провалюється()
    {
        using var provider = BuildProviderWithProductionCollectionJobRegistration();
        using var scope = provider.CreateScope();

        // ⛔ Це і є регресія: якщо колись хтось знову поставить розклад через
        // `ScheduleAsync<Infrastructure.Jobs.CollectionJob>` (конкретний клас),
        // `QuartzJobAdapter.Resolve` питатиме контейнер саме цим типом — і
        // отримає `null`, як і тут.
        var resolved = scope.ServiceProvider.GetService(typeof(Ecr.Infrastructure.Jobs.CollectionJob));

        Assert.Null(resolved);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "Q-235")]
    public void Резолв_за_портом_ICollectionJob_дає_робочу_задачу()
    {
        using var provider = BuildProviderWithProductionCollectionJobRegistration();
        using var scope = provider.CreateScope();

        // Те саме, що робить `QuartzJobAdapter.Resolve` для коду задачі, який
        // `RecurringScheduleService.ScheduleAsync<ICollectionJob>` кладе в
        // `JobDataMap` (Q-235): контейнер має віддати робочу задачу.
        var resolved = scope.ServiceProvider.GetService(typeof(ICollectionJob));

        Assert.NotNull(resolved);
        Assert.IsType<Ecr.Infrastructure.Jobs.CollectionJob>(resolved);
        Assert.IsAssignableFrom<IBackgroundJob>(resolved);
    }
}
