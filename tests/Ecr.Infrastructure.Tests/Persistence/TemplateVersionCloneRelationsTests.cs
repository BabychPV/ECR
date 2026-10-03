// tests/Ecr.Infrastructure.Tests/Persistence/TemplateVersionCloneRelationsTests.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// T2-02 (тестувальний прохід №2): «Clone version» переносить зв'язки таблиць
/// (Check/Rollup) з ремапом ідентифікаторів таблиць на таблиці КЛОНУ — на живому SQL.
/// </summary>
/// <remarks>
/// ⚠ Код клонованого зв'язку отримує суфікс <c>_v&lt;id версії&gt;</c>:
/// <c>UQ_TableRelationDef</c> унікальний по всій системі (T2-03), а міграцію індексу
/// не робимо. Мутаційний доказ: прибрати <c>CloneTableRelations</c> із
/// <c>SaveCloneAsync</c> — тест червоніє (зв'язків у клоні немає).
/// </remarks>
[Collection("SqlServer")]
public sealed class TemplateVersionCloneRelationsTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "T2-02")]
    public async Task Клон_версії_переносить_зв_язки_на_таблиці_клону()
    {
        var ct = CancellationToken.None;
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: ct);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var code = $"CHK{tag}";

        int secondTableId;
        await using (var setup = builder.CreateContext())
        {
            var second = new TableDef(
                doc.SheetDefId, EcrCode.Create($"TB{tag}"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "B" }), 2,
                TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
            setup.TableDefs.Add(second);
            await setup.SaveChangesAsync(ct);
            secondTableId = second.Id;

            var relation = new TableRelationDef(
                EcrCode.Create(code), doc.TableDefId, secondTableId, TableRelationKind.Check, "{\"k\":1}");
            relation.Update(
                doc.TableDefId, secondTableId, TableRelationKind.Check, "{\"k\":1}", "{\"m\":2}",
                TableRelationDef.OnSourceChangeBlock, isActive: false);
            setup.TableRelations.Add(relation);
            await setup.SaveChangesAsync(ct);
        }

        int cloneId;
        await using (var db = builder.CreateContext())
        {
            cloneId = await new TemplateVersionStore(db).CloneAsync(
                doc.TemplateVersionId, $"3.{tag[..4].GetHashCode() & 0xFFF}.0.1", 1,
                new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc), ct);
        }

        await using var read = builder.CreateContext();
        var cloneTables = await read.TableDefs.AsNoTracking()
            .Where(t => read.SheetDefs.Any(s => s.Id == t.SheetDefId && s.TemplateVersionId == cloneId))
            .ToListAsync(ct);
        var cloneSource = cloneTables.Single(t => t.Code != $"TB{tag}");
        var cloneTarget = cloneTables.Single(t => t.Code == $"TB{tag}");

        var cloned = await read.TableRelations.AsNoTracking()
            .Where(r => cloneTables.Select(t => t.Id).Contains(r.SourceTableDefId))
            .SingleAsync(ct);

        // Таблиці — клону, не джерела.
        Assert.Equal(cloneSource.Id, cloned.SourceTableDefId);
        Assert.Equal(cloneTarget.Id, cloned.TargetTableDefId);
        Assert.NotEqual(doc.TableDefId, cloned.SourceTableDefId);
        Assert.NotEqual(secondTableId, cloned.TargetTableDefId);

        // Налаштування перенесені цілком, включно з вимкненим станом.
        Assert.Equal(TableRelationKind.Check, cloned.RelationKind);
        Assert.Equal("{\"k\":1}", cloned.MatchJson);
        Assert.Equal("{\"m\":2}", cloned.MapJson);
        Assert.Equal(TableRelationDef.OnSourceChangeBlock, cloned.OnSourceChange);
        Assert.False(cloned.IsActive);
        Assert.Equal($"{code}_v{cloneId}", cloned.Code);

        // Джерело не зачеплене.
        var original = await read.TableRelations.AsNoTracking().SingleAsync(r => r.Code == code, ct);
        Assert.Equal(doc.TableDefId, original.SourceTableDefId);
        Assert.Equal(secondTableId, original.TargetTableDefId);
    }
}
