using System.Diagnostics;
using ClosedXML.Excel;
using Ecr.Adapters.Excel;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using NSubstitute;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Adapters.Tests.Excel;

/// <summary>
/// Рядок <c>XL</c> директиви №14 (§3.6): експорт читає базу ПАКЕТОМ, а стиль
/// кладе на діапазон, не на комірку.
/// </summary>
/// <remarks>
/// ⚠ Порти тут — заглушки, і це навмисно. Предмет виміру — СКІЛЬКИ РАЗІВ
/// експорт звертається до сховища і скільки коштує побудова книги на обсязі,
/// якого в базі тестового стенда не буває (91 × 500 × 60 = 2.73 млн комірок:
/// саму лише вставку такого документа не встигнути за прогін). Заглушка
/// відповідає на обидва питання точно: звернення до порту — це рівно те, що
/// перетворюється на запит, а вартість ClosedXML від походження даних не
/// залежить.
/// <para>
/// ⛔ Чого цей файл НЕ доводить: що пакетний метод порту виконується одним
/// запитом. Це доводить <c>ImportDiffBuilderPerformanceTests</c> на живій базі
/// для тих самих <c>GetRowIdsBatchAsync</c>/<c>ReadSlicesAsync</c>.
/// </para>
/// </remarks>
public sealed class ExcelExporterPerformanceTests(ITestOutputHelper output)
{
    private const int TemplateVersionId = 7;
    private const int PeriodKeyValue = 202601;
    private const long DocumentId = 500;

    /// <summary>
    /// ⛔ Храповик N+1: скільки б таблиць не було, звернень до сховища стільки
    /// само.
    /// </summary>
    /// <remarks>
    /// ⚠ Перевіряється ДВОМА обсягами, а не одним числом: константа в тесті
    /// збігалася б випадково, а рівність між 3 і 40 таблицями — ні. До зміни
    /// тут було 2×N (<c>GetRowIdsAsync</c> + <c>ReadSliceAsync</c> на таблицю):
    /// 6 проти 80.
    /// </remarks>
    [Theory]
    [InlineData(3)]
    [InlineData(40)]
    public async Task Звернень_до_сховища_не_більшає_від_кількості_таблиць(int tableCount)
    {
        var world = new FakeWorld(tableCount, rowCount: 2, columnCount: 2, filled: 1);

        await using var book = await world.Exporter()
            .ExportAsync(DocumentId, Options(), CancellationToken.None)
            .ConfigureAwait(true);

        output.WriteLine($"таблиць {tableCount}: звернень до сховища {world.Calls}, "
            + $"з них пакетних {world.BatchCalls} ({string.Join(", ", world.CallLog)})");

        // ⛔ Рівно ці чотири: перелік екземплярів, знімок структури, каталог
        // стилів і ДВА пакетні читання — разом п'ять, і жодне з них не
        // залежить від числа таблиць.
        Assert.Equal(5, world.Calls);
        Assert.Equal(2, world.BatchCalls);

        // Прямий доказ того, що саме зникло: поштучних викликів немає жодного.
        await world.Rows.DidNotReceiveWithAnyArgs()
            .GetRowIdsAsync(default, default, default).ConfigureAwait(true);
        await world.Cells.DidNotReceiveWithAnyArgs()
            .ReadSliceAsync(default, default).ConfigureAwait(true);
    }

    /// <summary>
    /// Книга лишається тією самою: порожня комірка обчисленої колонки —
    /// оформлена, формат числа на місці, значення там, де були.
    /// </summary>
    /// <remarks>
    /// ⛔ Це головна перевірка зміни, а не приємне доповнення. Найдешевший
    /// спосіб зробити експорт швидким — перестати оформлювати порожні комірки
    /// (стиль на СТОВПЕЦЬ аркуша замість діапазону коштує вчетверо менше), і
    /// саме він мовчки псує книгу: обчислена комірка, у якій ще нічого не
    /// ввели, перестає бути сірою, і користувач дізнається про заборону
    /// правки лише з відмови імпорту.
    /// </remarks>
    [Fact]
    public async Task Порожня_комірка_обчисленої_колонки_лишається_оформленою()
    {
        // filled: 2 — заповнені лише рядки R0 і R2, тобто рядки книги 3 і 5.
        var world = new FakeWorld(tableCount: 1, rowCount: 4, columnCount: 3, filled: 2);

        await using var book = await world.Exporter()
            .ExportAsync(DocumentId, Options(), CancellationToken.None)
            .ConfigureAwait(true);

        using var workbook = new XLWorkbook(book);
        var sheet = workbook.Worksheet("Sheet 0");

        // Розкладка блоку: 1 — назва таблиці, 2 — заголовки, 3..6 — рядки.
        // Колонка 3 — обчислена (див. FakeWorld).
        Assert.Equal(StyleMapper.CalculatedFill, sheet.Cell(3, 3).Style.Fill.BackgroundColor);
        Assert.Equal(StyleMapper.CalculatedFill, sheet.Cell(6, 3).Style.Fill.BackgroundColor);
        Assert.True(sheet.Cell(6, 3).Style.Protection.Locked);
        Assert.True(sheet.Cell(6, 3).IsEmpty(), "рядок 6 мав лишитися порожнім — інакше перевірка нічого не варта.");

        // Звичайна колонка сірою НЕ стає — інакше перевірка вище проходила б
        // на книзі, пофарбованій цілком.
        Assert.NotEqual(StyleMapper.CalculatedFill, sheet.Cell(6, 2).Style.Fill.BackgroundColor);

        // Формат числа з `Scale` — теж на всьому стовпчику даних, і на
        // порожній комірці також.
        Assert.Equal("0.00", sheet.Cell(6, 2).Style.NumberFormat.Format);

        // Заголовок оформлений, значення на місці.
        Assert.Equal(StyleMapper.HeaderFill, sheet.Cell(2, 2).Style.Fill.BackgroundColor);
        Assert.True(sheet.Cell(2, 2).Style.Font.Bold);
        Assert.Equal(1d, sheet.Cell(3, 2).Value.GetNumber());
        Assert.Equal(21d, sheet.Cell(5, 2).Value.GetNumber());

        // ⛔ Стиль колонки НЕ виходить за межі таблиці: рядок під блоком і
        // сам аркуш лишаються чистими. Саме це відрізняє діапазон від
        // стовпця аркуша.
        Assert.NotEqual(StyleMapper.CalculatedFill, sheet.Cell(20, 3).Style.Fill.BackgroundColor);
    }

    /// <summary>
    /// Ширина колонок міряється по РЯДКУ ЗАГОЛОВКІВ, а не по всьому аркушу.
    /// </summary>
    /// <remarks>
    /// ⚠ Тест навмисно тримає значення, ЗНАЧНО довше за заголовок: поки
    /// <c>AdjustToContents</c> дивився на весь аркуш, ширина йшла за ним.
    /// </remarks>
    [Fact]
    public async Task Ширина_колонки_йде_за_заголовком_а_не_за_значенням()
    {
        var world = new FakeWorld(
            tableCount: 1, rowCount: 2, columnCount: 1, filled: 1, text: new string('X', 200));

        await using var book = await world.Exporter()
            .ExportAsync(DocumentId, Options(), CancellationToken.None)
            .ConfigureAwait(true);

        using var workbook = new XLWorkbook(book);
        var sheet = workbook.Worksheet("Sheet 0");

        output.WriteLine($"ширина колонки 1: {sheet.Column(1).Width}");
        Assert.True(
            sheet.Column(1).Width < 60,
            $"ширина {sheet.Column(1).Width} — колонку розсунуло значення на 200 символів, "
            + "тобто AdjustToContents знову дивиться не лише на заголовок.");
    }

    /// <summary>
    /// Результат віддається ФАЙЛОМ, який зникає сам, а не другою копією книги
    /// в пам'яті.
    /// </summary>
    [Fact]
    public async Task Результат_це_файл_що_видаляє_себе_при_закритті()
    {
        var world = new FakeWorld(tableCount: 1, rowCount: 2, columnCount: 2, filled: 1);

        var book = await world.Exporter()
            .ExportAsync(DocumentId, Options(), CancellationToken.None)
            .ConfigureAwait(true);

        var file = Assert.IsAssignableFrom<FileStream>(book);
        var path = file.Name;
        Assert.True(File.Exists(path), "книга не лягла у файл.");
        Assert.True(book.Length > 0);

        await book.DisposeAsync().ConfigureAwait(true);
        Assert.False(File.Exists(path), $"тимчасовий файл {path} лишився після закриття потоку.");
    }

    /// <summary>
    /// Замір на обсязі директиви: 91 таблиця × 500 рядків × 60 колонок.
    /// </summary>
    /// <remarks>
    /// ⚠ Це ВИМІР, а не храповик: межа взята із запасом і сторожить порядок
    /// величини, а не секунди. Число з прогону — у виводі тесту; воно ж іде в
    /// опис PR.
    /// </remarks>
    [Fact]
    [Trait("Category", "Benchmark")]
    public async Task Замір_на_обсязі_директиви()
    {
        var world = new FakeWorld(tableCount: 91, rowCount: 500, columnCount: 60, filled: 10);

        // Розігрів: перший дотик до ClosedXML платить за JIT і метрики шрифту,
        // і без нього замір перебільшує вартість на секунду з гаком.
        await using (var warm = await new FakeWorld(1, 2, 2, 1).Exporter()
            .ExportAsync(DocumentId, Options(), CancellationToken.None).ConfigureAwait(true))
        {
            Assert.True(warm.Length > 0);
        }

        var before = GC.GetTotalAllocatedBytes();
        var sw = Stopwatch.StartNew();

        await using var book = await world.Exporter()
            .ExportAsync(DocumentId, Options(), CancellationToken.None)
            .ConfigureAwait(true);

        sw.Stop();

        output.WriteLine(
            $"91 × 500 × 60: {sw.ElapsedMilliseconds} мс, книга {book.Length / 1048576} МБ, "
            + $"виділено {(GC.GetTotalAllocatedBytes() - before) / 1048576} МБ, "
            + $"звернень до сховища {world.Calls}");

        Assert.Equal(5, world.Calls);
    }

    private static ExcelExportOptions Options()
        => new(IncludeFormulas: false, IncludeStyles: true, Language: "en", PeriodKey: PeriodKeyValue);

    /// <summary>Документ потрібної форми і порти, які його віддають.</summary>
    private sealed class FakeWorld
    {
        private readonly List<TableInstanceRef> _instances = [];
        private readonly Dictionary<long, IReadOnlyDictionary<string, long>> _rowIds = [];
        private readonly Dictionary<long, IReadOnlyList<CellRecord>> _slices = [];

        public ICellStore Cells { get; } = Substitute.For<ICellStore>();

        public IRowStore Rows { get; } = Substitute.For<IRowStore>();

        /// <summary>Скільки разів експорт звернувся до сховища.</summary>
        public int Calls { get; private set; }

        /// <summary>Скільки з них пакетних.</summary>
        public int BatchCalls { get; private set; }

        public List<string> CallLog { get; } = [];

        /// <summary>Значення першої колонки; довгим воно перевіряє ширину.</summary>
        private readonly string _text;

        public FakeWorld(int tableCount, int rowCount, int columnCount, int filled, string text = "v")
        {
            _text = text;
            var sheets = new List<SheetDef>();
            var columnsById = new Dictionary<int, ColumnDef>();
            var rowsByKey = new Dictionary<(int, string), RowDef>();
            var period = new PeriodKey(PeriodKeyValue);

            // Таблиці розкладено по аркушах по десять: саме так і виглядає
            // документ на 91 таблицю, і саме тому ширину колонок доводиться
            // зводити по КІЛЬКОХ рядках заголовків на аркуші.
            for (var t = 0; t < tableCount; t++)
            {
                if (t % 10 == 0)
                {
                    sheets.Add(new SheetDef(
                        TemplateVersionId, EcrCode.Create($"S{t}"), Text($"Sheet {t / 10}"), t / 10));
                    SetId(sheets[^1], (t / 10) + 1);
                }

                var tableId = t + 1;
                var table = new TableDef(
                    sheets[^1].Id, EcrCode.Create($"T{t}"), Text($"Table {t}"), t,
                    TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
                SetId(table, tableId);

                for (var c = 0; c < columnCount; c++)
                {
                    // Кожна третя колонка — обчислена: саме вона має лишитися
                    // сірою і в порожніх комірках.
                    var type = c switch
                    {
                        0 => CellDataType.String,
                        2 => CellDataType.Calculated,
                        _ => CellDataType.Decimal,
                    };

                    var column = new ColumnDef(
                        tableId, EcrCode.Create($"C{c}"), Text($"Col {c}"), c, type);
                    column.SetNumericFormat(precision: 18, scale: 2);

                    var columnId = (tableId * 1000) + c;
                    SetId(column, columnId);
                    table.AddColumn(column);
                    columnsById[columnId] = column;
                }

                for (var r = 0; r < rowCount; r++)
                {
                    var rowDef = new RowDef(
                        tableId, RowKey.Create($"R{r}"), r, Text($"Row {r}"), RowKind.Item);
                    table.AddRow(rowDef);
                    rowsByKey[(tableId, $"R{r}")] = rowDef;
                }

                sheets[^1].AddTable(table);

                var instanceId = 10_000L + tableId;
                _instances.Add(new TableInstanceRef(
                    instanceId, DocumentId, tableId, TemplateVersionId, PeriodKeyValue));

                var ids = new Dictionary<string, long>(StringComparer.Ordinal);
                var cells = new List<CellRecord>();

                for (var r = 0; r < rowCount; r++)
                {
                    var rowId = (instanceId * 1000) + r;
                    ids[$"R{r}"] = rowId;

                    // Заповнений лише кожен `filled`-й рядок: зріз порожніх
                    // комірок не повертає (ФВ-3.8), і документ, заповнений
                    // цілком, міряв би не те, що буває насправді.
                    if (r % filled != 0)
                    {
                        continue;
                    }

                    cells.Add(new CellRecord(
                        new CellAddress(period, rowId, tableId * 1000),
                        tableId,
                        new CellValueData { ValueString = _text }));

                    for (var c = 1; c < columnCount; c++)
                    {
                        cells.Add(new CellRecord(
                            new CellAddress(period, rowId, (tableId * 1000) + c),
                            tableId,
                            new CellValueData { ValueNumeric = (r * 10) + c }));
                    }
                }

                _rowIds[instanceId] = ids;
                _slices[instanceId] = cells;
            }

            Snapshot = new TemplateVersionSnapshot(
                TemplateVersionId, 1, sheets, columnsById, rowsByKey);

            Rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
                .Returns(_ => Record("GetTableInstances", batch: false, (IReadOnlyList<TableInstanceRef>)_instances));

            Rows.GetRowIdsBatchAsync(
                    Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
                .Returns(_ => Record(
                    "GetRowIdsBatch", batch: true,
                    (IReadOnlyDictionary<long, IReadOnlyDictionary<string, long>>)_rowIds));

            Cells.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
                .Returns(_ => Record(
                    "ReadSlices", batch: true,
                    (IReadOnlyDictionary<long, IReadOnlyList<CellRecord>>)_slices));

            // ⛔ Поштучні методи НЕ налаштовані навмисно: якби експорт до них
            // повернувся, заглушка віддала б `null`, і тест упав би з
            // NullReference — гучно, а не тихо повільніше.
        }

        public TemplateVersionSnapshot Snapshot { get; }

        public ExcelExporter Exporter()
        {
            var metadata = Substitute.For<IMetadataCache>();
            metadata.GetAsync(TemplateVersionId, Arg.Any<CancellationToken>())
                .Returns(_ => Record("Metadata", batch: false, Snapshot));

            var styles = Substitute.For<IStyleCatalog>();
            styles.GetAsync(TemplateVersionId, Arg.Any<CancellationToken>())
                .Returns(_ => Record(
                    "Styles", batch: false, (IReadOnlyDictionary<int, StyleDef>)new Dictionary<int, StyleDef>()));

            var registries = Substitute.For<IRegistryStore>();

            return new ExcelExporter(
                Cells, Rows, metadata, styles, registries, new StyleMapper(), new FormulaTranslator());
        }

        private T Record<T>(string name, bool batch, T value)
        {
            Calls++;
            CallLog.Add(name);
            if (batch)
            {
                BatchCalls++;
            }

            return value;
        }

        private static LocalizedText Text(string value)
            => new(new Dictionary<string, string> { ["en"] = value });

        private static void SetId<T>(T entity, int id)
            where T : Entity<int>
            => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);
    }
}
