// tests/Ecr.Application.Tests/Templates/ReportViewsJobScopeTests.cs
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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// ✎ P-4b P2 (F13 на великому бекапі): задача вʼюх після публікації будувала <c>_vN</c> УСІХ Published+Deprecated версій
/// шаблону і стабільні вʼюхи шаблону — на кожну версію (25 версій — 42 хв, ~4 хв на версію). Тепер: <c>_vN</c> лише версій,
/// що чекають (набір переживає злиття), і стабільні — один раз на прогін; після рестарту — лише поточна опублікована.
/// Справжня СУБД (версії, статуси) і справжній обробник публікації; генератор — записувач викликів.
/// </summary>
/// <remarks>
/// Мутації: (1) задача знову бере всі Published/Deprecated версії — обидва тести червоні (зайві <c>_vN</c> старих версій);
/// (2) стабільні вʼюхи в циклі по версіях — «стабільні один раз» червоне; (3) задача бере лише версію з payload, без набору —
/// перший тест червоний (версія, чий payload злиття замінило, не отримує вʼюх).
/// </remarks>
[Collection("SqlServer")]
public sealed class ReportViewsJobScopeTests(SqlServerFixture sql)
{
    private const int Actor = 9;
    private static readonly DateTime Now = new(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];
    private readonly IAccessDecisionService _access = Access();
    private readonly ICurrentUser _user = User();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Задача_після_злитих_публікацій_будує_vN_лише_опублікованих_версій_і_стабільні_один_раз()
    {
        // v1 опублікована давно (її вʼюхи вже є); потім дві публікації (v2, v3) злилися в одну задачу з payload останньої.
        var code = $"P{_tag}";
        var (templateId, v1) = await ArrangeDraftAsync(code, "1");
        var (_, v2) = await ArrangeDraftAsync(code, "2", templateId);
        var (_, v3) = await ArrangeDraftAsync(code, "3", templateId);
        await PublishAsync(v1, new ReportViewStatus(), Substitute.For<IBackgroundJobScheduler>());

        var status = new ReportViewStatus();
        var jobs = Substitute.For<IBackgroundJobScheduler>();
        await PublishAsync(v2, status, jobs);
        await PublishAsync(v3, status, jobs);
        // Злиття: у черзі лишився payload ОСТАННЬОЇ постановки (v3).
        var payload = jobs.ReceivedCalls()
            .Last(c => c.GetMethodInfo().Name == nameof(IBackgroundJobScheduler.EnqueueCoalescedAsync))
            .GetArguments()[1];

        var generator = new RecordingGenerator();
        await RunJobAsync(payload, generator, status);

        Assert.Equal([v2, v3], generator.VersionViews());
        Assert.DoesNotContain(v1, generator.VersionViews());
        Assert.Equal(1, generator.StableRuns());
        Assert.Empty(status.PendingTemplates());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Задача_без_набору_версій_після_рестарту_будує_лише_поточну_опубліковану()
    {
        var code = $"R{_tag}";
        var (templateId, v1) = await ArrangeDraftAsync(code, "1");
        var (_, v2) = await ArrangeDraftAsync(code, "2", templateId);
        await ArrangeDraftAsync(code, "3", templateId);
        await PublishAsync(v1, new ReportViewStatus(), Substitute.For<IBackgroundJobScheduler>());
        await PublishAsync(v2, new ReportViewStatus(), Substitute.For<IBackgroundJobScheduler>());

        // Рестарт: набір у памʼяті порожній; задача з черги в базі (або «Restart») має лише шаблон.
        var generator = new RecordingGenerator();
        await RunJobAsync(new ReportViewsTask(templateId), generator, new ReportViewStatus());

        Assert.Equal([v2], generator.VersionViews());
        Assert.Equal(1, generator.StableRuns());
    }

    // ── підготовка ───────────────────────────────────────────────────────────

    /// <summary>Записувач викликів генератора: повна генерація версії рахується і як <c>_vN</c>, і як стабільні.</summary>
    private sealed class RecordingGenerator : IReportViewGenerator
    {
        private readonly List<string> _calls = [];

        public Task GenerateAsync(int? templateVersionId, CancellationToken ct) => Record($"full:{templateVersionId}");

        public Task GenerateVersionViewsAsync(int templateVersionId, CancellationToken ct) => Record($"vN:{templateVersionId}");

        public Task GenerateStableViewsAsync(int templateId, int anchorVersionId, CancellationToken ct) => Record($"stable:{templateId}");

        public List<int> VersionViews()
        {
            lock (_calls)
            {
                return [.. _calls
                    .Where(c => c.StartsWith("full:", StringComparison.Ordinal) || c.StartsWith("vN:", StringComparison.Ordinal))
                    .Select(c => int.Parse(c[(c.IndexOf(':', StringComparison.Ordinal) + 1)..], System.Globalization.CultureInfo.InvariantCulture))
                    .Order()];
            }
        }

        public int StableRuns()
        {
            lock (_calls)
            {
                return _calls.Count(c => c.StartsWith("full:", StringComparison.Ordinal) || c.StartsWith("stable:", StringComparison.Ordinal));
            }
        }

        private Task Record(string call)
        {
            lock (_calls)
            {
                _calls.Add(call);
            }

            return Task.CompletedTask;
        }
    }

    private async Task RunJobAsync(object? payload, IReportViewGenerator generator, IReportViewStatus status)
    {
        await using var db = Context();
        await new ReportViewsJob(db, generator, status)
            .ExecuteAsync(payload, Substitute.For<IJobProgress>(), CancellationToken.None);
    }

    private async Task PublishAsync(int versionId, IReportViewStatus status, IBackgroundJobScheduler jobs)
    {
        await using var db = Context();
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
            Substitute.For<IReportViewGenerator>(),
            logger: null,
            viewStatus: status,
            jobs: jobs);

        await handler.PublishAsync(versionId, Actor, "job scope", CancellationToken.None);
    }

    private async Task<(int TemplateId, int VersionId)> ArrangeDraftAsync(string template, string versionNumber, int? templateId = null)
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

        var td = new TableDef(s.Id, EcrCode.Create("T1"), Text("T1"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        db.TableDefs.Add(td);
        await db.SaveChangesAsync();

        db.ColumnDefs.Add(new ColumnDef(td.Id, EcrCode.Create("C1"), Text("C1"), 1, CellDataType.Decimal));
        await db.SaveChangesAsync();
        return (id, version.Id);
    }

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

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.CommandTimeout(120))
            .Options);
}
