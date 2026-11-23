// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.B74PermitRepair.cs
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// B74 P3 (D-576): ремонт історії дозволів у сіді (<c>MIGR:permit-history-repair</c>, <c>99-epilogue.sql</c>) звіряє рядок
/// документа з версією ПАРИ проєкт × шаблон документа, а не з версією основного шаблону проєкту.
/// </summary>
public sealed partial class DocumentVersionMigrationTests
{
    /// <summary>
    /// Документ другого (не основного) шаблону проєкту з історією дозволів на чинній версії СВОГО шаблону: сід не
    /// чіпає рядків і не пише знахідку <c>PERMIT_HISTORY_STALE_VERSION</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ Мутація: крок 4 знову порівнює з <c>pr.TemplateVersionId</c> (версія основного шаблону) — червоне: на кожен рядок
    /// історії документа другого шаблону з'являється хибна відкрита знахідка.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B74")]
    [Trait("Decision", "D-576")]
    public async Task B74_історія_дозволів_документа_другого_шаблону_не_є_застарілою()
    {
        var s = await ArrangeAsync(Target.OnlyLabels).ConfigureAwait(true);
        _ = await ArrangePermitHistoryAsync(s, s.Doc.ColumnDefIds[1]).ConfigureAwait(true);

        // Основним шаблоном проєкту стає ІНШИЙ шаблон X; шаблон документа (з історією на своїй версії) — приєднаний.
        var other = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync().ConfigureAwait(true);
        await using (var arrange = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            var (project, document, own, ownVersion, x, xVersion) = (
                s.Doc.ProjectId, s.Doc.DocumentId, s.Doc.TemplateId, s.Doc.TemplateVersionId, other.TemplateId, other.TemplateVersionId);
            await arrange.Database.ExecuteSqlAsync($"""
                DELETE FROM doc.ProjectTemplate WHERE ProjectId = {project};
                INSERT doc.ProjectTemplate (ProjectId, TemplateId, CurrentTemplateVersionId) VALUES ({project}, {x}, {xVersion});
                INSERT doc.ProjectTemplate (ProjectId, TemplateId, CurrentTemplateVersionId) VALUES ({project}, {own}, {ownVersion});
                UPDATE doc.Project SET TemplateVersionId = {xVersion} WHERE Id = {project};
                UPDATE doc.Document SET TemplateId = {own} WHERE Id = {document};
                """).ConfigureAwait(true);
        }

        var run = await RunSeedRepairAsync().ConfigureAwait(true);

        await using var check = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var documentId = s.Doc.DocumentId;
        var stale = await check.Database.SqlQuery<int>($"""
                SELECT COUNT(*) AS Value FROM aud.ConsistencyIssue AS c
                JOIN   doc.PermitSegment AS g ON g.Id = c.EntityId
                WHERE  c.RuleCode = N'PERMIT_HISTORY_STALE_VERSION' AND c.EntityType = N'doc.PermitSegment'
                  AND  c.ResolvedAt IS NULL AND g.DocumentId = {documentId}
                """).SingleAsync().ConfigureAwait(true);
        Assert.True(stale == 0, $"хибні знахідки: {stale}; {run.Line}");

        var segments = await check.PermitSegments.AsNoTracking()
            .Where(g => g.DocumentId == documentId && g.TableDefId != null)
            .Select(g => g.TableDefId!.Value)
            .ToListAsync().ConfigureAwait(true);
        Assert.NotEmpty(segments);
        Assert.All(segments, t => Assert.Equal(s.Doc.TableDefId, t));
    }
}
