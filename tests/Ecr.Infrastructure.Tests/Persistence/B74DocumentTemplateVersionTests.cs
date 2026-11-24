using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// B74 P3 (D-576): документ ПРИЄДНАНОГО шаблону проєкту «рік · об'єкт» — замок правки без періоду й маршрутизація
/// Q-17 беруть версію пари проєкт × шаблон документа, а не версію основного шаблону проєкту.
/// </summary>
[Collection("SqlServer")]
public sealed class B74DocumentTemplateVersionTests(SqlServerFixture sql)
{
    /// <summary>
    /// <c>SheetEditGate.EnterStructureAsync</c> без періоду (шапка, склад, перенос) повертає версію ПАРИ — з нею
    /// писарі звіряють структуру (<c>DocumentStructure.EnsureUnchanged</c>).
    /// </summary>
    /// <remarks>
    /// ⛔ Мутація: у <c>SheetEditGate.GetAppLockAsync</c> без періоду брати <c>p.TemplateVersionId</c> — червоне (версія
    /// основного шаблону A замість B: кожна правка шапки документа B — хибне «структура змінилась»).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B74")]
    [Trait("Decision", "D-576")]
    public async Task Замок_структури_без_періоду_повертає_версію_пари()
    {
        var (a, b, documentB) = await ArrangeAsync();

        await using var db = Context();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var gate = new SheetEditGate(db);

        Assert.Equal(b.TemplateVersionId, await gate.EnterStructureAsync(documentB, exclusive: false, CancellationToken.None));
        Assert.Equal(a.TemplateVersionId, await gate.EnterStructureAsync(a.DocumentId, exclusive: false, CancellationToken.None));
    }

    /// <summary>
    /// Q-17: річну розкладку має лише версія шаблону B — маршрутизатор впізнає документ B (версія пари), а документ
    /// основного шаблону A — ні.
    /// </summary>
    /// <remarks>
    /// ⛔ Мутація: у <c>MonthColumnRouter.GetDocumentAsync</c> брати <c>p.TemplateVersionId</c> — червоне (версія A не
    /// річна: документ B «не маршрутизується», місячні колонки пишуться не у власників).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B74")]
    [Trait("Decision", "D-576")]
    public async Task Маршрутизація_Q17_бере_версію_пари_документа()
    {
        var (a, b, documentB) = await ArrangeAsync();

        var registry = Substitute.For<ITemplateLayoutRegistry>();
        registry.HasAny.Returns(true);
        registry.IsYearVersion(b.TemplateVersionId).Returns(true);

        await using var db = Context();
        var router = new MonthColumnRouter(db, new ConfigurationBuilder().Build(), registry);

        var routed = await router.GetDocumentAsync(documentB, CancellationToken.None);
        Assert.NotNull(routed);
        Assert.Equal(b.TemplateVersionId, routed.TemplateVersionId);
        Assert.Null(await router.GetDocumentAsync(a.DocumentId, CancellationToken.None));
    }

    /// <summary>Проєкт A (основний шаблон A) з приєднаним шаблоном B і документом B.</summary>
    private async Task<(TestDocument A, TestDocument B, long DocumentB)> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var a = await builder.BuildAsync(rowCount: 1);
        var b = await builder.BuildAsync(rowCount: 1);

        await using var db = Context();
        db.ProjectTemplates.Add(new Ecr.Domain.Entities.Documents.ProjectTemplate(a.ProjectId, a.TemplateId, a.TemplateVersionId));
        db.ProjectTemplates.Add(new Ecr.Domain.Entities.Documents.ProjectTemplate(a.ProjectId, b.TemplateId, b.TemplateVersionId));
        var document = new Ecr.Domain.Entities.Documents.Document(
            a.ProjectId, $"B74G-{Guid.NewGuid():N}"[..16], 1, DateTime.UtcNow, b.TemplateId);
        db.Documents.Add(document);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlAsync($"UPDATE doc.Document SET TemplateId = {a.TemplateId} WHERE Id = {a.DocumentId}");

        return (a, b, document.Id);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
