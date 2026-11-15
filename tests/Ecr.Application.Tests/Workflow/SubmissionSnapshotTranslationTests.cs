using Ecr.Application.Ports;
using Ecr.Application.Workflow;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Workflow;

/// <summary>
/// B72 (D1): переклад зрізу подання з таблиці, перейменованої переносом, за родоводом колонок — лише ОДНОЗНАЧНІ пари.
/// </summary>
/// <remarks>
/// Версія зрізу (1): аркуш S, таблиця OLD з колонками a(101), b(102). Версія документа (2): аркуш S, таблиця NEW з
/// колонками z(201), w(202) — за кодами відповідників немає. Родовід (<see cref="IColumnPathMapper.LineageAsync"/>) —
/// фейк: так виглядають пари <c>cfg.ColumnRename</c>, які глобальні (без проєкту) і можуть прийти з переносів ІНШИХ
/// проєктів. Мутації: прибрати умову «у кандидата одне джерело» → червоніє «два_джерела»; прибрати «джерело в одного
/// кандидата» → червоніє «одне_джерело_двох_кандидатів».
/// </remarks>
public sealed class SubmissionSnapshotTranslationTests
{
    private const int A = 101;
    private const int B = 102;
    private const int Z = 201;
    private const int W = 202;

    private readonly TemplateVersionSnapshot _from = Version(1, "OLD", (A, "a"), (B, "b"));
    private readonly TemplateVersionSnapshot _to = Version(2, "NEW", (Z, "z"), (W, "w"));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "B72")]
    public async Task Однозначні_пари_перекладаються_і_ланцюжок_через_проміжну_версію_теж()
    {
        // 301 — Id проміжної версії (ланцюжок v1 → v3 → v2): у версії зрізу його немає, на однозначність не впливає.
        var paths = Mapper(new() { [Z] = [Z, 301, A], [W] = [W, B] });

        var cells = await SubmissionSnapshotTranslation.CellsAsync([Cell(A, "1"), Cell(B, "2")], _from, _to, paths, null, default);

        Assert.Equal([Z, W], cells.Select(c => c.Column));
        Assert.Equal(["1", "2"], cells.Select(c => c.Value));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "B72")]
    public async Task Два_джерела_в_одну_колонку_не_перекладаються_навіть_якщо_в_зрізі_лише_одне()
    {
        // Проєкт A переніс a → z, проєкт B (ті самі версії) — b → z.
        var paths = Mapper(new() { [Z] = [Z, A, B], [W] = [W] });

        var both = await SubmissionSnapshotTranslation.CellsAsync([Cell(A, "1"), Cell(B, "2")], _from, _to, paths, null, default);
        var single = await SubmissionSnapshotTranslation.CellsAsync([Cell(A, "1")], _from, _to, paths, null, default);

        // Без дубля адреси (r, z) і без значення відкинутої колонки під чужим кодом — як до B72.
        Assert.Equal([A, B], both.Select(c => c.Column));
        Assert.Equal([A], single.Select(c => c.Column));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "B72")]
    public async Task Одне_джерело_двох_кандидатів_не_перекладається()
    {
        var paths = Mapper(new() { [Z] = [Z, A], [W] = [W, A] });

        var cells = await SubmissionSnapshotTranslation.CellsAsync([Cell(A, "1")], _from, _to, paths, null, default);

        Assert.Equal([A], cells.Select(c => c.Column));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "B72")]
    public async Task Усе_знайдено_за_кодами_родовід_не_питається()
    {
        var paths = Substitute.For<IColumnPathMapper>();
        var same = Version(3, "OLD", (301, "a"), (302, "b"));

        var cells = await SubmissionSnapshotTranslation.CellsAsync([Cell(A, "1")], _from, same, paths, null, default);

        Assert.Equal([301], cells.Select(c => c.Column));
        await paths.DidNotReceiveWithAnyArgs().LineageAsync(default!, default, default);
    }

    /// <remarks>
    /// M10: у версії зрізу є ВИЛУЧЕНА колонка NEW.z (150) — тим самим шляхом, що й кандидат z (201); за кодами вона
    /// переходить у z (як до B72). Родовід z веде ще й у перейменовану a — вона не має права зайняти ту саму z, інакше
    /// дві комірки зрізу на одній адресі. Мутація: прибрати відсів кандидатів, зайнятих за кодами → червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "B72")]
    public async Task Кандидат_зайнятий_вилученою_колонкою_за_кодами_не_приймає_джерело_родоводу()
    {
        const int DeletedZ = 150;
        var from = Version(4, "OLD", (A, "a"));
        var stale = new TableDef(from.Sheets[0].Id, EcrCode.Create("NEW"), Text("NEW"), 2,
                                 TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        SetId(stale, 42);
        var z = new ColumnDef(stale.Id, EcrCode.Create("z"), Text("z"), 1, CellDataType.String);
        SetId(z, DeletedZ);
        z.SoftDelete(1, new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        stale.AddColumn(z);
        from.Sheets[0].AddTable(stale);

        var paths = Mapper(new() { [Z] = [Z, A], [W] = [W] });

        var cells = await SubmissionSnapshotTranslation.CellsAsync(
            [Cell(A, "1"), Cell(DeletedZ, "9")], from, _to, paths, null, default);

        Assert.Equal([A, Z], cells.Select(c => c.Column));
        Assert.Equal(cells.Count, cells.Select(c => (c.Row, c.Column)).Distinct().Count());
    }

    private static SubmissionPayloadCell Cell(int column, string value) => new(1, column, value, null);

    private static IColumnPathMapper Mapper(Dictionary<int, IReadOnlyList<int>> lineage)
    {
        var paths = Substitute.For<IColumnPathMapper>();
        paths.LineageAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(lineage.ToDictionary(p => p.Key, p => p.Value) as IReadOnlyDictionary<int, IReadOnlyList<int>>);
        return paths;
    }

    private static TemplateVersionSnapshot Version(int id, string tableCode, params (int Id, string Code)[] columns)
    {
        var sheet = new SheetDef(id, EcrCode.Create("S"), Text("S"), 1);
        SetId(sheet, id * 10);
        var table = new TableDef(sheet.Id, EcrCode.Create(tableCode), Text(tableCode), 1,
                                 TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        SetId(table, id * 10 + 1);
        var byId = new Dictionary<int, ColumnDef>();
        var ordinal = 1;
        foreach (var (columnId, code) in columns)
        {
            var column = new ColumnDef(table.Id, EcrCode.Create(code), Text(code), ordinal++, CellDataType.String);
            SetId(column, columnId);
            table.AddColumn(column);
            byId[columnId] = column;
        }

        sheet.AddTable(table);
        return new TemplateVersionSnapshot(id, 0, [sheet], byId, new Dictionary<(int, string), RowDef>());
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id) where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);
}
