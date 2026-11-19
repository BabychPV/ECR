// tests/Ecr.Infrastructure.Tests/Persistence/RowDefCellDefaultsCloneTests.cs
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// ✎ B175: клон версії копіює дефолти комірок рядка (<c>cfg.RowDef.CellDefaultsJson</c>) — інакше нова версія мовчки
/// втрачала б їх (і, наприклад, Land_Status нового документа знову був би порожнім). На реальній базі.
/// </summary>
/// <remarks>Мутація: <c>Set(row, nameof(RowDef.CellDefaultsJson), null)</c> у циклі рядків <c>TemplateVersionCloner</c>.</remarks>
[Collection("SqlServer")]
public sealed class RowDefCellDefaultsCloneTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B175")]
    public async Task Клон_версії_копіює_дефолти_комірок_рядка_а_рядки_без_них_лишає_без()
    {
        var ct = CancellationToken.None;
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: ct);
        const string json = """{"Land_Status":"RUNNING"}""";

        await using (var setup = builder.CreateContext())
        {
            await setup.Database.ExecuteSqlRawAsync(
                "UPDATE cfg.RowDef SET CellDefaultsJson = {0} WHERE Id = {1}", [json, doc.RowDefIds[0]], ct);
        }

        int cloneId;
        await using (var db = builder.CreateContext())
        {
            cloneId = await new TemplateVersionStore(db).CloneAsync(
                doc.TemplateVersionId, "2.0.0.1", 1, new DateTime(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc), ct);
        }

        await using var read = builder.CreateContext();
        var source = await read.RowDefs.AsNoTracking().SingleAsync(r => r.Id == doc.RowDefIds[0], ct);
        var cloned = await read.RowDefs.AsNoTracking()
            .Where(r => read.TableDefs.Any(t => t.Id == r.TableDefId
                && read.SheetDefs.Any(s => s.Id == t.SheetDefId && s.TemplateVersionId == cloneId)))
            .ToListAsync(ct);

        Assert.Equal(json, source.CellDefaultsJson);
        var copy = Assert.Single(cloned, r => r.RowKeyValue == source.RowKeyValue);
        Assert.NotEqual(source.Id, copy.Id);
        Assert.Equal(json, copy.CellDefaultsJson);
        Assert.Equal("RUNNING", copy.CellDefaults["Land_Status"]);
        Assert.All(cloned.Where(r => r.RowKeyValue != source.RowKeyValue), r => Assert.Null(r.CellDefaultsJson));
    }
}
