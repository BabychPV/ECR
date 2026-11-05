// tests/Ecr.Infrastructure.Tests/Persistence/StaleCountsEpochMonotonicTests.cs
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// AN-155b, знахідка Аудиту-2 (F13 #733): жорстка епоха періоду (<see cref="StaleCountsEpoch.HardValueFor"/>) не спадає ні на
/// мить, поки інший потік перемикає прогони й піднімає епоху. Раніше вона була різницею двох лічильників, що росли
/// окремими інкрементами, і читач у щілині між ними бачив значення на 1 менше - брав старіший запис карти за чинний.
/// </summary>
/// <remarks>
/// Мутаційний доказ (виконаний локально): повернути <c>HardValueFor</c> як «складова періоду мінус перемикання» з двома
/// інкрементами в <c>BumpRunSwitch</c> - читач ловить спад, тест червоніє. Без бази: контекст лише дає
/// <c>CurrentTransaction == null</c> (підйом негайний), з'єднання не відкривається.
/// </remarks>
public sealed class StaleCountsEpochMonotonicTests
{
    private const int Period = 202607;

    [Fact]
    public async Task Жорстка_епоха_не_спадає_під_паралельними_перемиканнями_прогону()
    {
        var epoch = new StaleCountsEpoch();
        await using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer("Server=localhost;Database=EcrNoConnect;Integrated Security=true;TrustServerCertificate=True").Options);

        using var stop = new CancellationTokenSource();
        using var readerRunning = new ManualResetEventSlim();
        var dips = 0L;
        var reads = 0L;
        var reader = Task.Run(() =>
        {
            var last = epoch.HardValueFor(Period);
            do
            {
                var now = epoch.HardValueFor(Period);
                if (now < last)
                {
                    Interlocked.Increment(ref dips);
                }

                last = now;
                reads++;
                readerRunning.Set();
            }
            while (!stop.IsCancellationRequested);
        });

        // Без часового вікна: письменник стартує, лише коли читач уже в циклі (під навантаженням CI потік читача міг не
        // встигнути стартувати до кінця 400 тис. операцій - «читач не встиг прочитати», CI 42018285775).
        Assert.True(readerRunning.Wait(TimeSpan.FromSeconds(30)), "читач не стартував");
        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 400_000; i++)
            {
                epoch.InvalidateRunSwitch(db, Period);
                if (i % 50 == 0)
                {
                    epoch.Bump(Period);
                }
            }
        });

        await writer;
        await stop.CancelAsync();
        await reader;

        Assert.True(reads > 0, "читач не встиг прочитати");
        Assert.Equal(0L, Interlocked.Read(ref dips));

        // Перемикання прогону не рухає жорстку епоху, а підйом періоду - рухає; ValueFor росте від обох.
        var hard = epoch.HardValueFor(Period);
        var full = epoch.ValueFor(Period);
        epoch.InvalidateRunSwitch(db, Period);
        Assert.Equal(hard, epoch.HardValueFor(Period));
        Assert.Equal(full + 1, epoch.ValueFor(Period));
        epoch.Bump(Period);
        Assert.Equal(hard + 1, epoch.HardValueFor(Period));
    }
}
