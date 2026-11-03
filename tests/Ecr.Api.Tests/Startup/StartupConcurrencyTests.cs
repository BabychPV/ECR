// tests/Ecr.Api.Tests/Startup/StartupConcurrencyTests.cs
using Ecr.Api.Startup;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// AN-156: незалежні кроки старту (bootstrap-адмін, прибирання покинутих задач, прогрів кешу)
/// виконуються одночасно, а не один за одним.
/// </summary>
/// <remarks>
/// Мутаційний доказ: у <c>StartupConcurrency.RunAllAsync</c> замінити <c>Task.WhenAll(...)</c> на
/// послідовний <c>foreach (var s in steps) await s();</c> — перший тест червоніє за таймаутом
/// (кожен крок чекає на сусіда, якого послідовний цикл ще не запустив).
/// </remarks>
public sealed class StartupConcurrencyTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Кроки_стартують_одночасно_а_не_по_черзі()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Кожен крок завершується лише після того, як стартував сусід: послідовний запуск
        // не завершиться ніколи (до таймауту).
        async Task First()
        {
            firstStarted.SetResult();
            await secondStarted.Task.WaitAsync(Limit);
        }

        async Task Second()
        {
            secondStarted.SetResult();
            await firstStarted.Task.WaitAsync(Limit);
        }

        await StartupConcurrency.RunAllAsync(First, Second).WaitAsync(Limit);

        Assert.True(firstStarted.Task.IsCompletedSuccessfully);
        Assert.True(secondStarted.Task.IsCompletedSuccessfully);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Помилка_кроку_летить_далі_лише_після_завершення_решти()
    {
        var slowFinished = false;

        async Task Slow()
        {
            await Task.Delay(200);
            slowFinished = true;
        }

        static Task Broken() => Task.FromException(new InvalidOperationException("крок упав"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartupConcurrency.RunAllAsync(Broken, Slow));

        Assert.Equal("крок упав", ex.Message);
        Assert.True(slowFinished, "Повільний крок мав дійти до кінця, а не лишитися «в польоті» позаду винятку.");
    }
}
