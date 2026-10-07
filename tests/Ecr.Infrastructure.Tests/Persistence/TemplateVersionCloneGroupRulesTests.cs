// tests/Ecr.Infrastructure.Tests/Persistence/TemplateVersionCloneGroupRulesTests.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// D-13: «Clone version» переносить правила складу документа (<c>cfg.SheetGroupRule</c>) — вони належать ВЕРСІЇ
/// шаблону (<c>TemplateVersionId</c>) і посилаються на групи аркушів ЗА ТЕКСТОМ, тож ремап Id не потрібен.
/// </summary>
/// <remarks>
/// <c>cfg.RegistryRuleDef</c> належить довіднику (<c>RegistryDefId</c>), а довідник — не версіонований, тому
/// клон версії його не торкається й тут не перевіряється.
/// Мутаційний доказ: прибрати <c>CloneSheetGroupRulesAsync</c> із <c>SaveCloneAsync</c> — тест червоний.
/// </remarks>
[Collection("SqlServer")]
public sealed class TemplateVersionCloneGroupRulesTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-13")]
    public async Task Клон_версії_переносить_правила_складу_документа()
    {
        var ct = CancellationToken.None;
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 1, ct: ct);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        await using (var setup = builder.CreateContext())
        {
            setup.SheetGroupRules.Add(new SheetGroupRule(doc.TemplateVersionId, "Water", 0, null));
            setup.SheetGroupRules.Add(new SheetGroupRule(doc.TemplateVersionId, "Air", 1, null));
            setup.SheetGroupRules.Add(new SheetGroupRule(doc.TemplateVersionId, "Water", 2, "Air"));
            await setup.SaveChangesAsync(ct);
        }

        int cloneId;
        await using (var db = builder.CreateContext())
        {
            cloneId = await new TemplateVersionStore(db).CloneAsync(
                doc.TemplateVersionId, $"7.{tag[..4].GetHashCode() & 0xFFF}.0.1", 1,
                new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc), ct);
        }

        await using var read = builder.CreateContext();
        var cloned = (await read.SheetGroupRules.AsNoTracking()
            .Where(r => r.TemplateVersionId == cloneId).ToListAsync(ct))
            .Select(r => (r.SheetGroup, r.RuleKind, r.TargetGroup)).OrderBy(r => r.RuleKind).ToList();

        Assert.Equal(
            [("Water", (byte)0, (string?)null), ("Air", (byte)1, null), ("Water", (byte)2, "Air")],
            cloned);

        // Джерело не зачеплене.
        Assert.Equal(3, await read.SheetGroupRules.AsNoTracking()
            .CountAsync(r => r.TemplateVersionId == doc.TemplateVersionId, ct));
    }
}