// tests/Ecr.Worker.Tests/ChildShutdownFlushTests.cs

using Ecr.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Ecr.Worker.Tests;

/// <summary>
/// Y6-02: штатна зупинка дочірнього воркера не падає на скиданні метрик.
/// </summary>
/// <remarks>
/// <para>
/// До виправлення цикл дочірнього був <c>try { host.RunAsync } finally { ChildTelemetry.Flush(host.Services) }</c>.
/// <c>RunAsync</c> у власному <c>finally</c> звільняє хост, і <c>GetService</c> на звільненому провайдері
/// кидає <see cref="ObjectDisposedException"/> — кожен вихід справжнього <c>--child</c> за сигналом
/// наглядача закінчувався необробленим винятком (код <c>0xE0434352</c>).
/// </para>
/// <para>
/// ⚠ Мутація: у <c>WorkerProgram.RunBuiltChildAsync</c> замінити <c>StartAsync</c> +
/// <c>WaitForShutdownAsync</c> на <c>host.RunAsync</c> — тест червоніє з <see cref="ObjectDisposedException"/>.
/// </para>
/// <para>
/// Без SQL і без процесу: тест модульний і біжить у <c>worker (windows)</c> і на Linux
/// (на відміну від <c>WorkerSupervisorStopTests</c>, Y6-03).
/// </para>
/// </remarks>
public sealed class ChildShutdownFlushTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Штатна_зупинка_скидає_метрики_до_звільнення_хоста_без_винятку()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddHostedService<StopAtStart>();
        using var host = builder.Build();

        var thrown = await Record.ExceptionAsync(() => WorkerProgram.RunBuiltChildAsync(host, CancellationToken.None));

        Assert.Null(thrown);

        // Звільнення — справа власника (`using` у викликача): після циклу провайдер ще живий,
        // тобто й Flush бачив живий провайдер.
        Assert.NotNull(host.Services.GetService<IHostApplicationLifetime>());
    }

    /// <summary>Те, що робить <c>ChildStopListener</c> за сигналом наглядача: <c>StopApplication</c>.</summary>
    private sealed class StopAtStart(IHostApplicationLifetime lifetime) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            lifetime.StopApplication();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
