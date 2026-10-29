using Ecr.Application.Audit;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Журнал дій аркуша на справжньому SQL: порядок, фільтри походження/періоду/колонок, курсор через межі сторінок,
/// формат курсора правок. Ціна першої сторінки - у <see cref="SheetActivityPerfTests"/> (власна база).
/// </summary>
[Collection("SqlServer")]
public sealed class SheetActivityReaderTests(SqlServerFixture sql)
{
    private const int Period = 201903;
    private static readonly DateTime T0 = new(2021, 3, 10, 9, 0, 0, DateTimeKind.Utc);

    internal sealed class FixedClock(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc, TimeSpan.Zero);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Формат_курсора_правок_збігається_з_читачем_журналу()
    {
        var at = new DateTime(2021, 3, 10, 9, 0, 1, 234, DateTimeKind.Utc);

        Assert.Equal(Cursor.Encode(at, 77), ActivityCursor.EncodeCellPosition(at, 77));
        Assert.Equal((at, 77L), Cursor.DecodeAt(ActivityCursor.EncodeCellPosition(at, 77)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Журнал_людські_правки_видимих_колонок_періоду_найновіші_першими_через_межі_сторінок()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 3, rowCount: 2, ct: CancellationToken.None);
        var other = await builder.BuildAsync(ct: CancellationToken.None);
        await using var db = builder.CreateContext();

        var visible = doc.ColumnDefIds[1];
        var hidden = doc.ColumnDefIds[2];

        // Однаковий момент (пакет) - тай-брейк за Id; різні походження/період/колонка/документ.
        await Write(db, doc, visible, T0, "e1", "UserEdit");
        await Write(db, doc, visible, T0, "e2", "UserEdit");
        await Write(db, doc, visible, T0, "e3", "Import");
        await Write(db, doc, visible, T0.AddMinutes(1), "e4", "ApproverEdit");
        await Write(db, doc, visible, T0.AddMinutes(2), "SYS", "Recalculation");
        await Write(db, doc, hidden, T0.AddMinutes(3), "HIDDEN", "UserEdit");
        await Write(db, doc, visible, T0.AddMinutes(4), "OTHERPERIOD", "UserEdit", period: 201904);
        await Write(db, other, other.ColumnDefIds[1], T0.AddMinutes(5), "OTHERDOC", "UserEdit");

        var workflow = Substitute.For<IWorkflowStore>();
        workflow.GetHistoryAsync(doc.DocumentId, Arg.Any<PeriodKey>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([new ApprovalEventRecord("SA", DocumentStatus.Submitted, DocumentStatus.Rejected, ApprovalAction.Reject, 1, "Ann",
                T0.AddMinutes(1), "reason", null, Id: 9)]);

        var query = new SheetActivityQuery(new AuditReader(db), workflow, new FixedClock(T0.AddDays(30)));
        var scope = new SheetActivityScope(doc.DocumentId, Period, "SA", [visible]);

        var walked = await Walk(query, scope, limit: 2);

        // Подія процесу (T0+1) стоїть ПІСЛЯ правки того ж моменту (тег 0 раніше за тег 1).
        Assert.Equal(["e4", "reason", "e3", "e2", "e1"], walked.Select(i => i.NewValue ?? i.Reason));
        Assert.Equal(["ApproverEdit", null, "Import", "UserEdit", "UserEdit"], walked.Select(i => i.Origin));

        // Системні зміни - лише за прапорцем; колонка прихованої таблиці не з'являється за жодного прапорця.
        var withSystem = await Walk(query, scope with { IncludeSystem = true }, limit: 3);
        Assert.Contains(withSystem, i => i.NewValue == "SYS" && i.ByDisplayName == "system");
        Assert.DoesNotContain(withSystem, i => i.NewValue is "HIDDEN" or "OTHERPERIOD" or "OTHERDOC");
    }

    internal static async Task<List<SheetActivityItem>> Walk(SheetActivityQuery query, SheetActivityScope scope, int limit)
    {
        var items = new List<SheetActivityItem>();
        string? cursor = null;
        for (var pages = 0; pages < 30; pages++)
        {
            var page = await query.ReadAsync(scope, limit, cursor, CancellationToken.None);
            items.AddRange(page.Items);
            cursor = page.NextCursor;
            if (cursor is null)
            {
                return items;
            }
        }

        throw new InvalidOperationException("курсор не завершується");
    }

    private static Task Write(
        EcrDbContext db, TestDocument doc, int columnDefId, DateTime at, string value, string origin, int period = Period)
        => new AuditWriter(db).WriteCellChangesAsync(
            [new CellChangeRecord(
                at, new CellAddress(new PeriodKey(period), doc.RowIds[0], columnDefId), doc.DocumentId, "R1",
                OldValue: "1", NewValue: value, ChangedByUserId: 1, Origin: origin, IsLateEdit: false, CorrelationId: null)],
            CancellationToken.None);
}
