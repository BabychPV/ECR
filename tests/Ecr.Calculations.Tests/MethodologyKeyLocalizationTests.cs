// tests/Ecr.Calculations.Tests/MethodologyKeyLocalizationTests.cs
using Ecr.Application.Ports;
using Ecr.Calculations;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// C1 (клон версії шаблону і ключі методологій): правила (<c>MatchJson</c>) й обов'язкові входи опублікованої
/// версії методології ключуються <c>ColumnDefId</c> версії-ДЖЕРЕЛА, а опублікована версія незмінна. Документ на
/// версії-КЛОНІ мусить давати ті самі результати, що й на джерелі: ключі перекладаються на локальні Id за шляхом
/// «код аркуша → код таблиці → код колонки».
/// </summary>
[Collection("SqlServer")]
public sealed class MethodologyKeyLocalizationTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 10, 8, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>Шаблон-джерело з методологією (правило по колонці 1, вхід — колонка 2) і його клон.</summary>
    private sealed record Stand(
        TestDocument Source,
        int CloneVersionId,
        int CloneTableDefId,
        IReadOnlyList<int> CloneColumnIds,
        int MethodologyVersionId);

    private async Task<Stand> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 3, ct: CancellationToken.None);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        int versionId;
        await using (var db = builder.CreateContext())
        {
            var methodology = new Methodology(
                EcrCode.Create($"CK{tag}"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "C1" }));
            db.Methodologies.Add(methodology);
            await db.SaveChangesAsync();

            var version = new MethodologyVersion(
                methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
            db.MethodologyVersions.Add(version);
            await db.SaveChangesAsync();

            db.MethodologyRules.Add(new MethodologyRule(
                version.Id, EcrCode.Create("R_RUN"), $$"""{"{{doc.ColumnDefIds[0]}}":"Running"}""", 10));
            db.MethodologyRequiredInputs.Add(new MethodologyRequiredInput(
                version.Id, doc.ColumnDefIds[1], RequiredInputSeverity.Block, hint: null));
            await db.SaveChangesAsync();

            version.Publish(2, "C1", new DateOnly(2026, 1, 1), testsPassed: true, Now);
            await db.SaveChangesAsync();
            versionId = version.Id;
        }

        int cloneId;
        await using (var db = builder.CreateContext())
        {
            cloneId = await new TemplateVersionStore(db).CloneAsync(
                doc.TemplateVersionId, $"8.{tag[..4].GetHashCode() & 0xFFF}.0.1", 1, Now, CancellationToken.None);
        }

        await using var read = builder.CreateContext();
        var cloneTable = await read.TableDefs.AsNoTracking()
            .SingleAsync(t => read.SheetDefs.Any(s => s.Id == t.SheetDefId && s.TemplateVersionId == cloneId));
        var sourceCodes = await read.ColumnDefs.AsNoTracking()
            .Where(c => doc.ColumnDefIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Code);
        var cloneByCode = await read.ColumnDefs.AsNoTracking()
            .Where(c => c.TableDefId == cloneTable.Id).ToDictionaryAsync(c => c.Code, c => c.Id);

        return new Stand(
            doc,
            cloneId,
            cloneTable.Id,
            [.. doc.ColumnDefIds.Select(id => cloneByCode[sourceCodes[id]])],
            versionId);
    }

    /// <summary>Два рядки: у першого значення <c>Running</c> у колонці 1, у другого <c>Idle</c>.</summary>
    private static (MethodologyResolver Resolver, long InstanceId) ResolverFor(
        EcrDbContext db, int templateVersionId, int tableDefId, IReadOnlyList<int> columns)
    {
        const long InstanceId = 910;
        var rows = Substitute.For<IRowStore>();
        rows.ResolveTableInstanceAsync(InstanceId, Arg.Any<CancellationToken>())
            .Returns(new TableInstanceRef(InstanceId, 700, tableDefId, templateVersionId, 202601));
        rows.GetRowIdsAsync(InstanceId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, long> { ["R1"] = 1, ["R2"] = 2 });

        var cells = Substitute.For<ICellStore>();
        cells.ReadSliceAsync(InstanceId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(
        [
            new CellRecord(new CellAddress(new PeriodKey(202601), 1, columns[0]), tableDefId, new CellValueData { ValueString = "Running" }),
            new CellRecord(new CellAddress(new PeriodKey(202601), 2, columns[0]), tableDefId, new CellValueData { ValueString = "Idle" }),
        ]);

        return (new MethodologyResolver(new MethodologyStore(db), cells, rows), InstanceId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "C1")]
    public async Task Прогін_на_клон_версії_дає_ті_самі_рядки_що_на_джерелі()
    {
        var stand = await ArrangeAsync();
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();

        var (onSource, sourceInstance) = ResolverFor(
            db, stand.Source.TemplateVersionId, stand.Source.TableDefId, stand.Source.ColumnDefIds);
        var (onClone, cloneInstance) = ResolverFor(
            db, stand.CloneVersionId, stand.CloneTableDefId, stand.CloneColumnIds);

        var expected = await onSource.MatchRowsAsync(stand.MethodologyVersionId, sourceInstance, CancellationToken.None);
        var actual = await onClone.MatchRowsAsync(stand.MethodologyVersionId, cloneInstance, CancellationToken.None);

        Assert.Equal(["R1"], expected);
        Assert.Equal(expected, actual);
    }
}
