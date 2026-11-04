// tests/Ecr.Infrastructure.Tests/Persistence/ProjectVersionTimelineTests.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// RPT-STABLE Part B: ОДНА функція «версія за період» — чиста <see cref="ProjectVersionTimeline.VersionOf"/>, її кешований
/// сервіс і SQL-відповідник <c>doc.fn_VersionOf</c> дають однакову відповідь на всіх межах.
/// </summary>
/// <remarks>
/// Мутаційний доказ: у <c>ProjectVersionTimeline.VersionOf</c> замінити <c>&lt;=</c> на <c>&lt;</c> — червоніють «рівно на межі»
/// (чиста функція) і паритет із SQL.
/// </remarks>
[Collection("SqlServer")]
public sealed class ProjectVersionTimelineTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "RPT-STABLE")]
    public void Чиста_функція_бере_найбільший_початок_не_пізніший_за_період_включно_з_межею()
    {
        TimelineEntry[] line = [new(202601, 10), new(202604, 20), new(202610, 30)];

        Assert.Equal(10, ProjectVersionTimeline.VersionOf(line, 202601, 99));   // рівно на першому початку
        Assert.Equal(10, ProjectVersionTimeline.VersionOf(line, 202603, 99));
        Assert.Equal(20, ProjectVersionTimeline.VersionOf(line, 202604, 99));   // рівно на межі - вже нова версія
        Assert.Equal(20, ProjectVersionTimeline.VersionOf(line, 202609, 99));
        Assert.Equal(30, ProjectVersionTimeline.VersionOf(line, 202610, 99));
        Assert.Equal(30, ProjectVersionTimeline.VersionOf(line, 203001, 99));
        Assert.Equal(10, ProjectVersionTimeline.VersionOf(line, 202512, 99));   // раніше першого рядка - перший рядок
        Assert.Equal(99, ProjectVersionTimeline.VersionOf([], 202604, 99));     // шкали немає - чинна версія
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "RPT-STABLE")]
    public void Ефективний_початок_зсувається_на_січень_лише_для_сімейства_місяців_у_колонках()
    {
        Assert.Equal((202604, false), ProjectVersionTimeline.EffectiveFrom(202604, hasMonthsInColumns: false));
        Assert.Equal((202601, true), ProjectVersionTimeline.EffectiveFrom(202604, hasMonthsInColumns: true));
        Assert.Equal((202601, false), ProjectVersionTimeline.EffectiveFrom(202601, hasMonthsInColumns: true));   // січень - без зсуву
        Assert.Equal((202612, false), ProjectVersionTimeline.EffectiveFrom(202612, hasMonthsInColumns: false));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "RPT-STABLE")]
    public async Task SQL_функція_і_сервіс_дають_ту_саму_версію_на_всіх_межах_і_без_шкали()
    {
        var (v1, v2, v3) = await ThreeVersionsAsync();
        var projectId = await NewProjectAsync(v3, 202601, 202612);   // чинна = v3

        // Шкали немає: усі періоди - чинна версія (поведінка до Part B).
        await AssertParityAsync(projectId, [202601, 202606, 202612], expected: v3);

        // Шкала v1 (з січня), v2 (з квітня), v3 (з жовтня).
        await using (var db = sql.CreateContext())
        {
            db.ProjectTemplateVersions.AddRange(
                new ProjectTemplateVersion(projectId, 202601, v1),
                new ProjectTemplateVersion(projectId, 202604, v2),
                new ProjectTemplateVersion(projectId, 202610, v3));
            await db.SaveChangesAsync();
        }

        var cases = new Dictionary<int, int>
        {
            [202512] = v1, [202601] = v1, [202603] = v1, [202604] = v2, [202609] = v2, [202610] = v3, [202612] = v3, [203001] = v3,
        };
        foreach (var (period, expected) in cases)
        {
            await AssertParityAsync(projectId, [period], expected);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "RPT-STABLE")]
    public async Task Скидання_кешу_показує_нову_шкалу_а_свіже_читання_її_бачить_одразу()
    {
        var (v1, v2, _) = await ThreeVersionsAsync();
        var projectId = await NewProjectAsync(v2, 202601, 202612);
        var cache = new MemoryCache(new MemoryCacheOptions());
        await using var db = sql.CreateContext();
        var timeline = new ProjectVersionTimelineService(cache, db);

        Assert.Equal(v2, await timeline.VersionOfAsync(projectId, 202605, CancellationToken.None));   // кешовано: шкали немає

        db.ProjectTemplateVersions.AddRange(new ProjectTemplateVersion(projectId, 202601, v1), new ProjectTemplateVersion(projectId, 202604, v2));
        await db.SaveChangesAsync();

        Assert.Equal(v2, await timeline.VersionOfAsync(projectId, 202602, CancellationToken.None));          // кеш ще старий
        Assert.Equal(v1, await timeline.VersionOfFreshAsync(projectId, 202602, CancellationToken.None));     // писар бачить одразу
        timeline.Invalidate(projectId);
        Assert.Equal(v1, await timeline.VersionOfAsync(projectId, 202602, CancellationToken.None));
    }

    private async Task AssertParityAsync(int projectId, int[] periods, int expected)
    {
        await using var db = sql.CreateContext();
        var service = new ProjectVersionTimelineService(new MemoryCache(new MemoryCacheOptions()), db);
        foreach (var period in periods)
        {
            var viaService = await service.VersionOfFreshAsync(projectId, period, CancellationToken.None);
            var viaSql = await db.Projects
                .Where(p => p.Id == projectId)
                .Select(p => EcrDbContext.VersionOf(p.Id, period))
                .FirstAsync();
            Assert.Equal(expected, viaService);
            Assert.Equal(expected, viaSql);
        }
    }

    private async Task<(int V1, int V2, int V3)> ThreeVersionsAsync()
    {
        await using var db = sql.CreateContext();
        var code = "PTL" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var template = new Template(EcrCode.Create(code), Name(code), 1, Now);
        db.Templates.Add(template);
        await db.SaveChangesAsync();

        var ids = new List<int>();
        foreach (var text in new[] { "1", "2", "3" })
        {
            var version = new TemplateVersion(template.Id, text, 1, Now);
            db.TemplateVersions.Add(version);
            await db.SaveChangesAsync();
            version.Publish(1, Now);
            await db.SaveChangesAsync();
            ids.Add(version.Id);
        }

        return (ids[0], ids[1], ids[2]);
    }

    private async Task<int> NewProjectAsync(int versionId, params int[] periods)
    {
        await using var db = sql.CreateContext();
        var tag = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var policyId = await db.PeriodPolicies.Select(p => p.Id).FirstAsync();
        var project = new Project(
            EcrCode.Create("PRJ" + tag), Name(tag), new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            versionId, PeriodKind.Monthly, policyId, "Asia/Atyrau");
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        foreach (var period in periods)
        {
            var key = new PeriodKey(period);
            db.Periods.Add(new Period(project.Id, key, (byte)key.Sequence, new DateOnly(key.Year, key.Sequence, 1),
                new DateOnly(key.Year, key.Sequence, DateTime.DaysInMonth(key.Year, key.Sequence))));
        }

        await db.SaveChangesAsync();
        return project.Id;
    }

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
