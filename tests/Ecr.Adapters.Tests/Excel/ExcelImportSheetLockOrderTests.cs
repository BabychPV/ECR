// tests/Ecr.Adapters.Tests/Excel/ExcelImportSheetLockOrderTests.cs
using System.Text.Json;
using Ecr.Adapters.Excel;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Adapters.Tests.Excel;

/// <summary>
/// Імпорт книги бере спільні блокування аркушів у СТАБІЛЬНОМУ порядку ключа
/// «документ × аркуш × період» (<c>SheetDefId</c> за зростанням) — тому самому,
/// що й <c>RecalculationService</c>, — а не в порядку таблиць книги.
/// </summary>
/// <remarks>
/// ⛔ Що було. Кожен <c>PatchCellsHandler</c> брав блокування свого аркуша сам,
/// тобто в порядку таблиць КНИГИ. Черга блокувань SQL Server FIFO: спільний
/// запит стає за винятковим (подання), що вже чекає. Імпорт (B, потім A) проти
/// перерахунку (A, потім B) з двома поданнями в черзі давав цикл.
///
/// ⚠ Книга тут навмисно в ЗВОРОТНОМУ порядку: перша таблиця — на аркуші 20,
/// друга — на аркуші 10. Шпигун — ОДИН <see cref="ISheetEditGate"/> на імпортер і
/// обробник правки, як у DI-скоупі.
/// </remarks>
public sealed class ExcelImportSheetLockOrderTests
{
    private const long DocumentId = 700;
    private const int Period = 202601;
    private const int TemplateVersionId = 2;
    private const string Token = "tok";

    /// <summary>Таблиці книги в порядку книги: (екземпляр, таблиця, колонка, аркуш, рядок).</summary>
    private static readonly (long Instance, int TableDefId, int ColumnId, int SheetDefId, long RowId)[] Book =
    [
        (601L, 3, 11, 20, 2001L),
        (602L, 4, 12, 10, 2002L),
    ];

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IImportPreviewStore _previews = Substitute.For<IImportPreviewStore>();
    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly IMethodologyStore _methodologies = Substitute.For<IMethodologyStore>();
    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IDocumentHeaderStore _headers = Substitute.For<IDocumentHeaderStore>();
    private readonly IBackgroundJobScheduler _jobs = Substitute.For<IBackgroundJobScheduler>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly ISheetEditGate _gate = Substitute.For<ISheetEditGate>();

    /// <summary>Журнал: блокування аркушів і записи таблиць у порядку, у якому вони сталися.</summary>
    private readonly List<string> _trace = [];

    public ExcelImportSheetLockOrderTests()
    {
        _clock.UtcNow.Returns(new DateTime(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc));
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(Profile());
        _metadata.GetAsync(TemplateVersionId, Arg.Any<CancellationToken>()).Returns(Snapshot());
        _methodologies.GetMethodologyIdsBoundToTableAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult<IReadOnlyList<int>>([]));
        _registries.FindExistingEntryIdsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
                   .Returns(call => call.ArgAt<IReadOnlyCollection<long>>(0).ToHashSet());
        _headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
                .Returns(new Dictionary<string, ExpressionValue>());

        _rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(Book
                 .Select(t => new TableInstanceRef(t.Instance, DocumentId, t.TableDefId, TemplateVersionId, Period))
                 .ToList());

        foreach (var table in Book)
        {
            _rows.ResolveTableInstanceAsync(table.Instance, Arg.Any<CancellationToken>())
                 .Returns(new TableInstanceRef(table.Instance, DocumentId, table.TableDefId, TemplateVersionId, Period));
            _rows.GetRowIdsAsync(table.Instance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
                 .Returns(new Dictionary<string, long> { ["R1"] = table.RowId });
            _rows.GetRowVersionsAsync(table.Instance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
                 .Returns(new Dictionary<string, string> { ["R1"] = "0xAA" });
            _access.CanEditSliceAsync(Arg.Any<AccessProfile>(), table.Instance, Arg.Any<CancellationToken>())
                   .Returns(new Dictionary<CellAddress, EditDecision>
                   {
                       [new CellAddress(PeriodKey.Parse(Period), table.RowId, table.ColumnId)] = EditDecision.Allow(),
                   });
        }

        // Підмінена транзакція виконує тіло; вкладена — теж (як справжня UnitOfWork).
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));

        _gate.EnterEditAsync(DocumentId, Arg.Any<int>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(call =>
             {
                 _trace.Add($"lock:{call.ArgAt<int>(1)}");
                 return Task.FromResult(DocumentStatus.Draft);
             });

        _cells.When(c => c.ApplyAsync(Arg.Any<CellChangeSet>(), Arg.Any<CancellationToken>()))
              .Do(call => _trace.Add($"write:{call.ArgAt<CellChangeSet>(0).TableInstanceId}"));

        _previews.FindAsync(Token, Arg.Any<CancellationToken>()).Returns(Plan());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "DAT-05")]
    public async Task Блокування_аркушів_книги_беруться_за_зростанням_ключа_а_не_в_порядку_книги()
    {
        await Importer().ApplyAsync(DocumentId, Token, CancellationToken.None);

        // ⛔ ПЕРШЕ взяття кожного аркуша — у порядку ключа (10, потім 20), і обидва
        // ДО першого запису. Повторні взяття тим самим власником (обробник
        // правки під тією самою транзакцією) порядку не змінюють: вони видаються
        // одразу.
        var firstLocks = _trace
            .Where(e => e.StartsWith("lock:", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["lock:10", "lock:20"], firstLocks);

        var firstWrite = _trace.FindIndex(e => e.StartsWith("write:", StringComparison.Ordinal));
        Assert.True(firstWrite > 0, $"Запису не було: {string.Join(", ", _trace)}");
        Assert.True(
            _trace.IndexOf("lock:20") < firstWrite,
            $"Блокування аркуша 20 взято вже після запису: {string.Join(", ", _trace)}");

        // Записи — у порядку книги, як і були: змінився лише порядок блокувань.
        Assert.Equal(
            ["write:601", "write:602"],
            _trace.Where(e => e.StartsWith("write:", StringComparison.Ordinal)).ToList());
    }

    private ExcelImporter Importer()
        => new(
            _metadata, _registries, _access, _user, _previews,
            new PatchCellsHandler(
                _cells, _rows, Substitute.For<IDocumentStore>(), Substitute.For<IPeriodStore>(), _metadata, _access,
                new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
                _methodologies, _registries, _headers, Substitute.For<IAuditWriter>(), Substitute.For<IAuditReader>(),
                _jobs, _uow, _user, _clock, _gate),
            new ImportDiffBuilder(), _cells, _rows, _uow, _jobs, _gate);

    private static AccessProfile Profile() => new()
    {
        CacheKey = "p1", UserId = 9, SecurityStamp = "s",
        Permissions = new HashSet<string>(), Grants = new Dictionary<string, GrantLevel>(),
        Denies = new HashSet<string>(), RoleIds = new HashSet<int>(),
    };

    private static TemplateVersionSnapshot Snapshot()
    {
        var sheets = new List<SheetDef>();
        var columns = new Dictionary<int, ColumnDef>();

        foreach (var entry in Book.Select((t, i) => (t, i)))
        {
            var (table, i) = entry;
            var sheet = new SheetDef(TemplateVersionId, EcrCode.Create($"S{table.SheetDefId}"), Name("Sheet"), i + 1);
            typeof(Entity<int>).GetProperty("Id")!.SetValue(sheet, table.SheetDefId);

            var def = new TableDef(
                table.SheetDefId, EcrCode.Create($"T{table.TableDefId}"), Name("Table"), 1,
                TableLayoutKind.MonthsInColumns, TableRowMode.Dynamic);
            typeof(Entity<int>).GetProperty("Id")!.SetValue(def, table.TableDefId);

            var column = new ColumnDef(
                table.TableDefId, EcrCode.Create("Volume"), Name("Volume"), 1, CellDataType.Decimal);
            typeof(Entity<int>).GetProperty("Id")!.SetValue(column, table.ColumnId);

            def.AddColumn(column);
            sheet.AddTable(def);
            sheets.Add(sheet);
            columns[table.ColumnId] = column;
        }

        return new TemplateVersionSnapshot(
            TemplateVersionId, PresentationRevision: 0, Sheets: sheets,
            ColumnsById: columns,
            RowsByKey: new Dictionary<(int, string), RowDef>());
    }

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>Збережений перегляд: дві таблиці, у кожній одна зміна, у порядку книги.</summary>
    private static string Plan()
    {
        var tables = Book
            .Select((t, i) => new TableDiff(
                t.Instance,
                Period,
                [new ImportChange("R1", "Volume", null, 10m * (i + 1))],
                [],
                new Dictionary<string, string> { ["R1"] = "0xAA" }))
            .ToList();

        return JsonSerializer.Serialize(new ImportPlan(DocumentId, Period, tables), Options);
    }
}
