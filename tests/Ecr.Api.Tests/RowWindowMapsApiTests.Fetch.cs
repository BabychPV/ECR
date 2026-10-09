// tests/Ecr.Api.Tests/RowWindowMapsApiTests.Fetch.cs
using System.Net;
using System.Net.Http.Json;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Аудит I1-02: заведена, змінена чи відновлена прив'язка ставить підтягування екземплярам таблиці у відкритих
/// періодах — інакше рядки, що вже існують, не підтягувалися ніколи (тригер реагує лише на правку Початку/Кінця/
/// селектора, щогодинний повтор бере тільки рядки з наявним провенансом).
/// </summary>
/// <remarks>
/// Мутаційний доказ: прибрати виклик <c>RowWindowMapSupport.EnqueueFetchAsync</c> у
/// <c>CreateRowWindowMapHandler</c> чи <c>UpdateRowWindowMapHandler</c> — червоніє
/// <see cref="Створення_зміна_й_відновлення_прив_язки_ставлять_підтягування_відкритого_періоду_а_пауза_ні"/>.
/// </remarks>
public sealed partial class RowWindowMapsApiTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "I1-02")]
    public async Task Створення_зміна_й_відновлення_прив_язки_ставлять_підтягування_відкритого_періоду_а_пауза_ні()
    {
        await using var stand = await ArrangeAsync();
        await ExecuteAsync(
            $"UPDATE doc.Period SET State = {(int)PeriodState.Open} WHERE ProjectId = {stand.ProjectId} AND PeriodKey = 202601");

        var jobs = Substitute.For<IBackgroundJobScheduler>();
        using var app = new EcrApiFactory(sql);
        using var host = app.WithWebHostBuilder(b => b.ConfigureTestServices(services => services.AddSingleton(jobs)));
        using var manager = await SignedInAsync(app, ["Integration.Manage"], stand.ProjectId, GrantLevel.Manage, host);

        var id = await CreateAsync(manager, stand, stand.TargetA);
        await ExpectFetchAsync(jobs, stand, times: 1);

        var one = new Uri($"/api/v1/row-window-maps/{id}", UriKind.Relative);

        // Зміна джерел (інший атрибут) — рядки перечитуються за новою конфігурацією.
        jobs.ClearReceivedCalls();
        Assert.Equal(
            HttpStatusCode.OK,
            (await manager.PutAsJsonAsync(one, Replace(stand, sources: [Source("A", stand.EntityId, "Flare.Other", stand.UnitSource)]))).StatusCode);
        await ExpectFetchAsync(jobs, stand, times: 1);

        // Пауза нічого не ставить: на паузі прив'язка не підтягує.
        jobs.ClearReceivedCalls();
        Assert.Equal(HttpStatusCode.OK, (await manager.PutAsJsonAsync(one, Replace(stand, isActive: false))).StatusCode);
        await ExpectFetchAsync(jobs, stand, times: 0);

        // Відновлення: правки вікон під час паузи тригера не ставили — підтягування зараз.
        jobs.ClearReceivedCalls();
        Assert.Equal(HttpStatusCode.OK, (await manager.PutAsJsonAsync(one, Replace(stand))).StatusCode);
        await ExpectFetchAsync(jobs, stand, times: 1);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "I1-02")]
    public async Task Прив_язка_на_таблицю_лише_Scheduled_періоду_підтягування_не_ставить()
    {
        await using var stand = await ArrangeAsync();
        await ExecuteAsync(
            $"UPDATE doc.Period SET State = {(int)PeriodState.Scheduled} WHERE ProjectId = {stand.ProjectId} AND PeriodKey = 202601");

        var jobs = Substitute.For<IBackgroundJobScheduler>();
        using var app = new EcrApiFactory(sql);
        using var host = app.WithWebHostBuilder(b => b.ConfigureTestServices(services => services.AddSingleton(jobs)));
        using var manager = await SignedInAsync(app, ["Integration.Manage"], stand.ProjectId, GrantLevel.Manage, host);

        await CreateAsync(manager, stand, stand.TargetA);

        await ExpectFetchAsync(jobs, stand, times: 0);
    }

    private static async Task ExpectFetchAsync(IBackgroundJobScheduler jobs, Stand stand, int times)
        => await jobs.Received(times).EnqueueCoalescedAsync<IRowWindowFetchJob>(
            RowWindowFetchTarget.Of(stand.InstanceId),
            Arg.Is<object?>(p => p is RowWindowFetchRequest
                                 && ((RowWindowFetchRequest)p).TableInstanceId == stand.InstanceId
                                 && ((RowWindowFetchRequest)p).PeriodKey == 202601),
            Arg.Any<CancellationToken>(),
            Arg.Any<int?>());
}
