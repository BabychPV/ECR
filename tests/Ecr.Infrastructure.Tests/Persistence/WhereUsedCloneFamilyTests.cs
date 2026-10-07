// tests/Ecr.Infrastructure.Tests/Persistence/WhereUsedCloneFamilyTests.cs
using Ecr.Application.Common;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// C1: «де використовується» колонки клон-версії шаблону показує правила й вимоги методологій, що ключуються
/// Id її відповідника в версії-джерелі (за шляхом аркуш/таблиця/колонка).
/// </summary>
[Collection("SqlServer")]
public sealed class WhereUsedCloneFamilyTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "C1")]
    public async Task Колонка_клону_бачить_вимогу_і_правило_що_ключуються_колонкою_джерела()
    {
        var ct = CancellationToken.None;
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 3, ct: ct);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var now = new DateTime(2026, 10, 8, 9, 0, 0, DateTimeKind.Utc);

        await using (var db = builder.CreateContext())
        {
            var methodology = new Methodology(
                EcrCode.Create($"WU{tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "WU" }));
            db.Methodologies.Add(methodology);
            await db.SaveChangesAsync(ct);

            var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, 1, now);
            db.MethodologyVersions.Add(version);
            await db.SaveChangesAsync(ct);

            db.MethodologyRules.Add(new MethodologyRule(
                version.Id, EcrCode.Create("R_WU"), $$"""{"{{doc.ColumnDefIds[0]}}":"X"}""", 10));
            db.MethodologyRequiredInputs.Add(new MethodologyRequiredInput(
                version.Id, doc.ColumnDefIds[1], RequiredInputSeverity.Block, hint: null));
            await db.SaveChangesAsync(ct);
        }

        int cloneId;
        await using (var db = builder.CreateContext())
        {
            cloneId = await new TemplateVersionStore(db).CloneAsync(
                doc.TemplateVersionId, $"9.{tag[..4].GetHashCode() & 0xFFF}.0.1", 1, now, ct);
        }

        await using var read = builder.CreateContext();
        var cloneTable = await read.TableDefs.AsNoTracking()
            .SingleAsync(t => read.SheetDefs.Any(s => s.Id == t.SheetDefId && s.TemplateVersionId == cloneId), ct);
        var cloneColumns = await read.ColumnDefs.AsNoTracking()
            .Where(c => c.TableDefId == cloneTable.Id).OrderBy(c => c.Id).Select(c => c.Id).ToListAsync(ct);

        var store = new WhereUsedStore(read);

        var requiredInput = await store.FindColumnUsageAsync(cloneColumns[1], 50, ct);
        Assert.Contains(requiredInput.Items, i => i.Kind == UsageKinds.MethodologyRequiredInput);

        var rule = await store.FindColumnUsageAsync(cloneColumns[0], 50, ct);
        Assert.Contains(rule.Items, i => i.Kind == UsageKinds.MethodologyRule && i.Label == "R_WU");

        // Колонка без згадок у методологіях їх і не показує.
        var none = await store.FindColumnUsageAsync(cloneColumns[2], 50, ct);
        Assert.DoesNotContain(none.Items, i => i.Kind is UsageKinds.MethodologyRule or UsageKinds.MethodologyRequiredInput);
    }
}
