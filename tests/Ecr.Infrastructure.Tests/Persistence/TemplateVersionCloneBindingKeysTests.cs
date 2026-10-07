// tests/Ecr.Infrastructure.Tests/Persistence/TemplateVersionCloneBindingKeysTests.cs
using System.Text.Json;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// D1: ключі предиката прив'язки (<c>CalculationBinding.MatchJson</c>) — це <c>ColumnDefId</c> версії-джерела,
/// які <c>CalculatedCellOverlay</c> зіставляє з колонками документа. Клон мусить переписати їх на Id колонок
/// КЛОНУ; ключ без відповідника — прив'язка не копіюється (прибрати предикат = розширити на всі рядки).
/// </summary>
[Collection("SqlServer")]
public sealed class TemplateVersionCloneBindingKeysTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D1")]
    public async Task Клон_переписує_ключі_предиката_прив_язки_на_колонки_клону_і_не_копіює_прив_язку_з_чужим_ключем()
    {
        var ct = CancellationToken.None;
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 3, ct: ct);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        var keyed = $$"""{"{{doc.ColumnDefIds[1]}}":"Running","{{doc.ColumnDefIds[2]}}":"Gas"}""";
        const string Orphan = """{"2000000000":"X"}""";

        await using (var setup = builder.CreateContext())
        {
            var methodology = new Methodology(
                EcrCode.Create($"MK{tag}"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Keys" }));
            setup.Methodologies.Add(methodology);
            await setup.SaveChangesAsync(ct);

            setup.CalculationBindings.Add(new CalculationBinding(
                doc.TableDefId, doc.ColumnDefIds[0], methodology.Id, "TONS", keyed));
            setup.CalculationBindings.Add(new CalculationBinding(
                doc.TableDefId, doc.ColumnDefIds[1], methodology.Id, "GSEC", Orphan));
            await setup.SaveChangesAsync(ct);
        }

        int cloneId;
        await using (var db = builder.CreateContext())
        {
            cloneId = await new TemplateVersionStore(db).CloneAsync(
                doc.TemplateVersionId, $"7.{tag[..4].GetHashCode() & 0xFFF}.0.1", 1,
                new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc), ct);
        }

        await using var read = builder.CreateContext();
        var cloneTable = await read.TableDefs.AsNoTracking()
            .SingleAsync(t => read.SheetDefs.Any(s => s.Id == t.SheetDefId && s.TemplateVersionId == cloneId), ct);
        var cloneByCode = await read.ColumnDefs.AsNoTracking()
            .Where(c => c.TableDefId == cloneTable.Id).ToDictionaryAsync(c => c.Code, ct);
        var sourceCodes = await read.ColumnDefs.AsNoTracking()
            .Where(c => doc.ColumnDefIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Code, ct);

        var cloned = await read.CalculationBindings.AsNoTracking()
            .Where(b => b.TableDefId == cloneTable.Id).ToListAsync(ct);

        // Прив'язка з ключем без відповідника не скопійована (fail-closed); друга — на місці.
        var copy = Assert.Single(cloned);
        Assert.Equal("TONS", copy.OutputCode);

        // Ключі — Id колонок КЛОНУ (за кодом), значення й порядок збережені, жодного ключа джерела.
        var expected = new Dictionary<string, string>
        {
            [cloneByCode[sourceCodes[doc.ColumnDefIds[1]]].Id.ToString(System.Globalization.CultureInfo.InvariantCulture)] = "Running",
            [cloneByCode[sourceCodes[doc.ColumnDefIds[2]]].Id.ToString(System.Globalization.CultureInfo.InvariantCulture)] = "Gas",
        };
        var actual = JsonSerializer.Deserialize<Dictionary<string, string>>(copy.MatchJson)!;
        Assert.Equal(expected, actual);

        // Джерело не зачеплене.
        var original = await read.CalculationBindings.AsNoTracking()
            .SingleAsync(b => b.TableDefId == doc.TableDefId && b.OutputCode == "TONS", ct);
        Assert.Equal(keyed, original.MatchJson);
    }
}
