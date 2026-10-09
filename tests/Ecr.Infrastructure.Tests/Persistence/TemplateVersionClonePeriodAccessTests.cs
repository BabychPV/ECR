// tests/Ecr.Infrastructure.Tests/Persistence/TemplateVersionClonePeriodAccessTests.cs
using Ecr.Application.Errors;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// PS-P1B (D-PS-2): клон версії шаблону переносить правила доступу до періоду
/// (<c>PeriodAccessRuleDef</c>) з перемапінгом аркуша, таблиці й колонки-джерела вікна на Id клону.
/// </summary>
/// <remarks>
/// До фіксу після кількох клонів лишалось лише правило першої версії. Мутаційний доказ:
/// прибрати <c>CloneAccessRulesAsync</c> із <c>SaveCloneAsync</c> — тест червоніє.
/// </remarks>
[Collection("SqlServer")]
public sealed class TemplateVersionClonePeriodAccessTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "PS-P1B")]
    public async Task Клон_копіює_правила_доступу_до_періоду_на_ключі_клону_і_клон_клона_теж()
    {
        var ct = CancellationToken.None;
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 2, ct: ct);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        await using (var setup = builder.CreateContext())
        {
            setup.PeriodAccessRules.AddRange(
                PeriodAccessRuleDef.EditablePeriodOnly(doc.TemplateVersionId, OutOfWindowBehavior.ReadOnly, 2, 5)
                    .ForSheet(doc.SheetDefId).ForTable(doc.TableDefId),
                PeriodAccessRuleDef.ForSourceWindow(
                    doc.TemplateVersionId, doc.ColumnDefIds[1], OutOfWindowBehavior.ReadOnly)
                    .ForTable(doc.TableDefId),
                PeriodAccessRuleDef.ForExpression(doc.TemplateVersionId, "true", OutOfWindowBehavior.ReadOnly)
                    .ForSheet(doc.SheetDefId));
            await setup.SaveChangesAsync(ct);
        }

        var now = new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc);
        int firstId;
        await using (var db = builder.CreateContext())
        {
            firstId = await new TemplateVersionStore(db).CloneAsync(
                doc.TemplateVersionId, $"6.{tag[..4].GetHashCode() & 0xFFF}.0.1", 1, now, ct);
        }

        int secondId;
        await using (var db = builder.CreateContext())
        {
            secondId = await new TemplateVersionStore(db).CloneAsync(
                firstId, $"7.{tag[..4].GetHashCode() & 0xFFF}.0.1", 1, now, ct);
        }

        await using var read = builder.CreateContext();
        foreach (var cloneId in new[] { firstId, secondId })
        {
            var rules = await read.PeriodAccessRules.AsNoTracking()
                .Where(r => r.TemplateVersionId == cloneId).ToListAsync(ct);
            Assert.Equal(3, rules.Count);

            var tables = await read.TableDefs.AsNoTracking()
                .Where(t => read.SheetDefs.Any(s => s.Id == t.SheetDefId && s.TemplateVersionId == cloneId))
                .ToListAsync(ct);
            var sheetId = await read.SheetDefs.AsNoTracking()
                .Where(s => s.TemplateVersionId == cloneId).Select(s => s.Id).SingleAsync(ct);
            var columns = await read.ColumnDefs.AsNoTracking()
                .Where(c => tables.Select(t => t.Id).Contains(c.TableDefId)).ToListAsync(ct);

            var period = rules.Single(r => r.RuleKind == PeriodAccessRuleKind.EditablePeriodOnly);
            Assert.Equal(sheetId, period.SheetDefId);
            Assert.Equal(tables.Single().Id, period.TableDefId);
            Assert.Equal((byte)2, period.FromSequence);
            Assert.Equal((byte)5, period.ToSequence);

            var window = rules.Single(r => r.RuleKind == PeriodAccessRuleKind.SourceWindow);
            Assert.Contains(window.SourceColumnDefId!.Value, columns.Select(c => c.Id));
            Assert.NotEqual(doc.ColumnDefIds[1], window.SourceColumnDefId);

            Assert.Equal("true", rules.Single(r => r.RuleKind == PeriodAccessRuleKind.Expression).ConditionExpr);
        }

        // Джерело не зачеплене.
        Assert.Equal(3, await read.PeriodAccessRules.CountAsync(r => r.TemplateVersionId == doc.TemplateVersionId, ct));
    }
    /// <summary>
    /// ⛔ A1-02 (аудит 09.10c): правило SourceWindow з колонкою-джерелом ІНШОЇ версії (до A1-02 API таке
    /// приймало) клон мовчки викидав — «випадок неможливий». Тепер клон відмовляє з ключем і id правила, і
    /// транзакція відкочується: чернетки-клону без обмеження періоду не з'являється.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "A1-02")]
    public async Task Клон_не_губить_мовчки_правило_з_колонкою_чужої_версії()
    {
        var ct = CancellationToken.None;
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 2, ct: ct);
        var foreign = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(columnCount: 1, ct: ct);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        int ruleId;
        int templateId;
        await using (var setup = builder.CreateContext())
        {
            var rule = PeriodAccessRuleDef.ForSourceWindow(
                    doc.TemplateVersionId, foreign.ColumnDefIds[0], OutOfWindowBehavior.ReadOnly)
                .ForTable(doc.TableDefId);
            setup.PeriodAccessRules.Add(rule);
            await setup.SaveChangesAsync(ct);
            ruleId = rule.Id;
            templateId = await setup.TemplateVersions.AsNoTracking()
                .Where(v => v.Id == doc.TemplateVersionId).Select(v => v.TemplateId).SingleAsync(ct);
        }

        var versionsBefore = await CountVersionsAsync(builder, templateId, ct);

        await using (var db = builder.CreateContext())
        {
            var error = await Assert.ThrowsAsync<BusinessRuleException>(() => new TemplateVersionStore(db).CloneAsync(
                doc.TemplateVersionId, $"8.{tag[..4].GetHashCode() & 0xFFF}.0.1", 1,
                new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc), ct));

            Assert.Equal("err.ECR-TMPL-0422.cloneAccessRuleForeignRef", error.Details?["messageKey"]);
            Assert.Equal(
                ruleId.ToString(System.Globalization.CultureInfo.InvariantCulture), error.Details?["ruleId"]);
        }

        Assert.Equal(versionsBefore, await CountVersionsAsync(builder, templateId, ct));
    }

    private static async Task<int> CountVersionsAsync(TestDocumentBuilder builder, int templateId, CancellationToken ct)
    {
        await using var db = builder.CreateContext();
        return await db.TemplateVersions.CountAsync(v => v.TemplateId == templateId, ct);
    }
}
