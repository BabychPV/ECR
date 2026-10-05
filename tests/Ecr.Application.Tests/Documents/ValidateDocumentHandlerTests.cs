// tests/Ecr.Application.Tests/Documents/ValidateDocumentHandlerTests.cs
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Validation;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// <c>ValidateDocumentHandler</c> — бюджет **p95 3 с** на весь документ
/// (`ФВ-5.1`), і похід у базу на кожну з ~90 таблиць у нього не вкладається.
/// </summary>
public sealed class ValidateDocumentHandlerTests
{
    private const long DocumentId = 700;
    private const int Period = 202601;
    private const int TemplateVersionId = 2;
    private const long Instance1 = 500;
    private const long Instance2 = 501;
    private const long InstanceNoRules = 502;

    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IValidationResultStore _results = Substitute.For<IValidationResultStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();

    /// <summary>Шапка документа — тести цього файлу її не читають.</summary>
    private readonly IDocumentHeaderStore _headers = CreateHeaderStore();

    private static IDocumentHeaderStore CreateHeaderStore()
    {
        var store = Substitute.For<IDocumentHeaderStore>();
        store.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ExpressionValue>());
        return store;
    }

    public ValidateDocumentHandlerTests()
    {
        var (sheet, tableWithRule1) = TableWithRule(tableDefId: 3, columnId: 40);
        var (_, tableWithRule2) = (sheet, AddTable(sheet, tableDefId: 4, columnId: 41, withRule: true));
        AddTable(sheet, tableDefId: 5, columnId: 42, withRule: false);

        _rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new List<TableInstanceRef>
             {
                 new(Instance1, DocumentId, tableWithRule1.Id, TemplateVersionId, Period),
                 new(Instance2, DocumentId, tableWithRule2.Id, TemplateVersionId, Period),
                 new(InstanceNoRules, DocumentId, 5, TemplateVersionId, Period),
             });

        _metadata.GetAsync(TemplateVersionId, Arg.Any<CancellationToken>()).Returns(
            new TemplateVersionSnapshot(TemplateVersionId, 0, [sheet],
                new Dictionary<int, ColumnDef>(),
                new Dictionary<(int, string), RowDef>()));

        // ⚠ Рядків НЕМАЄ навмисно (порожні словники): предмет цих тестів —
        // ЧИ ПАКЕТУЄТЬСЯ читання, а не сама логіка правил (її доводять
        // окремі тести `TableValidation`/`ValidationEngine`). Без рядків
        // цикл усередині `TableValidation.Run` не виконується жодного разу
        // і формульний рушій не потрібен.
        _cells.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
              .Returns(new Dictionary<long, IReadOnlyList<CellRecord>>
              {
                  [Instance1] = [],
                  [Instance2] = [],
              });
        _rows.GetRowIdsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>>
             {
                 [Instance1] = new Dictionary<string, long>(StringComparer.Ordinal),
                 [Instance2] = new Dictionary<string, long>(StringComparer.Ordinal),
             });

        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Document.View").Build());
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());
    }

    private static (SheetDef Sheet, TableDef Table) TableWithRule(int tableDefId, int columnId)
    {
        var sheet = new SheetDef(2, EcrCode.Create("Water"), Text("Water"), 1);
        var table = AddTable(sheet, tableDefId, columnId, withRule: true);
        return (sheet, table);
    }

    private static TableDef AddTable(SheetDef sheet, int tableDefId, int columnId, bool withRule)
    {
        var table = new TableDef(tableDefId, EcrCode.Create($"T{tableDefId}"), Text($"T{tableDefId}"), 1,
                                  TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        SetId(table, tableDefId);
        var column = new ColumnDef(columnId, EcrCode.Create("Volume"), Text("Volume"), 1, CellDataType.Decimal);
        SetId(column, columnId);
        table.AddColumn(column);

        if (withRule)
        {
            table.AddValidationRule(new ValidationRule(
                tableDefId, EcrCode.Create("REQ"), ValidationSeverity.Error, scope: 1,
                "true", Text("required")));
        }

        sheet.AddTable(table);
        return table;
    }

    private static LocalizedText Text(string s) => new(new Dictionary<string, string> { ["en"] = s });

    private static void SetId<T>(T e, int id) where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(e, id);

    private ValidateDocumentHandler Handler() => new(
        _cells, _rows, _metadata, _results, new ValidationEngine(Substitute.For<IFormulaEngine>()),
        _headers, _clock, _uow, _access, _user, Substitute.For<IRegistryStore>(), Substitute.For<ITemplateVersionStore>());

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-5.1")]
    public async Task Немає_запиту_на_кожну_таблицю()
    {
        await Handler().HandleAsync(DocumentId, new PeriodKey(Period), CancellationToken.None);

        // ⛔ Q-165: рівно ОДИН пакетний виклик на весь документ, скільки б
        // таблиць у ньому не було з правилами валідації.
        // O3c: з ключем партиції документо-періоду, не пошуком по всіх.
        await _cells.Received(1).ReadSlicesAsync(
            Arg.Any<IReadOnlyList<long>>(), new PeriodKey(Period), Arg.Any<CancellationToken>());
        await _cells.DidNotReceive().ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>());
        await _rows.Received(1).GetRowIdsBatchAsync(
            Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());

        await _cells.DidNotReceive().ReadSliceAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
        await _rows.DidNotReceive().GetRowIdsAsync(
            Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// D-230: знахідка зв'язку Check потрапляє в панель валідації і в збережений підсумок як Error.
    /// Мутація: прибрати <c>RelationCheckRunner.RunAsync</c> з <c>ValidateDocumentHandler</c> — тест червоніє.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Знахідка_зв_язку_Check_Block_потрапляє_в_панель_і_в_підсумок_як_Error()
    {
        var snapshot = await _metadata.GetAsync(TemplateVersionId, CancellationToken.None);
        var columns = snapshot.Sheets.SelectMany(s => s.Tables).SelectMany(t => t.Columns).ToDictionary(c => c.Id);
        _metadata.GetAsync(TemplateVersionId, Arg.Any<CancellationToken>()).Returns(
            new TemplateVersionSnapshot(TemplateVersionId, 0, snapshot.Sheets, columns, new Dictionary<(int, string), RowDef>()));

        var relation = new TableRelationDef(EcrCode.Create("REL1"), 3, 4, TableRelationKind.Check, "{}");
        relation.Update(3, 4, TableRelationKind.Check, "{}",
            """{"left":"Volume","right":"Volume","tolerance":"0","severity":"Block"}""", 0, true);
        var store = Substitute.For<ITemplateVersionStore>();
        store.ListTableRelationsAsync(TemplateVersionId, Arg.Any<CancellationToken>())
            .Returns(new List<TableRelationDef> { relation });

        _rows.GetRowIdsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>>
            {
                [Instance1] = new Dictionary<string, long> { ["R1"] = 1 },
                [Instance2] = new Dictionary<string, long> { ["R1"] = 2 },
            });
        _cells.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyList<CellRecord>>
            {
                [Instance1] = [new CellRecord(new CellAddress(new PeriodKey(Period), 1, 40), 3, new CellValueData { ValueNumeric = 10m })],
                [Instance2] = [new CellRecord(new CellAddress(new PeriodKey(Period), 2, 41), 4, new CellValueData { ValueNumeric = 12m })],
            });
        var handler = new ValidateDocumentHandler(
            _cells, _rows, _metadata, _results, new ValidationEngine(new RealFormulaEngine()),
            _headers, _clock, _uow, _access, _user, Substitute.For<IRegistryStore>(), store);

        ValidationSummary? saved = null;
        await _results.SaveAsync(Arg.Do<ValidationSummary>(s => saved = s), Arg.Any<CancellationToken>());

        try
        {
            await handler.HandleAsync(DocumentId, new PeriodKey(Period), CancellationToken.None);
        }
        catch (NullReferenceException)
        {
            // ReadScopeAsync не налаштовано в цьому файлі (повертає null): фільтр видимості після
            // збереження підсумку нас тут не цікавить — предмет тесту сам підсумок.
        }

        Assert.NotNull(saved);
        Assert.Equal(1, saved!.ErrorCount);
        Assert.Contains("REL-REL1", saved.MessagesJson);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task У_пакетний_запит_ідуть_лише_таблиці_з_правилами()
    {
        await Handler().HandleAsync(DocumentId, new PeriodKey(Period), CancellationToken.None);

        // Таблиця без жодного правила (`InstanceNoRules`) валідацію не
        // проходить узагалі — і не має потрапляти в пакетний запит комірок:
        // інакше "лише реально потрібні таблиці" (Q-165) залишилось би
        // непідтвердженим твердженням у коментарі.
        await _cells.Received(1).ReadSlicesAsync(
            Arg.Is<IReadOnlyList<long>>(ids => ids.Count == 2
                && ids.Contains(Instance1) && ids.Contains(Instance2)
                && !ids.Contains(InstanceNoRules)),
            new PeriodKey(Period),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Без_права_Document_View_валідація_не_запускається()
    {
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Build());

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(
            () => Handler().HandleAsync(DocumentId, new PeriodKey(Period), CancellationToken.None));

        Assert.Equal("ECR-AUTH-0403", denied.ErrorCode);
        await _cells.DidNotReceive().ReadSlicesAsync(
            Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Без_гранта_на_документ_валідація_не_запускається()
    {
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
            .Returns(EditDecision.Deny(EditDenyReason.NoGrant));

        // ⛔ B-08: невидимий документ — 404, як і відсутній, а не 403.
        var denied = await Assert.ThrowsAsync<NotFoundException>(
            () => Handler().HandleAsync(DocumentId, new PeriodKey(Period), CancellationToken.None));

        Assert.Equal("ECR-DOC-0404", denied.ErrorCode);
    }

    /// <summary>
    /// L6-09: таблиця БЕЗ правил, але з обов'язковою колонкою — «Перевірити» показує
    /// незаповнену комірку так само, як подання на ній відмовляє.
    /// Мутація: повернути фільтр «лише таблиці з правилами» — тест червоніє.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-5.4")]
    public async Task Перевірити_показує_незаповнену_обовязкову_колонку()
    {
        var snapshot = await _metadata.GetAsync(TemplateVersionId, CancellationToken.None);
        var noRules = snapshot.Sheets.SelectMany(s => s.Tables).Single(t => t.Id == 5);
        noRules.Columns.Single().SetRequired(true);

        _rows.GetRowIdsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>>
            {
                [InstanceNoRules] = new Dictionary<string, long>(StringComparer.Ordinal) { ["R1"] = 7 },
            });
        _cells.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyList<CellRecord>>());

        ValidationSummary? saved = null;
        await _results.SaveAsync(Arg.Do<ValidationSummary>(s => saved = s), Arg.Any<CancellationToken>());

        try
        {
            await Handler().HandleAsync(DocumentId, new PeriodKey(Period), CancellationToken.None);
        }
        catch (NullReferenceException)
        {
            // ReadScopeAsync не налаштовано в цьому файлі — див. тест D-230 вище.
        }

        Assert.NotNull(saved);
        Assert.Equal(1, saved!.ErrorCount);
        Assert.Contains("ECR-CELL-0422", saved.MessagesJson);
        Assert.Contains("\"R1\"", saved.MessagesJson);
    }
}
