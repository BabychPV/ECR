// tests/Ecr.Infrastructure.Tests/Persistence/SheetChangeRowChangePeriodTests.cs
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// ✎ D-577 (огляд L5): авторство банера «оновив інший користувач» за журналом рядків <c>aud.RowChange</c> шукається лише в
/// періодах рядків проби (партиції <c>IX_RowChange_Document (DocumentId, PeriodKey, ChangedAt)</c>), а не в усіх періодах
/// документа.
/// </summary>
/// <remarks>Мутація (локально): прибрати умову <c>rc.PeriodKey IN (@periods)</c> з <c>SheetChangeStore.AttributeSql</c> — червоний.</remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Requirement", "D-577")]
public sealed class SheetChangeRowChangePeriodTests(SqlServerFixture sql)
{
    [Fact]
    public async Task L5_журнал_рядків_інших_періодів_не_дає_авторства()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(periodKey: 202604, columnCount: 2, rowCount: 2);
        var row = doc.RowIds[0];
        var at = DateTime.UtcNow;

        await using var db = builder.CreateContext();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO aud.RowChange (ChangedAt, DocumentId, PeriodKey, TableInstanceId, TableDefId, TableRowId, RowKey, Action,
                                       ChangedByUserId, Origin, CorrelationId)
            VALUES ({at}, {doc.DocumentId}, {202612}, {doc.TableInstanceId}, {doc.TableDefId}, {row}, {"R_L5"}, 1,
                    {777}, N'UserEdit', NULL)
            """);

        var store = new SheetChangeStore(db);
        var scoped = await store.AttributeAsync(
            doc.DocumentId, [row], [], at.AddMinutes(-15), 1, [doc.PeriodKey.Value], CancellationToken.None);
        var unscoped = await store.AttributeAsync(
            doc.DocumentId, [row], [], at.AddMinutes(-15), 1, CancellationToken.None);

        Assert.DoesNotContain(row, scoped.ForeignRowIds);
        Assert.Contains(row, unscoped.ForeignRowIds);
    }
}
