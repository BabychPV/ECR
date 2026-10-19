// tests/Ecr.Infrastructure.Tests/Persistence/PermitRuntimeRowsTests.Recalculation.cs
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// PM-10b: рядки й ліміти з дозволу, створені під час відкриття документа, ставлять перерахунок залежних формул -
/// рівно одну coalesced-задачу на відкриття; без створеного нічого не ставиться.
/// </summary>
/// <remarks>
/// Мутаційний доказ (прогнано): у <c>RowStore.EnsureTableInstancesAsync</c> в <c>PermitMaterializationRecalculation.EnqueueAsync</c>
/// передано <c>null</c> замість результату - червоніють «Відкриття_з_прив_язкою…», «Повторне_відкриття…» (його перша
/// частина чекає задачу) і «Збій_постановки…»; «Дозволу_немає…» та «Без_прив_язки…» лишаються зеленими (вони доводять
/// відсутність задачі). Мутацію відкочено.
/// </remarks>
public sealed partial class PermitRuntimeRowsTests
{
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Відкриття_з_прив_язкою_ставить_рівно_одну_coalesced_задачу_перерахунку_з_насінням_створених_комірок()
    {
        var scene = await ArrangeAsync();
        await using var _ = scene.H;
        var jobs = Substitute.For<IBackgroundJobScheduler>();

        await using var db = scene.NewContext();
        Assert.Equal(1, await NewStore(db, permits: true, jobs).EnsureTableInstancesAsync(scene.DocumentId, January, default));

        var call = Assert.Single(jobs.ReceivedCalls());
        Assert.Equal(nameof(IBackgroundJobScheduler.EnqueueCoalescedAsync), call.GetMethodInfo().Name);
        Assert.Equal(typeof(IFormulaRecalculationJob), call.GetMethodInfo().GetGenericArguments().Single());

        var args = call.GetArguments();
        Assert.Equal(FormulaRecalculationTarget.Of(scene.DocumentId, January.Value, createdByUserId: null), args[0]);
        Assert.Null(args[3]);

        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(args[1], PayloadOptions));
        var root = payload.RootElement;
        Assert.Equal(scene.DocumentId, root.GetProperty("documentId").GetInt64());
        Assert.Equal(await scene.InstanceIdAsync(), root.GetProperty("tableInstanceId").GetInt64());
        Assert.Equal(January.Value, root.GetProperty("periodKey").GetInt32());

        // Насіння - рівно комірки, записані з дозволу (ключ + ліміт у кожного з 3 рядків, примітка у двох): 8, як в аудиті.
        var seeds = root.GetProperty("cells").EnumerateArray()
            .Select(c => (c.GetProperty("rowId").GetInt64(), c.GetProperty("columnDefId").GetInt32()))
            .ToHashSet();
        Assert.Equal(await scene.PermitAuditCountAsync(), seeds.Count);
        Assert.Equal((await scene.RowsAsync()).Select(r => r.Id).Order(), seeds.Select(s => s.Item1).Distinct().Order());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Повторне_відкриття_без_нових_екземплярів_нічого_не_ставить()
    {
        var scene = await ArrangeAsync();
        await using var _ = scene.H;
        var jobs = Substitute.For<IBackgroundJobScheduler>();

        await using (var first = scene.NewContext())
        {
            await NewStore(first, permits: true, jobs).EnsureTableInstancesAsync(scene.DocumentId, January, default);
        }

        Assert.Single(jobs.ReceivedCalls());
        jobs.ClearReceivedCalls();

        await using var again = scene.NewContext();
        Assert.Equal(0, await NewStore(again, permits: true, jobs).EnsureTableInstancesAsync(scene.DocumentId, January, default));

        Assert.Empty(jobs.ReceivedCalls());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Дозволу_немає_екземпляр_створено_але_нічого_не_записано_отже_задачі_немає()
    {
        var scene = await ArrangeAsync(facility: "Nowhere");
        await using var _ = scene.H;
        var jobs = Substitute.For<IBackgroundJobScheduler>();

        await using var db = scene.NewContext();
        Assert.Equal(1, await NewStore(db, permits: true, jobs).EnsureTableInstancesAsync(scene.DocumentId, January, default));

        Assert.Empty(jobs.ReceivedCalls());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_прив_язки_створення_екземплярів_не_ставить_задачу()
    {
        // Land-подібна таблиця: фіксовані рядки шаблону, прив'язки немає.
        var scene = await BuildLandLikeAsync(withInstances: false);
        var jobs = Substitute.For<IBackgroundJobScheduler>();

        await using var db = scene.Builder.CreateContext();
        Assert.Equal(1, await NewStore(db, permits: true, jobs).EnsureTableInstancesAsync(scene.Chain.DocumentId, scene.Chain.PeriodKey, default));

        Assert.Empty(jobs.ReceivedCalls());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Збій_постановки_перерахунку_не_ламає_відкриття_рядки_й_ліміти_збережено()
    {
        var scene = await ArrangeAsync();
        await using var _ = scene.H;
        var jobs = Substitute.For<IBackgroundJobScheduler>();
        jobs.EnqueueCoalescedAsync<IFormulaRecalculationJob>(
                Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .ThrowsAsync(new InvalidOperationException("черга недоступна"));

        await using var db = scene.NewContext();
        var created = await NewStore(db, permits: true, jobs).EnsureTableInstancesAsync(scene.DocumentId, January, default);

        Assert.Equal(1, created);
        Assert.Equal(3, (await scene.RowsAsync()).Count);
        Assert.Equal(8, await scene.PermitAuditCountAsync());
        Assert.Single(jobs.ReceivedCalls());
    }
}
