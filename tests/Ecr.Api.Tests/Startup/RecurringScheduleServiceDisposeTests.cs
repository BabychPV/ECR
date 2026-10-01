// tests/Ecr.Api.Tests/Startup/RecurringScheduleServiceDisposeTests.cs
using Ecr.Api.Startup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Завершення хоста: <c>StopAsync</c> і <c>Dispose</c> ідемпотентні й не кидають
/// <see cref="ObjectDisposedException"/>, у будь-якому порядку й паралельно.
/// </summary>
/// <remarks>
/// Дефект: диспоз фабрики (контейнер звільняє сервіс) міг випередити
/// <c>StopAsync</c>, і <c>sweepStop.CancelAsync()</c> кидав. Мутаційний доказ:
/// повернути <c>await sweepStop.CancelAsync()</c> без перехоплення в
/// <c>StopAsync</c> → тести «після Dispose» червоні.
/// </remarks>
public sealed class RecurringScheduleServiceDisposeTests
{
    private static RecurringScheduleService Create(out CancellationTokenSource started)
    {
        started = new CancellationTokenSource();
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStarted.Returns(started.Token);

        return new RecurringScheduleService(
            new ServiceCollection().BuildServiceProvider(),
            lifetime,
            NullLogger<RecurringScheduleService>.Instance,
            new ConfigurationBuilder().Build());
    }

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task StopAsync_двічі_підряд_не_кидає_і_наступний_Dispose_теж()
    {
        var service = Create(out var started);
        using var _ = started;
        await service.StartAsync(CancellationToken.None);

        var first = await Record.ExceptionAsync(() => service.StopAsync(CancellationToken.None).WaitAsync(Limit));
        var second = await Record.ExceptionAsync(() => service.StopAsync(CancellationToken.None).WaitAsync(Limit));
        var dispose = Record.Exception(() => { service.Dispose(); service.Dispose(); });

        Assert.Null(first);
        Assert.Null(second);
        Assert.Null(dispose);
    }

    [Fact]
    public async Task StopAsync_після_Dispose_не_кидає_і_Dispose_двічі_не_кидає()
    {
        var service = Create(out var started);
        using var _ = started;
        await service.StartAsync(CancellationToken.None);

        var dispose = Record.Exception(() => { service.Dispose(); service.Dispose(); });
        var stop = await Record.ExceptionAsync(() => service.StopAsync(CancellationToken.None).WaitAsync(Limit));

        Assert.Null(dispose);
        Assert.Null(stop);
        Assert.IsNotType<ObjectDisposedException>(stop);
    }

    [Fact]
    public async Task ApplicationStarted_після_Dispose_не_кидає_і_цикл_не_запускається()
    {
        var service = Create(out var started);
        using var _ = started;
        await service.StartAsync(CancellationToken.None);

        service.Dispose();
        var cancel = await Record.ExceptionAsync(() => started.CancelAsync());
        var stop = await Record.ExceptionAsync(() => service.StopAsync(CancellationToken.None).WaitAsync(Limit));

        Assert.Null(cancel);
        Assert.Null(stop);
    }

    [Fact]
    public async Task Паралельні_StopAsync_і_Dispose_не_кидають_і_завершуються()
    {
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var timeouts = 0;

        for (var i = 0; i < 200; i++)
        {
            var service = Create(out var started);
            await service.StartAsync(CancellationToken.None);

            var tasks = new[]
            {
                Task.Run(() => service.StopAsync(CancellationToken.None)),
                Task.Run(() => service.Dispose()),
                Task.Run(() => service.StopAsync(CancellationToken.None)),
                Task.Run(() => service.Dispose()),
                Task.Run(() => started.Cancel()),
            };

            try
            {
                await Task.WhenAll(tasks).WaitAsync(Limit);
            }
            catch (TimeoutException)
            {
                timeouts++;
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }

            foreach (var task in tasks.Where(t => t.IsFaulted))
            {
                exceptions.Add(task.Exception!);
            }

            started.Dispose();
        }

        Assert.Empty(exceptions);
        Assert.Equal(0, timeouts);
    }
}