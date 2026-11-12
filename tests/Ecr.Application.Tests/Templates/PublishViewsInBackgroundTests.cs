// tests/Ecr.Application.Tests/Templates/PublishViewsInBackgroundTests.cs
using System.Diagnostics;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Reporting;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// ✎ P-4b (D-479): публікація версії шаблону НЕ будує вʼюх SSRS у своєму HTTP-запиті — ставить задачу злиття на шаблон
/// (<see cref="IReportViewsJob"/>), а вʼюхи будує <see cref="ReportViewsJob"/> у фоні. Справжня СУБД і справжній обробник.
/// </summary>
/// <remarks>
/// Мутації: повернути в <c>PublishAsync</c> синхронний <c>GenerateViewsBestEffortAsync</c> за наявної черги — перший тест
/// червоний (генератор викликано, задачі немає); задача бере лише версію з payload, а не всі опубліковані версії
/// шаблону — тест двох версій червоний; прибрати <c>Settled</c> з <c>finally</c> задачі — тест збою червоний (картка
/// лишається «оновлюються»).
/// </remarks>
[Collection("SqlServer")]
public sealed class PublishViewsInBackgroundTests(SqlServerFixture sql, ITestOutputHelper output)
{
    private const int Actor = 9;
    private static readonly DateTime Now = new(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];
    // ⚠ Налаштовано один раз (тест конкурентності публікує паралельно — переналаштування підміни з двох потоків ненадійне).
    private readonly IAccessDecisionService _access = Access();
    private readonly ICurrentUser _user = User();

    private static IAccessDecisionService Access()
    {
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Actor }
                .Permission(PublishTemplateVersionHandler.Permission).Permission("Template.View").Build());
        return access;
    }

    private static ICurrentUser User()
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(Actor);
        return user;
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Публікація_не_будує_вʼюх_у_запиті_а_ставить_одну_задачу_злиття_на_шаблон()
    {
        var (templateId, versionId) = await ArrangeDraftAsync($"B{_tag}", "1", tables: 1, columns: 3);
        var generator = Substitute.For<IReportViewGenerator>();
        var jobs = Substitute.For<IBackgroundJobScheduler>();
        var status = new ReportViewStatus();

        await PublishAsync(versionId, generator, status, jobs);

        Assert.Equal((byte)TemplateVersionStatus.Published, await VersionStatusAsync(versionId));
        await generator.DidNotReceiveWithAnyArgs().GenerateAsync(default, default);
        await jobs.Received(1).EnqueueCoalescedAsync<IReportViewsJob>(
            ReportViewsTarget.Of(templateId),
            Arg.Is<object?>(p => p is ReportViewsTask && ((ReportViewsTask)p).TemplateId == templateId),
            Arg.Any<CancellationToken>(),
            Actor);
        Assert.Equal([templateId], status.PendingTemplates());
        Assert.Empty(status.Snapshot());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Задача_будує_вʼюхи_всіх_опублікованих_версій_шаблону_і_знімає_оновлюються()
    {
        // Дві публікації одного шаблону злилися в одну задачу: вона мусить збудувати вʼюхи ОБОХ версій, а чернетку — ні.
        var code = $"J{_tag}";
        var (templateId, first) = await ArrangeDraftAsync(code, "1", tables: 1, columns: 2);
        var (_, second) = await ArrangeDraftAsync(code, "2", tables: 1, columns: 2, templateId: templateId);
        await ArrangeDraftAsync(code, "3", tables: 1, columns: 2, templateId: templateId);
        var status = new ReportViewStatus();
        var jobs = Substitute.For<IBackgroundJobScheduler>();
        await PublishAsync(first, Substitute.For<IReportViewGenerator>(), status, jobs);
        await PublishAsync(second, Substitute.For<IReportViewGenerator>(), status, jobs);
        Assert.Empty(await ViewsLikeAsync(code));

        await RunJobAsync(templateId, status);

        var views = await ViewsLikeAsync(code);
        Assert.Contains(views, v => v.EndsWith("_v1", StringComparison.Ordinal));
        Assert.Contains(views, v => v.EndsWith("_v2", StringComparison.Ordinal));
        Assert.DoesNotContain(views, v => v.EndsWith("_v3", StringComparison.Ordinal));
        Assert.Empty(status.PendingTemplates());
        Assert.Empty(status.Snapshot());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Збій_генерації_у_задачі_видно_на_картці_задача_падає_і_оновлюються_знято()
    {
        // Таблиця понад 250 колонок — процедура відмовляє 50422 (як у PublishViewsBestEffortTests).
        var (templateId, versionId) = await ArrangeDraftAsync($"F{_tag}", "1", tables: 1, columns: 251);
        var status = new ReportViewStatus();
        await PublishAsync(versionId, Substitute.For<IReportViewGenerator>(), status, Substitute.For<IBackgroundJobScheduler>());
        Assert.Equal([templateId], status.PendingTemplates());

        await Assert.ThrowsAsync<SqlException>(() => RunJobAsync(templateId, status));

        // Публікація не відкочена; причина — на картці; «оновлюються» знято; задачу можна повторити (вона ідемпотентна).
        Assert.Equal((byte)TemplateVersionStatus.Published, await VersionStatusAsync(versionId));
        var failure = Assert.Single(status.Snapshot());
        Assert.Equal(50422, failure.Code);
        Assert.Equal(versionId, failure.TemplateVersionId);
        Assert.Empty(status.PendingTemplates());
    }

    /// <summary>
    /// Конкурентність: задача вʼюх, що виконується, не тримає ні блоку версії, ні транзакції публікації — друга публікація
    /// того самого шаблону проходить, поки задача стоїть; дві одночасні публікації двох версій ставлять задачу на ту саму
    /// ціль (злиття — справа черги).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Публікації_не_чекають_на_задачу_вʼюх_і_ставлять_одну_ціль()
    {
        var code = $"C{_tag}";
        var (templateId, first) = await ArrangeDraftAsync(code, "1", tables: 1, columns: 2);
        var (_, second) = await ArrangeDraftAsync(code, "2", tables: 1, columns: 2, templateId: templateId);
        var (_, third) = await ArrangeDraftAsync(code, "3", tables: 1, columns: 2, templateId: templateId);
        var status = new ReportViewStatus();
        var jobs = Substitute.For<IBackgroundJobScheduler>();
        await PublishAsync(first, Substitute.For<IReportViewGenerator>(), status, jobs);

        // Задача вʼюх стоїть усередині генерації (реальна задача, генератор-заглушка тримає її, доки не відпустимо).
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocking = Substitute.For<IReportViewGenerator>();
        blocking.GenerateAsync(Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        });
        Task running;
        await using (var jobDb = Context())
        {
            running = new ReportViewsJob(jobDb, blocking, status).ExecuteAsync(
                new ReportViewsTask(templateId), Substitute.For<IJobProgress>(), CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // Дві публікації того самого шаблону одночасно, поки задача стоїть.
            var publishes = Task.WhenAll(
                PublishAsync(second, Substitute.For<IReportViewGenerator>(), status, jobs),
                PublishAsync(third, Substitute.For<IReportViewGenerator>(), status, jobs));
            await publishes.WaitAsync(TimeSpan.FromSeconds(60));

            Assert.False(running.IsCompleted);
            release.SetResult();
            await running.WaitAsync(TimeSpan.FromSeconds(30));
        }

        Assert.Equal((byte)TemplateVersionStatus.Published, await VersionStatusAsync(second));
        Assert.Equal((byte)TemplateVersionStatus.Published, await VersionStatusAsync(third));
        await jobs.Received(3).EnqueueCoalescedAsync<IReportViewsJob>(
            ReportViewsTarget.Of(templateId), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Actor);
    }

    /// <summary>
    /// Замір (не таймінговий твердження, лише вивід): публікація синтетичного шаблону 40 таблиць × 60 колонок зі СПРАВЖНІМ
    /// генератором — шлях без черги (як до D-479, вʼюхи в запиті) проти шляху з чергою (вʼюхи в задачі).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Замір_часу_обробника_публікації_до_і_після()
    {
        const int tables = 40;
        const int columns = 60;
        var (_, inRequest) = await ArrangeDraftAsync($"Ma{_tag}", "1", tables, columns);
        var (backgroundTemplate, inBackground) = await ArrangeDraftAsync($"Mb{_tag}", "1", tables, columns);
        var status = new ReportViewStatus();

        var watch = Stopwatch.StartNew();
        await PublishWithRealGeneratorAsync(inRequest, status, jobs: null);
        var before = watch.Elapsed;

        watch.Restart();
        await PublishWithRealGeneratorAsync(inBackground, status, Substitute.For<IBackgroundJobScheduler>());
        var after = watch.Elapsed;

        watch.Restart();
        await RunJobAsync(backgroundTemplate, status);
        var job = watch.Elapsed;

        output.WriteLine(
            $"Публікація {tables}×{columns}: вʼюхи в запиті {before.TotalMilliseconds:F0} мс; з чергою {after.TotalMilliseconds:F0} мс; "
            + $"задача вʼюх {job.TotalMilliseconds:F0} мс.");
        Assert.True(after < before, $"з чергою {after.TotalMilliseconds:F0} мс не швидше за {before.TotalMilliseconds:F0} мс");
    }

    // ── підготовка ───────────────────────────────────────────────────────────

    private async Task RunJobAsync(int templateId, IReportViewStatus status)
    {
        await using var db = Context();
        await new ReportViewsJob(db, new ReportViewGenerator(db, status), status)
            .ExecuteAsync(new ReportViewsTask(templateId), Substitute.For<IJobProgress>(), CancellationToken.None);
    }

    private async Task PublishWithRealGeneratorAsync(int versionId, IReportViewStatus status, IBackgroundJobScheduler? jobs)
    {
        await using var db = Context();
        await PublishCoreAsync(db, versionId, new ReportViewGenerator(db, status), status, jobs);
    }

    private async Task PublishAsync(
        int versionId, IReportViewGenerator generator, IReportViewStatus status, IBackgroundJobScheduler? jobs)
    {
        await using var db = Context();
        await PublishCoreAsync(db, versionId, generator, status, jobs);
    }

    private async Task PublishCoreAsync(
        EcrDbContext db, int versionId, IReportViewGenerator generator, IReportViewStatus status, IBackgroundJobScheduler? jobs)
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());

        var handler = new PublishTemplateVersionHandler(
            new Repository<TemplateVersion, int>(db),
            new TemplateVersionStore(db),
            new RealFormulaEngine(),
            new CalculationBindingStore(db),
            new MetadataCache(memory, db),
            new UnitCatalog(db),
            _access,
            _user,
            new AuditWriter(db),
            new UnitOfWork(db),
            new TestClock(Now),
            generator,
            logger: null,
            viewStatus: status,
            jobs: jobs);

        await handler.PublishAsync(versionId, Actor, "background views", CancellationToken.None);
    }

    private async Task<(int TemplateId, int VersionId)> ArrangeDraftAsync(
        string template, string versionNumber, int tables, int columns, int? templateId = null)
    {
        await using var db = Context();
        int id;
        if (templateId is { } existing)
        {
            id = existing;
        }
        else
        {
            var t = new Template(EcrCode.Create(template), Text(template), 1, Now);
            db.Templates.Add(t);
            await db.SaveChangesAsync();
            id = t.Id;
        }

        var version = new TemplateVersion(id, versionNumber, 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();

        var s = new SheetDef(version.Id, EcrCode.Create("S"), Text("S"), 1);
        db.SheetDefs.Add(s);
        await db.SaveChangesAsync();

        for (var tableNo = 1; tableNo <= tables; tableNo++)
        {
            var td = new TableDef(
                s.Id, EcrCode.Create($"T{tableNo}"), Text($"T{tableNo}"), tableNo, TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
            db.TableDefs.Add(td);
            await db.SaveChangesAsync();

            for (var i = 1; i <= columns; i++)
            {
                db.ColumnDefs.Add(new ColumnDef(td.Id, EcrCode.Create($"C{i}"), Text($"C{i}"), i, CellDataType.Decimal));
            }
        }

        await db.SaveChangesAsync();
        return (id, version.Id);
    }

    private async Task<byte> VersionStatusAsync(int versionId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Status FROM cfg.TemplateVersion WHERE Id = @id";
        command.Parameters.AddWithValue("@id", versionId);
        return (byte)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Вʼюхи з версією (<c>_vN</c>) шаблону <paramref name="templateCode"/>.</summary>
    private async Task<List<string>> ViewsLikeAsync(string templateCode)
    {
        var result = new List<string>();
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sys.views WHERE schema_id = SCHEMA_ID(N'rpt') AND name LIKE @p";
        command.Parameters.AddWithValue("@p", $"v[_]{templateCode}[_]%[_]v[0-9]%");
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.CommandTimeout(120))
            .Options);
}
