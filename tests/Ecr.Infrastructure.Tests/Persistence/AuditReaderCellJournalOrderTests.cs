using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// P1-07 (порядок журналу за кластерним ключем <c>(ChangedAt, Id)</c>) і AN-98 / N3-07 (історія комірки —
/// найновіші першими й лише за періодом документа).
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЙНІ ДОКАЗИ: повернути у <c>ReadCellChangesAsync</c> <c>ORDER BY Id</c> — червоніє
/// <see cref="Журнал_документа_йде_за_ChangedAt_потім_Id_а_курсор_не_губить_і_не_дублює_пакет"/>;
/// прибрати умову <c>PeriodKey = @periodKey</c> — червоніє
/// <see cref="Історія_комірки_лише_за_період_найновіші_першими_з_курсором"/>.
/// </remarks>
[Collection("SqlServer")]
public sealed class AuditReaderCellJournalOrderTests(SqlServerFixture sql)
{
    private static readonly DateTime At = new(2026, 3, 4, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "R-18")]
    public async Task Журнал_документа_йде_за_ChangedAt_потім_Id_а_курсор_не_губить_і_не_дублює_пакет()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);

        await using var db = builder.CreateContext();
        var writer = new AuditWriter(db);

        // ⚠ Кожен запис — окремим INSERT: Id зростають у порядку запису, а ChangedAt — НІ. Старий `ORDER BY Id`
        // віддав би r1, r2, r3, r4, r5; кластерний порядок — r2, r3, r4, r5, r1. r3..r5 мають ОДИН ChangedAt.
        await writer.WriteCellChangesAsync([Change(doc, "r1", 202603, At.AddMinutes(10))], CancellationToken.None);
        await writer.WriteCellChangesAsync([Change(doc, "r2", 202603, At)], CancellationToken.None);
        await writer.WriteCellChangesAsync([Change(doc, "r3", 202603, At.AddMinutes(5))], CancellationToken.None);
        await writer.WriteCellChangesAsync([Change(doc, "r4", 202603, At.AddMinutes(5))], CancellationToken.None);
        await writer.WriteCellChangesAsync([Change(doc, "r5", 202603, At.AddMinutes(5))], CancellationToken.None);

        var reader = new AuditReader(db);
        var filter = new CellChangeFilter(At.AddMinutes(-1), At.AddHours(1), DocumentId: doc.DocumentId);

        // Гортаємо по 2 — межа сторінки падає всередину групи з однаковим ChangedAt.
        var keys = new List<string>();
        string? cursor = null;
        do
        {
            var page = await reader.ReadCellChangesAsync(filter, new CursorRequest(2, cursor), CancellationToken.None);
            keys.AddRange(page.Items.Select(c => c.RowKey));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(["r2", "r3", "r4", "r5", "r1"], keys);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "R-18")]
    public async Task Історія_комірки_лише_за_період_найновіші_першими_з_курсором()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);

        await using var db = builder.CreateContext();
        var writer = new AuditWriter(db);

        // 60 змін у старому періоді (мінута за мінутою від `At`) і 5 — у поточному, ПІЗНІШЕ за них.
        var older = Enumerable.Range(0, 60)
            .Select(i => Change(doc, "R4", 202601, At.AddMinutes(i), newValue: $"old-{i:D2}"))
            .ToList();
        var current = Enumerable.Range(0, 5)
            .Select(i => Change(doc, "R4", 202609, At.AddHours(2).AddMinutes(i), newValue: $"cur-{i}"))
            .ToList();
        await writer.WriteCellChangesAsync([.. older, .. current], CancellationToken.None);

        var reader = new AuditReader(db);
        var window = (From: At.AddMinutes(-1), To: At.AddDays(1));

        CellChangeFilter Cell(int? periodKey = null) => new(
            window.From, window.To, doc.DocumentId, RowKey: "R4", ColumnDefId: doc.ColumnDefIds[1], PeriodKey: periodKey);

        // Поточний період: рівно його 5 змін, найновіша перша — а не «50 найстаріших за все».
        var now = await reader.ReadCellChangesAsync(Cell(202609), new CursorRequest(50), CancellationToken.None);
        Assert.Equal(["cur-4", "cur-3", "cur-2", "cur-1", "cur-0"], now.Items.Select(c => c.NewValue));
        Assert.All(now.Items, c => Assert.Equal(202609, c.PeriodKey));
        Assert.Null(now.NextCursor);

        // Старий період, 60 змін: перша сторінка — 50 найновіших, друга за курсором — решта 10, без дублів.
        var first = await reader.ReadCellChangesAsync(Cell(202601), new CursorRequest(50), CancellationToken.None);
        Assert.Equal(50, first.Items.Count);
        Assert.Equal("old-59", first.Items[0].NewValue);
        Assert.Equal("old-10", first.Items[^1].NewValue);
        Assert.NotNull(first.NextCursor);

        var second = await reader.ReadCellChangesAsync(
            Cell(202601), new CursorRequest(50, first.NextCursor), CancellationToken.None);
        Assert.Equal(10, second.Items.Count);
        Assert.Equal("old-09", second.Items[0].NewValue);
        Assert.Equal("old-00", second.Items[^1].NewValue);
        Assert.Null(second.NextCursor);

        // Без періоду: адреса однієї комірки теж віддається від найновішої (AN-98), а не від найстарішої.
        var all = await reader.ReadCellChangesAsync(Cell(), new CursorRequest(3), CancellationToken.None);
        Assert.Equal(["cur-4", "cur-3", "cur-2"], all.Items.Select(c => c.NewValue));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "R-18")]
    public async Task Історія_комірки_курсор_не_губить_зміни_з_однаковим_моментом()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);

        await using var db = builder.CreateContext();

        // Одна правка діапазону пише всі свої зміни одним ChangedAt; Id у межах моменту — єдина відмінність.
        await new AuditWriter(db).WriteCellChangesAsync(
            [.. Enumerable.Range(0, 5).Select(i => Change(doc, "R4", 202609, At, newValue: $"same-{i}"))],
            CancellationToken.None);

        var reader = new AuditReader(db);
        var filter = new CellChangeFilter(
            At.AddMinutes(-1), At.AddHours(1), doc.DocumentId, RowKey: "R4", ColumnDefId: doc.ColumnDefIds[1], PeriodKey: 202609);

        var seen = new List<string?>();
        string? cursor = null;
        do
        {
            var page = await reader.ReadCellChangesAsync(filter, new CursorRequest(2, cursor), CancellationToken.None);
            seen.AddRange(page.Items.Select(c => c.NewValue));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(5, seen.Count);
        Assert.Equal(5, seen.Distinct().Count());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Курсор_пари_повертає_момент_і_ідентифікатор_а_старий_або_зіпсований_дає_початок()
    {
        var at = new DateTime(2026, 3, 4, 9, 15, 0, 123, DateTimeKind.Utc);

        var decoded = Cursor.DecodeAt(Cursor.Encode(at, 42));

        Assert.NotNull(decoded);
        Assert.Equal(at, decoded.Value.At);
        Assert.Equal(42, decoded.Value.Id);

        // Курсор лише з Id (форма до P1-07), сміття, порожнє й від'ємне — не позиція, а «з початку».
        Assert.Null(Cursor.DecodeAt(Cursor.Encode(42)));
        Assert.Null(Cursor.DecodeAt("!!не-base64!!"));
        Assert.Null(Cursor.DecodeAt(null));
        Assert.Null(Cursor.DecodeAt(Convert.ToBase64String("-1:5"u8.ToArray())));
        Assert.Null(Cursor.DecodeAt(Convert.ToBase64String("99999999999999999999:5"u8.ToArray())));
    }

    private static CellChangeRecord Change(TestDocument doc, string rowKey, int periodKey, DateTime at, string newValue = "2")
        => new(
            at,
            new CellAddress(new PeriodKey(periodKey), doc.RowIds[0], doc.ColumnDefIds[1]),
            doc.DocumentId,
            rowKey,
            OldValue: "1",
            NewValue: newValue,
            ChangedByUserId: 1,
            Origin: "UserEdit",
            IsLateEdit: false,
            CorrelationId: null);
}
