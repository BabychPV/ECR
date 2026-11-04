using Ecr.Application.Audit;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Журнал аркуша з третім джерелом — додавання й видалення рядків (<c>aud.RowChange</c>, тег 2) — на справжньому SQL:
/// злиття з правками комірок у порядку «найновіші перші», межі читання за таблицею, автор, системне походження й
/// курсор через межі сторінок (рівні моменти не губляться і не дублюються).
/// </summary>
[Collection("SqlServer")]
public sealed class SheetActivityRowChangeTests(SqlServerFixture sql)
{
    private const int Period = 201903;
    private static readonly DateTime T0 = new(2021, 3, 10, 9, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.21")]
    public async Task Журнал_аркуша_показує_додані_й_видалені_рядки_видимих_таблиць_разом_із_правками(int limit)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 2, rowCount: 1, ct: CancellationToken.None);
        await using var db = builder.CreateContext();

        var hiddenTable = doc.TableDefId + 1_000_000; // таблиця, якої читач не бачить (не в переліку видимих)
        var rows = new RowChangeAudit(db);

        // Правка комірки й «додано рядок» в ОДИН момент: правка (тег 0) раніше за рядок (тег 2).
        await Cell(db, doc, T0, "v1");
        await rows.WriteRowChangesAsync(
            [
                Row(doc, doc.TableDefId, T0, "NEW1", RowChangeAction.Added, "UserEdit"),
                Row(doc, doc.TableDefId, T0, "NEW2", RowChangeAction.Added, "Import"),
                Row(doc, doc.TableDefId, T0.AddMinutes(1), "NEW1", RowChangeAction.Deleted, "UserEdit"),
                Row(doc, hiddenTable, T0.AddMinutes(2), "HIDDEN", RowChangeAction.Added, "UserEdit"),
                Row(doc, doc.TableDefId, T0.AddMinutes(3), "SYS", RowChangeAction.Added, "Integration"),
                Row(doc, doc.TableDefId, T0.AddMinutes(4), "OTHERPERIOD", RowChangeAction.Added, "UserEdit", period: 201904),
                Row(doc, doc.TableDefId, T0.AddMinutes(5), "OTHERUSER", RowChangeAction.Added, "UserEdit", userId: 2),
            ],
            CancellationToken.None);

        var query = new SheetActivityQuery(
            new AuditReader(db), Substitute.For<IWorkflowStore>(), new SheetActivityReaderTests.FixedClock(T0.AddDays(30)), rows);
        var scope = new SheetActivityScope(
            doc.DocumentId, Period, "SA", [doc.ColumnDefIds[1]], ByUserId: 1, VisibleTableIds: [doc.TableDefId]);

        var walked = await SheetActivityReaderTests.Walk(query, scope, limit);

        Assert.Equal(
            ["RowChange:RowDeleted:NEW1", "CellEdit:v1", "RowChange:RowAdded:NEW2", "RowChange:RowAdded:NEW1"],
            walked.Select(Describe));
        var deleted = walked[0];
        Assert.Equal("UserEdit", deleted.Origin);
        Assert.NotNull(deleted.TableCode);
        Assert.NotEqual(SheetActivityQuery.SystemActor, deleted.ByDisplayName);
        Assert.Equal("Import", walked[2].Origin);

        // Системне походження — лише за прапорцем і з підписом «system»; прихована таблиця й інший період — ніколи.
        var withSystem = await SheetActivityReaderTests.Walk(query, scope with { IncludeSystem = true }, limit);
        Assert.Contains(withSystem, i => i.RowKey == "SYS" && i.ByDisplayName == SheetActivityQuery.SystemActor);
        Assert.DoesNotContain(withSystem, i => i.RowKey is "HIDDEN" or "OTHERPERIOD" or "OTHERUSER");

        // Без видимих таблиць — жодного рядка «додано/видалено» (закрито за замовчуванням).
        var noTables = await SheetActivityReaderTests.Walk(query, scope with { VisibleTableIds = [] }, limit);
        Assert.Equal(["CellEdit:v1"], noTables.Select(Describe));
    }

    private static string Describe(SheetActivityItem item)
        => item.Kind == SheetActivityKinds.RowChange ? $"{item.Kind}:{item.Action}:{item.RowKey}" : $"{item.Kind}:{item.NewValue}";

    private static RowChangeRecord Row(
        TestDocument doc, int tableDefId, DateTime at, string key, RowChangeAction action, string origin,
        int period = Period, int userId = 1)
        => new(at, doc.DocumentId, period, doc.TableInstanceId, tableDefId, doc.RowIds[0], key, action, userId, origin, null);

    private static Task Cell(EcrDbContext db, TestDocument doc, DateTime at, string value)
        => new AuditWriter(db).WriteCellChangesAsync(
            [new CellChangeRecord(
                at, new CellAddress(new PeriodKey(Period), doc.RowIds[0], doc.ColumnDefIds[1]), doc.DocumentId, "R1",
                OldValue: null, NewValue: value, ChangedByUserId: 1, Origin: "UserEdit", IsLateEdit: false, CorrelationId: null)],
            CancellationToken.None);
}
