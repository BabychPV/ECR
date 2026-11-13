// tests/Ecr.Application.Tests/Templates/ReportViewsAllScopeTests.cs
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Reporting;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// ✎ P-4b P3 (F13 на великому бекапі): генерація вʼюх по ВСІХ шаблонах на старті йшла синхронно в послідовності старту й
/// упала на стелі команди 300 с, а картка <c>reportviews</c> лишалась Degraded і після пізнішого успіху задачі шаблону.
/// Тепер старт ставить фонову задачу з ціллю <see cref="ReportViewsTarget.All"/> (лише якщо входи змінилися) зі стелею
/// <see cref="ReportViewGenerator.AllTemplatesTimeoutSeconds"/>, а що який успіх знімає — задокументовано в
/// <see cref="IReportViewStatus"/> і перевірено тут.
/// </summary>
/// <remarks>
/// Мутації: (1) <c>Parse</c> знову вимагає шаблон — тест задачі «усі» червоний; (2) задача «усі» без
/// <c>onlyIfChanged</c> — він же червоний; (3) старт знову генерує синхронно за наявної черги — тест старту червоний;
/// (4) прогін по всіх без довгої стелі — тест стелі червоний; (5) <c>StableSucceeded</c>/<c>Succeeded(V)</c> знімають
/// збій прогону по всіх — тест ключів червоний.
/// </remarks>
public sealed class ReportViewsAllScopeTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Задача_усі_шаблони_генерує_все_лише_якщо_входи_змінилися()
    {
        var generator = new RecordingGenerator();

        // Payload з черги в базі — JSON (як після рестарту чи «Restart»); шаблону немає, є «усі».
        await using var db = OfflineContext();
        await new ReportViewsJob(db, generator, new ReportViewStatus())
            .ExecuteAsync("{\"templateId\":0,\"allTemplates\":true}", Substitute.For<IJobProgress>(), CancellationToken.None);

        Assert.Equal(["all:onlyIfChanged=True"], generator.Calls);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Старт_ставить_задачу_усі_шаблони_замість_синхронної_генерації()
    {
        var jobs = Substitute.For<IBackgroundJobScheduler>();
        var generator = Substitute.For<IReportViewGenerator>();

        var queued = await ReportViewsJob.QueueAllOnStartupAsync(jobs, generator, null, CancellationToken.None);

        Assert.True(queued);
        await jobs.Received(1).EnqueueCoalescedAsync<IReportViewsJob>(
            ReportViewsTarget.All,
            Arg.Is<object?>(p => p is ReportViewsTask && ((ReportViewsTask)p).AllTemplates),
            Arg.Any<CancellationToken>(),
            null);
        Assert.Empty(generator.ReceivedCalls());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Старт_без_черги_генерує_синхронно_а_збій_постановки_видно_на_картці()
    {
        // Без планувальника (так не буває в застосунку, лише в усічених хостах) — як раніше, синхронно, лише змінене.
        var generator = Substitute.For<IReportViewGenerator>();
        Assert.False(await ReportViewsJob.QueueAllOnStartupAsync(null, generator, null, CancellationToken.None));
        await generator.Received(1).GenerateAllAsync(true, Arg.Any<CancellationToken>());

        // Постановка впала — не синхронна генерація на пів години в старті, а збій прогону по всіх на картці.
        var jobs = Substitute.For<IBackgroundJobScheduler>();
        jobs.EnqueueCoalescedAsync<IReportViewsJob>(
                Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .ThrowsAsync(new InvalidOperationException("queue down"));
        var status = new ReportViewStatus();
        var other = Substitute.For<IReportViewGenerator>();
        Assert.False(await ReportViewsJob.QueueAllOnStartupAsync(jobs, other, status, CancellationToken.None));
        Assert.Empty(other.ReceivedCalls());
        var failure = Assert.Single(status.Snapshot());
        Assert.Null(failure.TemplateVersionId);

        // ✎ F13 #872 delta P2 (SEC): сирий текст винятку на картку (анонімний /health/ready) не йде — лише заголовок і
        // кореляція (ReportViewSitesSafeTextTests).
        Assert.Contains("not queued at startup", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("queue down", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [InlineData(null, false, 120, 1800)]
    [InlineData(null, true, 120, 1800)]
    [InlineData(null, false, null, 1800)]
    [InlineData(null, false, 3600, 3600)]
    [InlineData(7, true, 120, 300)]
    [InlineData(7, false, 120, 120)]
    [InlineData(7, false, null, null)]
    public void Стеля_команди_усі_шаблони_довга_одна_версія_як_була(int? version, bool routing, int? current, int? expected)
        => Assert.Equal(expected, ReportViewGenerator.CommandTimeoutFor(version, routing, current));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Який_успіх_знімає_який_збій()
    {
        var status = new ReportViewStatus();
        var all = new ReportViewFailure(null, -2, "timeout");
        var v7 = new ReportViewFailure(7, 50422, "v7");
        var stable3 = new ReportViewFailure(7, 50409, "stable T3", StableTemplateId: 3);
        var stable4 = new ReportViewFailure(9, 50409, "stable T4", StableTemplateId: 4);
        status.Failed(all);
        status.Failed(v7);
        status.Failed(stable3);
        status.Failed(stable4);

        // Стабільні T3 і збій _vN тієї ж опорної версії — різні ключі: жоден не перезаписав другого.
        Assert.Equal(4, status.Snapshot().Count);

        // Успіх _vN версії 7 знімає лише її збій — не стабільні T3 (опорна версія та сама) і не прогін по всіх.
        status.Succeeded(7);
        Assert.Equal([all, stable3, stable4], Ordered(status));

        // Успіх стабільних T3 знімає лише стабільні T3.
        status.StableSucceeded(3);
        Assert.Equal([all, stable4], Ordered(status));

        // Успіх одного шаблону не доводить, що вʼюхи решти є: збій прогону по всіх лишається…
        status.StableSucceeded(4);
        Assert.Equal([all], Ordered(status));

        // …і знімає його лише успішний прогін по всіх (задача ReportViewsTarget.All).
        status.Succeeded(null);
        Assert.Empty(status.Snapshot());
    }

    private static List<ReportViewFailure> Ordered(ReportViewStatus status)
        => [.. status.Snapshot().OrderBy(f => f.StableTemplateId ?? 0).ThenBy(f => f.TemplateVersionId ?? 0)];

    /// <summary>Контекст без з'єднання: задача «усі» до бази не ходить (лише генератор).</summary>
    private static EcrDbContext OfflineContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer("Server=127.0.0.1,1;Database=None;Connect Timeout=1;TrustServerCertificate=True")
            .Options);

    private sealed class RecordingGenerator : IReportViewGenerator
    {
        public List<string> Calls { get; } = [];

        public Task GenerateAsync(int? templateVersionId, CancellationToken ct)
        {
            Calls.Add($"full:{templateVersionId}");
            return Task.CompletedTask;
        }

        public Task GenerateAllAsync(bool onlyIfChanged, CancellationToken ct)
        {
            Calls.Add($"all:onlyIfChanged={onlyIfChanged}");
            return Task.CompletedTask;
        }
    }
}
