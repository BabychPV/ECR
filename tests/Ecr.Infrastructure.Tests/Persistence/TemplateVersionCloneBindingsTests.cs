// tests/Ecr.Infrastructure.Tests/Persistence/TemplateVersionCloneBindingsTests.cs
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// D-13: «Clone version» переносить прив'язки результатів методологій до колонок
/// (<c>cfg.CalculationBinding</c>) з ремапом <c>ColumnDefId</c>/<c>TableDefId</c> на колонки КЛОНУ — на живому SQL.
/// </summary>
/// <remarks>
/// Без цього клон опублікованої версії мав обчислювані колонки без жодного джерела, а повторний PUT
/// прив'язки на клоні створював нову (вимкнену) прив'язку, яку F-09 відхиляв.
/// Мутаційний доказ: прибрати <c>CloneCalculationBindingsAsync</c> із <c>SaveCloneAsync</c> — тести червоні.
/// </remarks>
[Collection("SqlServer")]
public sealed class TemplateVersionCloneBindingsTests(SqlServerFixture sql)
{
    private const string ActiveMatch = "{\"Land_Status\":\"Running\"}";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-13")]
    public async Task Клон_версії_переносить_прив_язки_методологій_на_колонки_клону()
    {
        var ct = CancellationToken.None;
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 3, ct: ct);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        int methodologyId;
        await using (var setup = builder.CreateContext())
        {
            var methodology = new Methodology(
                EcrCode.Create($"MB{tag}"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Bound" }));
            setup.Methodologies.Add(methodology);
            await setup.SaveChangesAsync(ct);
            methodologyId = methodology.Id;

            // Дві активні прив'язки з різним предикатом і одна ВИМКНЕНА: клон не має ні втрачати, ні вмикати.
            setup.CalculationBindings.Add(new CalculationBinding(
                doc.TableDefId, doc.ColumnDefIds[0], methodologyId, "TONS", ActiveMatch));
            setup.CalculationBindings.Add(new CalculationBinding(
                doc.TableDefId, doc.ColumnDefIds[1], methodologyId, "GSEC", "{}"));
            var disabled = new CalculationBinding(
                doc.TableDefId, doc.ColumnDefIds[2], methodologyId, "TONS", "{\"k\":1}");
            disabled.Update("{\"k\":1}", isActive: false);
            setup.CalculationBindings.Add(disabled);
            await setup.SaveChangesAsync(ct);
        }

        int cloneId;
        await using (var db = builder.CreateContext())
        {
            cloneId = await new TemplateVersionStore(db).CloneAsync(
                doc.TemplateVersionId, $"8.{tag[..4].GetHashCode() & 0xFFF}.0.1", 1,
                new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc), ct);
        }

        await using var read = builder.CreateContext();
        var cloneTable = await read.TableDefs.AsNoTracking()
            .SingleAsync(t => read.SheetDefs.Any(s => s.Id == t.SheetDefId && s.TemplateVersionId == cloneId), ct);
        var cloneColumns = await read.ColumnDefs.AsNoTracking()
            .Where(c => c.TableDefId == cloneTable.Id).ToDictionaryAsync(c => c.Code, ct);
        var sourceCodes = await read.ColumnDefs.AsNoTracking()
            .Where(c => doc.ColumnDefIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Code, ct);

        var cloned = await read.CalculationBindings.AsNoTracking()
            .Where(b => b.TableDefId == cloneTable.Id).OrderBy(b => b.OutputCode).ThenBy(b => b.Id).ToListAsync(ct);

        Assert.Equal(3, cloned.Count);

        // Колонка/таблиця — КЛОНУ (за кодом), методологія й вихід ті самі.
        var first = cloned.Single(b => b.ColumnDefId == cloneColumns[sourceCodes[doc.ColumnDefIds[0]]].Id);
        Assert.Equal(methodologyId, first.MethodologyId);
        Assert.Equal("TONS", first.OutputCode);
        Assert.Equal(ActiveMatch, first.MatchJson);
        Assert.True(first.IsActive);
        Assert.NotEqual(doc.ColumnDefIds[0], first.ColumnDefId);

        var second = cloned.Single(b => b.ColumnDefId == cloneColumns[sourceCodes[doc.ColumnDefIds[1]]].Id);
        Assert.Equal("GSEC", second.OutputCode);
        Assert.True(second.IsActive);

        var third = cloned.Single(b => b.ColumnDefId == cloneColumns[sourceCodes[doc.ColumnDefIds[2]]].Id);
        Assert.False(third.IsActive);
        Assert.Equal("{\"k\":1}", third.MatchJson);

        // Джерело не зачеплене.
        var original = await read.CalculationBindings.AsNoTracking()
            .Where(b => b.TableDefId == doc.TableDefId && b.MethodologyId == methodologyId).CountAsync(ct);
        Assert.Equal(3, original);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-13")]
    public async Task Прив_язка_у_клоні_знаходиться_за_трійкою_тож_повторний_PUT_правит_її_а_не_створює_вимкнену()
    {
        // F-09: `SaveLockedAsync` шукає наявну прив'язку за (колонка, методологія, вихід). Без копії при клоні
        // трійки в клоні немає → PUT створює НОВУ, і вимкнена відхиляється `bindingUnknownOutput`.
        var ct = CancellationToken.None;
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 1, ct: ct);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        int methodologyId;
        await using (var setup = builder.CreateContext())
        {
            var methodology = new Methodology(
                EcrCode.Create($"MP{tag}"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Put" }));
            setup.Methodologies.Add(methodology);
            await setup.SaveChangesAsync(ct);
            methodologyId = methodology.Id;
            setup.CalculationBindings.Add(new CalculationBinding(
                doc.TableDefId, doc.ColumnDefIds[0], methodologyId, "TONS", "{}"));
            await setup.SaveChangesAsync(ct);
        }

        int cloneId;
        await using (var db = builder.CreateContext())
        {
            cloneId = await new TemplateVersionStore(db).CloneAsync(
                doc.TemplateVersionId, $"9.{tag[..4].GetHashCode() & 0xFFF}.0.1", 1,
                new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc), ct);
        }

        await using var read = builder.CreateContext();
        var sourceCode = await read.ColumnDefs.AsNoTracking()
            .Where(c => c.Id == doc.ColumnDefIds[0]).Select(c => c.Code).SingleAsync(ct);
        var cloneColumnId = await read.ColumnDefs.AsNoTracking()
            .Where(c => c.Code == sourceCode
                        && read.TableDefs.Any(t => t.Id == c.TableDefId
                            && read.SheetDefs.Any(s => s.Id == t.SheetDefId && s.TemplateVersionId == cloneId)))
            .Select(c => c.Id).SingleAsync(ct);

        var found = await new CalculationBindingStore(read).FindAsync(cloneColumnId, methodologyId, "TONS", ct);

        Assert.NotNull(found);
        Assert.True(found.IsActive);
    }
}
