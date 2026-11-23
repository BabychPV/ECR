using Ecr.Domain.Enums;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// B74 P3 (D-576): у проєкті «рік · об'єкт» документ річної розкладки належить ПРИЄДНАНОМУ шаблону, а основний шаблон
/// проєкту — інший («копія на місяць»). Розкладку якоря року (B24) задає шкала шаблону ДОКУМЕНТА, а не основного.
/// </summary>
public sealed partial class YearLayoutCalculatedRoutingSqlTests
{
    /// <summary>
    /// Закритий січень, відкритий рік: перерахунок якоря документа другого шаблону пише не-місячний підсумок, як і в
    /// одношаблонному проєкті (B24), — бо річну розкладку має версія ЙОГО шаблону.
    /// </summary>
    /// <remarks>
    /// ⛔ Мутація: у <c>RecalculationJob.AnnualOnlyAnchorsAsync</c> брати <c>doc.fn_VersionOf</c> (основний шаблон
    /// «копія на місяць») для будь-якого документа — червоне: задача відмовляє <c>ECR-CALC-4221 periodClosed</c>.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B74")]
    [Trait("Decision", "D-576")]
    public async Task B74_якір_року_документа_другого_шаблону_бере_розкладку_свого_шаблону()
    {
        var world = await ArrangeAsync();
        await SeedInputsAsync(world);
        await SetCellAsync(world, 9, world.CalcMonth[9], 9 * Input, calculated: true);
        await SetCellAsync(world, Anchor, world.Total, 0m, calculated: true);
        await SetCellAsync(world, Anchor, world.CalcMonth[Anchor], -1m, calculated: true);
        await SetLayoutAsync(world, TemplateLayout.YearWithMonthColumns);

        // Основним шаблоном проєкту стає інший шаблон X («копія на місяць»); шаблон документа — приєднаний.
        var other = await world.Builder.BuildAsync(periodKey: Period(Anchor), columnCount: 1, rowCount: 1);
        await using (var db = world.Builder.CreateContext())
        {
            var (project, document, own, ownVersion, x, xVersion) = (
                world.Doc.ProjectId, world.Doc.DocumentId, world.Doc.TemplateId, world.Doc.TemplateVersionId,
                other.TemplateId, other.TemplateVersionId);
            await db.Database.ExecuteSqlAsync($"""
                DELETE FROM doc.ProjectTemplate WHERE ProjectId = {project};
                INSERT doc.ProjectTemplate (ProjectId, TemplateId, CurrentTemplateVersionId) VALUES ({project}, {x}, {xVersion});
                INSERT doc.ProjectTemplate (ProjectId, TemplateId, CurrentTemplateVersionId) VALUES ({project}, {own}, {ownVersion});
                UPDATE doc.Project SET TemplateVersionId = {xVersion} WHERE Id = {project};
                UPDATE doc.Document SET TemplateId = {own} WHERE Id = {document};
                """);
        }

        await CloseAsync(world, Anchor);

        await RunJobAsync(world, periodKey: Period(Anchor));

        Assert.Equal(9 * Input, await PhysicalAsync(world, Anchor, world.Total));
        Assert.Equal(-1m, await PhysicalAsync(world, Anchor, world.CalcMonth[Anchor]));
    }
}
