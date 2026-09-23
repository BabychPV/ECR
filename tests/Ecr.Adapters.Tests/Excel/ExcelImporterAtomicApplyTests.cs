// tests/Ecr.Adapters.Tests/Excel/ExcelImporterAtomicApplyTests.cs
using System.Text.Json;
using Ecr.Adapters.Excel;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Errors;
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
/// `DAT-05` / `D14-04` — імпорт застосовується «все або нічого», і перерахунок
/// ставиться ОДНІЄЮ задачею після коміту.
/// </summary>
/// <remarks>
/// ⛔ До цієї правки <c>ApplyAsync</c> кликав <c>PatchCellsHandler</c> на кожну
/// таблицю окремо — кожну зі своєю транзакцією і своєю задачею. Конфлікт на
/// другій із трьох означав: перша вже закомічена, задача на неї вже в черзі,
/// клієнт отримав помилку без переліку застосованого.
///
/// ⚠ Цей набір — ШВИДКЕ опудало на ПОРЯДОК викликів (одна транзакція; задача
/// після неї; нуль задач при відмові). Що відкат справді лишає в базі нуль
/// змін — доводить <c>ImportAtomicityScenarios</c> проти реального SQL Server:
/// транзакцію тут підмінено, і робити з підміни висновок про СУБД було б
/// самообманом.
/// </remarks>
public sealed class ExcelImporterAtomicApplyTests
{
    private const long DocumentId = 700;
    private const int Period = 202601;
    private const int TableDefId = 3;
    private const int TemplateVersionId = 2;
    private const int VolumeColumnId = 11;
    private const string Token = "tok";

    /// <summary>Три таблиці книги; друга — та, що конфліктує.</summary>
    private static readonly long[] Instances = [501L, 502L, 503L];

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IImportPreviewStore _previews = Substitute.For<IImportPreviewStore>();
    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();
    private readonly IMethodologyStore _methodologies = Substitute.For<IMethodologyStore>();
    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();

    /// <summary>Шапка документа — тести цього файлу її не читають.</summary>
    private readonly IDocumentHeaderStore _headers = CreateHeaderStore();

    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IDocumentStore _documents = Substitute.For<IDocumentStore>();
    private readonly IBackgroundJobScheduler _jobs = Substitute.For<IBackgroundJobScheduler>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();

    /// <summary>Журнал подій у порядку, у якому вони сталися.</summary>
    private readonly List<string> _trace = [];

    /// <summary>Глибина вкладеності «транзакції» саба.</summary>
    private int _depth;

    public ExcelImporterAtomicApplyTests()
    {
        _clock.UtcNow.Returns(new DateTime(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc));
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(Profile());
        _metadata.GetAsync(TemplateVersionId, Arg.Any<CancellationToken>()).Returns(Snapshot());
        _methodologies.GetMethodologyIdsBoundToTableAsync(TableDefId, Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult<IReadOnlyList<int>>([]));
        _registries.FindExistingEntryIdsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
                   .Returns(call => call.ArgAt<IReadOnlyCollection<long>>(0).ToHashSet());

        for (var i = 0; i < Instances.Length; i++)
        {
            var instance = Instances[i];
            var rowId = 1001L + i;

            _rows.ResolveTableInstanceAsync(instance, Arg.Any<CancellationToken>())
                 .Returns(new TableInstanceRef(instance, DocumentId, TableDefId, TemplateVersionId, Period));
            _rows.GetRowIdsAsync(instance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
                 .Returns(new Dictionary<string, long> { ["R1"] = rowId });
            _rows.GetRowVersionsAsync(instance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
                 .Returns(new Dictionary<string, string> { ["R1"] = "0xAA" });
            _access.CanEditSliceAsync(Arg.Any<AccessProfile>(), instance, Arg.Any<CancellationToken>())
                   .Returns(new Dictionary<CellAddress, EditDecision>
                   {
                       [new CellAddress(PeriodKey.Parse(Period), rowId, VolumeColumnId)] = EditDecision.Allow(),
                   });
        }

        // ⚠ Підмінена транзакція ВИКОНУЄ тіло і записує в журнал свої межі:
        // саме вони роблять твердження «задача — ПІСЛЯ коміту» перевірним.
        //
        // ⛔ І вона повторює головну властивість справжньої
        // <c>UnitOfWork.ExecuteInTransactionAsync</c>: коли транзакція вже
        // відкрита ЗОВНІ, вкладений виклик просто виконує тіло
        // (<c>db.Database.CurrentTransaction is not null</c>) — SQL Server не
        // підтримує вкладених транзакцій. Без цього фрагмента саб рахував би
        // чотири окремі транзакції там, де їх одна, і тест доводив би
        // властивість саба, а не продукту.
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var body = call.ArgAt<Func<CancellationToken, Task>>(0);
                var innerCt = call.ArgAt<CancellationToken>(1);

                if (_depth > 0)
                {
                    _depth++;
                    try
                    {
                        await body(innerCt);
                    }
                    finally
                    {
                        _depth--;
                    }

                    return;
                }

                _depth++;
                _trace.Add("tx:open");
                try
                {
                    await body(innerCt);
                }
                catch
                {
                    _trace.Add("tx:rollback");
                    throw;
                }
                finally
                {
                    _depth--;
                }

                _trace.Add("tx:commit");
            });

        _cells.When(c => c.ApplyAsync(Arg.Any<CellChangeSet>(), Arg.Any<CancellationToken>()))
              .Do(call => _trace.Add($"write:{call.ArgAt<CellChangeSet>(0).TableInstanceId}"));

        _jobs.When(j => j.EnqueueAsync<IFormulaRecalculationJob>(Arg.Any<object>(), Arg.Any<CancellationToken>()))
             .Do(_ => _trace.Add("enqueue"));

        _previews.FindAsync(Token, Arg.Any<CancellationToken>()).Returns(Plan());
    }

    private static AccessProfile Profile() => new()
    {
        CacheKey = "p1", UserId = 9, SecurityStamp = "s",
        Permissions = new HashSet<string>(), Grants = new Dictionary<string, GrantLevel>(),
        Denies = new HashSet<string>(), RoleIds = new HashSet<int>(),
    };

    private static TemplateVersionSnapshot Snapshot()
    {
        var column = new ColumnDef(
            TableDefId, EcrCode.Create("Volume"), Name("Volume"), 1, CellDataType.Decimal);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(column, VolumeColumnId);

        var sheet = new SheetDef(TemplateVersionId, EcrCode.Create("Water"), Name("Water"), 1);
        var table = new TableDef(
            sheetDefId: 1, EcrCode.Create("Main"), Name("Main"), 1,
            TableLayoutKind.MonthsInColumns, TableRowMode.Dynamic);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(table, TableDefId);
        table.AddColumn(column);
        sheet.AddTable(table);

        return new TemplateVersionSnapshot(
            TemplateVersionId, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: new Dictionary<int, ColumnDef> { [VolumeColumnId] = column },
            RowsByKey: new Dictionary<(int, string), RowDef>());
    }

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>Збережений перегляд: три таблиці, у кожній одна зміна.</summary>
    private static string Plan()
    {
        var tables = Instances
            .Select((instance, i) => new TableDiff(
                instance,
                Period,
                [new ImportChange("R1", "Volume", null, 10m * (i + 1))],
                [],
                new Dictionary<string, string> { ["R1"] = "0xAA" }))
            .ToList();

        return JsonSerializer.Serialize(new ImportPlan(DocumentId, Period, tables), Options);
    }

    private ExcelImporter Importer()
        => new(
            _metadata, _registries, _access, _user, _previews,
            new PatchCellsHandler(
                _cells, _rows, _documents, _periods, _metadata, _access,
                new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
                _methodologies, _registries, _headers, _audit, Substitute.For<IAuditReader>(),
                _jobs, _uow, _user, _clock),
            new ImportDiffBuilder(), _cells, _rows, _uow, _jobs);

    private static IDocumentHeaderStore CreateHeaderStore()
    {
        var store = Substitute.For<IDocumentHeaderStore>();
        store.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ExpressionValue>());
        return store;
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "DAT-05")]
    public async Task Уся_книга_іде_однією_транзакцією_і_однією_задачею_після_коміту()
    {
        await Importer().ApplyAsync(DocumentId, Token, CancellationToken.None);

        // ⛔ ОДНА транзакція на книгу, а не одна на таблицю. Без цього
        // твердження решта перевірок лишилася б зеленою і з трьома окремими
        // транзакціями — тобто рівно з тим дефектом, який виправляється.
        // (Рахуються саме ВІДКРИТТЯ: вкладені виклики обробника транзакції не
        // відкривають — ні тут, ні в справжній `UnitOfWork`.)
        Assert.Single(_trace, e => string.Equals(e, "tx:open", StringComparison.Ordinal));

        // ⛔ ОДНА задача перерахунку на документ, а не три.
        await _jobs.Received(1).EnqueueAsync<IFormulaRecalculationJob>(
            Arg.Any<object>(), Arg.Any<CancellationToken>());

        // ⚠ І саме ПІСЛЯ коміту: поставлена всередині, вона стартувала б у
        // воркері раніше, ніж записане стане видимим під RCSI.
        Assert.Equal(
            ["tx:open", "write:501", "write:502", "write:503", "tx:commit", "enqueue"],
            _trace);

        // ⛔ Насіння — з УСІХ трьох таблиць. Одна задача з насінням лише
        // останньої таблиці виглядала б так само «одна», а перерахунок двох
        // інших не стався б ніколи.
        var payload = _jobs.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IBackgroundJobScheduler.EnqueueAsync))
            .GetArguments()[0];

        var json = JsonSerializer.Serialize(payload, Options);
        Assert.Contains("\"rowId\":1001", json, StringComparison.Ordinal);
        Assert.Contains("\"rowId\":1002", json, StringComparison.Ordinal);
        Assert.Contains("\"rowId\":1003", json, StringComparison.Ordinal);

        await _previews.Received(1).RemoveAsync(Token, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "DAT-05")]
    public async Task Конфлікт_на_другій_таблиці_не_лишає_ні_задач_ні_застосованої_першої()
    {
        // Конфлікт приходить звідти, звідки приходить насправді: сховище
        // відхиляє батч, бо версія рядка розійшлася між переглядом і записом.
        _cells.When(c => c.ApplyAsync(
                  Arg.Is<CellChangeSet>(s => s.TableInstanceId == 502L), Arg.Any<CancellationToken>()))
              .Do(_ => throw new ConcurrencyConflictException(
                  "ECR-CELL-0409", "Дані змінилися після того, як ви їх прочитали.",
                  new Dictionary<string, object?> { ["messageKey"] = "err.ECR-CELL-0409.batchStale" }));

        var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Importer().ApplyAsync(DocumentId, Token, CancellationToken.None));

        // ⛔ Відповідь НАЗИВАЄ таблицю-винуватця. «Усе або нічого» без адреси
        // відмови гірше за часткове застосування: користувач бачить, що не
        // змінилося нічого, і не має жодного способу дізнатися, де шукати.
        Assert.NotNull(error.Details);
        Assert.Equal("502", error.Details!["tableInstanceId"]);

        // ⚠ Код і ключ каталогу збереглися — переклад додає поле, а не
        // підміняє відмову іншою.
        Assert.Equal("ECR-CELL-0409", error.ErrorCode);
        Assert.Equal("err.ECR-CELL-0409.batchStale", error.Details["messageKey"]);

        // ⛔ Нуль задач перерахунку: жодної на вже записану першу таблицю.
        await _jobs.DidNotReceive().EnqueueAsync<IFormulaRecalculationJob>(
            Arg.Any<object>(), Arg.Any<CancellationToken>());

        // ⛔ Відмова стається ВСЕРЕДИНІ транзакції — саме це й відкочує запис
        // першої таблиці. «tx:commit» у журналі означав би, що перша таблиця
        // закомічена, тобто повернення дефекту.
        Assert.Equal(["tx:open", "write:501", "write:502", "tx:rollback"], _trace);

        // Перегляд не прибирається: користувач має змогу застосувати його ще
        // раз, а не будувати наново з файлу, якого може вже не бути під рукою.
        await _previews.DidNotReceive().RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
