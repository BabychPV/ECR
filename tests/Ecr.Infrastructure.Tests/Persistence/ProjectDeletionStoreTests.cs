using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// UAT 08.11 (c): видалення порожньої чернетки на справжній базі — факти для рішення і саме видалення разом із
/// періодами (зовнішні ключі <c>NO ACTION</c>, тож порядок у <c>ProjectStore.DeleteAsync</c> — частина контракту).
/// </summary>
/// <remarks>
/// Мутація: прибрати <c>DELETE FROM doc.Period</c> у <c>DeleteAsync</c> — червоніє (FK <c>Period → Project</c>);
/// прибрати <c>UPDATE … CurrentPeriodId = NULL</c> — червоніє (FK <c>Project → Period</c>).
/// </remarks>
[Collection("SqlServer")]
public sealed class ProjectDeletionStoreTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Порожня_чернетка_з_періодами_і_поточним_періодом_видаляється_повністю()
    {
        await using var db = Context();

        var template = new Template(EcrCode.Create($"TPLDEL_{_tag}"), Text("Template"), 1, DateTime.UtcNow);
        db.Templates.Add(template);
        await db.SaveChangesAsync();

        var version = new TemplateVersion(template.Id, "1.0.0.0", 1, DateTime.UtcNow);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();

        var project = new Project(
            EcrCode.Create($"DELPRJ_{_tag}"), Text("Delete me"),
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            version.Id, PeriodKind.Monthly, periodPolicyId: 1, "Asia/Atyrau");
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        var period = new Period(project.Id, new PeriodKey(202601), 1, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        db.Periods.Add(period);
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlAsync(
            $"UPDATE doc.Project SET CurrentPeriodId = {period.Id} WHERE Id = {project.Id}");

        var store = new ProjectStore(db);

        var facts = await store.GetDeletionFactsAsync(project.Id, CancellationToken.None);

        Assert.NotNull(facts);
        Assert.Equal(ProjectStatus.Draft, facts.Status);
        Assert.Equal(0, facts.DocumentCount);
        Assert.Equal(0, facts.HistoryCount);
        Assert.Equal($"DELPRJ_{_tag}", facts.Code);

        await store.DeleteAsync(project.Id, CancellationToken.None);

        await using var check = Context();
        Assert.False(await check.Projects.AnyAsync(p => p.Id == project.Id));
        Assert.False(await check.Periods.AnyAsync(p => p.ProjectId == project.Id));
        Assert.Null(await new ProjectStore(check).GetDeletionFactsAsync(project.Id, CancellationToken.None));
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
