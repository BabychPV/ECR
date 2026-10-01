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

    [Fact]
    public async Task StopAsync_двічі_підряд_не_кидає()
    {
        using var service = Create(out var started);
        using var _ = started;
        await service.StartAsync(CancellationToken.None);

        await service.StopAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_після_Dispose_не_кидає_і_Dispose_двічі_не_кидає()
    {
        var service = Create(out var started);
        using var _ = started;
        await service.StartAsync(CancellationToken.None);

        service.Dispose();
        service.Dispose();

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ApplicationStarted_після_Dispose_не_кидає()
    {
        var service = Create(out var started);
        using var _ = started;
        await service.StartAsync(CancellationToken.None);

        service.Dispose();
        await started.CancelAsync();

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Паралельні_StopAsync_і_Dispose_не_кидають()
    {
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
                await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
                throw new Xunit.Sdk.XunitException(
                    "iter " + i + ": " + string.Join(",", tasks.Select(t => t.Status.ToString())));
            }
            started.Dispose();
        }
    }
}
