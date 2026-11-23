using Ecr.Domain.Enums;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// B74 P3 (D-576): нічна перевірка узгодженості бере «живі» версії і з ПРИЄДНАНИХ шаблонів проєктів
/// (<c>doc.ProjectTemplate</c>), а не лише основний шаблон (<c>Project.TemplateVersionId</c>).
/// </summary>
public sealed partial class ConsistencyCheckJobDetectionTests
{
    /// <summary>
    /// Версія шаблону B живе ЛИШЕ як приєднаний шаблон активного проєкту A (власний проєкт B заархівовано): колонка
    /// <c>Calculated</c> без прив'язки в ній — знахідка <c>UNBOUND_CALCULATED_COLUMN</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ Мутація: прибрати з <c>UnboundCalculatedColumnsAsync</c> чинні версії <c>doc.ProjectTemplate</c> (лише
    /// <c>Project.TemplateVersionId</c>) — червоне: знахідки немає.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B74")]
    [Trait("Decision", "D-576")]
    public async Task B74_колонка_Calculated_у_версії_приєднаного_шаблону_дає_знахідку()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var a = await builder.BuildAsync(rowCount: 1, ct: CancellationToken.None);
        var b = await builder.BuildAsync(rowCount: 1, ct: CancellationToken.None);

        await using (var db = builder.CreateContext())
        {
            var (aProject, bProject, bTemplate, bVersion) = (a.ProjectId, b.ProjectId, b.TemplateId, b.TemplateVersionId);
            var archived = (byte)ProjectStatus.Archived;
            await db.Database.ExecuteSqlAsync($"""
                INSERT doc.ProjectTemplate (ProjectId, TemplateId, CurrentTemplateVersionId) VALUES ({aProject}, {bTemplate}, {bVersion});
                UPDATE doc.Project SET Status = {archived} WHERE Id = {bProject};
                """, CancellationToken.None);
        }

        var (columnId, _, _) = await AddCalculatedColumnAsync(builder, b);

        await RunJobAsync();

        Assert.NotNull(await FindIssueAsync("UNBOUND_CALCULATED_COLUMN", "cfg.ColumnDef", columnId));
    }
}
