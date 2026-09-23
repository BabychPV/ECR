// tests/Ecr.Application.Tests/Documents/ValidateDocumentBoolDateRulesTests.cs
using Ecr.Application.Common;
using Ecr.Application.Documents;
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
/// Правила рівня РЯДКА над Bool- і Date-колонками. Той самий клас значень для
/// скоупу 0 (комірка) коректно йшов через <c>CellValueMapping</c>, а скоупи
/// 1/2/3 мали власне ad-hoc розгортання <c>ValueNumeric ?? ValueString</c> —
/// тобто одна мова правил мала два шляхи, і один із них ламав Bool/Date
/// (аудит 2026-09-16, §3.2).
/// </summary>
/// <remarks>
/// ⛔ Наслідок двобічний і однаково поганий. Правило або хибно ламалось
/// (вироджувалось у Warning «не дало логічної відповіді», маскуючи Error, на
/// який розраховує Submit — надто дозволяюче), або хибно спрацьовувало на
/// коректних даних (блокуючи легітимну подачу). Рівно те, від чого застерігає
/// власний коментар `TableValidation`: «подання зобов'язане рахувати РІВНО те
/// саме, що показує кнопка "Перевірити"».
/// </remarks>
public sealed class ValidateDocumentBoolDateRulesTests
{
    private const long DocumentId = 700;
    private const int Period = 202601;
    private const int TemplateVersionId = 2;
    private const long Instance = 500;
    private const int TableDefId = 3;
    private const long RowId = 1001;
    private const string RowKey = "7001001";

    private const int FlagColumnId = 40;
    private const int VolumeColumnId = 41;
    private const int MeasuredOnColumnId = 42;
    private const int PeriodStartColumnId = 43;

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

    public ValidateDocumentBoolDateRulesTests()
    {
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Document.View").Build());
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());

        _rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new List<TableInstanceRef>
             {
                 new(Instance, DocumentId, TableDefId, TemplateVersionId, Period),
             });

        _rows.GetRowIdsBatchAsync(
                 Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>>
             {
                 [Instance] = new Dictionary<string, long>(StringComparer.Ordinal) { [RowKey] = RowId },
             });
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-5.1")]
    public async Task Правило_рівня_рядка_читає_Bool_колонку_а_не_порожнечу()
    {
        // Правило звітності: рядок, позначений «не включати у звіт», у цьому
        // документі бути не повинен. `IncludeInReport = false` — дані КОРЕКТНІ,
        // і правило мусить пройти.
        //
        // ⚠ Вираз навмисно БЕЗ другого операнда через `OR`: `null` у бінарних
        // операторах ПОШИРЮЄТЬСЯ (02b §6.2), тож незаповнений `[Volume]` сам по
        // собі дав би `Null` і замаскував те, що цей тест доводить.
        Arrange("[IncludeInReport] = FALSE",
        [
            new CellRecord(
                new CellAddress(new PeriodKey(Period), RowId, FlagColumnId), TableDefId,
                new CellValueData { ValueBool = false }),
        ]);

        var messages = await Handler().HandleAsync(DocumentId, new PeriodKey(Period), CancellationToken.None);

        // ⛔ Без фіксу `[IncludeInReport]` читалося як `Null`, вираз не давав
        // логічної відповіді, і замість «правило пройшло» з'являлося Warning
        // «правило не дало логічної відповіді» — тобто Error-правило, на яке
        // розраховує Submit, ВИРОДЖУВАЛОСЬ у необов'язкове попередження.
        Assert.Empty(messages);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-5.1")]
    public async Task Правило_рівня_рядка_читає_Bool_колонку_і_коли_воно_справді_порушене()
    {
        // Дзеркальний випадок: `IncludeInReport = true` — правило порушене,
        // і порушення мусить бути видно як Error, а не як Warning «правило не
        // дало логічної відповіді».
        Arrange("[IncludeInReport] = FALSE",
        [
            new CellRecord(
                new CellAddress(new PeriodKey(Period), RowId, FlagColumnId), TableDefId,
                new CellValueData { ValueBool = true }),
        ]);

        var messages = await Handler().HandleAsync(DocumentId, new PeriodKey(Period), CancellationToken.None);

        var message = Assert.Single(messages);
        Assert.Equal("REQ", message.RuleCode);
        Assert.Equal(ValidationSeverity.Error, message.Severity);
        Assert.Equal(RowKey, message.RowKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-5.1")]
    public async Task Правило_рівня_рядка_порівнює_дві_Date_колонки()
    {
        // `[MeasuredOn] >= [PeriodStart]` — 15 січня не раніше за 1 січня, дані
        // коректні. Без фіксу обидві дати читались як `Null`, і правило знову
        // «не давало логічної відповіді».
        Arrange("[MeasuredOn] >= [PeriodStart]",
        [
            new CellRecord(
                new CellAddress(new PeriodKey(Period), RowId, MeasuredOnColumnId), TableDefId,
                new CellValueData { ValueDate = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc) }),
            new CellRecord(
                new CellAddress(new PeriodKey(Period), RowId, PeriodStartColumnId), TableDefId,
                new CellValueData { ValueDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) }),
        ]);

        var messages = await Handler().HandleAsync(DocumentId, new PeriodKey(Period), CancellationToken.None);

        Assert.Empty(messages);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-5.1")]
    public async Task Дата_виміру_раніша_за_початок_періоду_це_справжнє_порушення()
    {
        Arrange("[MeasuredOn] >= [PeriodStart]",
        [
            new CellRecord(
                new CellAddress(new PeriodKey(Period), RowId, MeasuredOnColumnId), TableDefId,
                new CellValueData { ValueDate = new DateTime(2025, 12, 28, 0, 0, 0, DateTimeKind.Utc) }),
            new CellRecord(
                new CellAddress(new PeriodKey(Period), RowId, PeriodStartColumnId), TableDefId,
                new CellValueData { ValueDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) }),
        ]);

        var messages = await Handler().HandleAsync(DocumentId, new PeriodKey(Period), CancellationToken.None);

        var message = Assert.Single(messages);
        Assert.Equal("REQ", message.RuleCode);
        Assert.Equal(ValidationSeverity.Error, message.Severity);
    }

    /// <summary>Таблиця з правилом рівня рядка і зрізом значень.</summary>
    private void Arrange(string expression, IReadOnlyList<CellRecord> cells)
    {
        var sheet = new SheetDef(
            TemplateVersionId, EcrCode.Create("Water"), Text("Water"), 1);

        var table = new TableDef(
            TableDefId, EcrCode.Create("Main"), Text("Main"), 1,
            TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        SetId(table, TableDefId);

        table.AddColumn(Column(FlagColumnId, "IncludeInReport", CellDataType.Bool));
        table.AddColumn(Column(VolumeColumnId, "Volume", CellDataType.Decimal));
        table.AddColumn(Column(MeasuredOnColumnId, "MeasuredOn", CellDataType.Date));
        table.AddColumn(Column(PeriodStartColumnId, "PeriodStart", CellDataType.Date));

        table.AddValidationRule(new ValidationRule(
            TableDefId, EcrCode.Create("REQ"), ValidationSeverity.Error, scope: 1,
            expression, Text("порушено")));

        sheet.AddTable(table);

        _metadata.GetAsync(TemplateVersionId, Arg.Any<CancellationToken>()).Returns(
            new TemplateVersionSnapshot(TemplateVersionId, 0, [sheet],
                new Dictionary<int, ColumnDef>(),
                new Dictionary<(int, string), RowDef>()));

        _cells.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
              .Returns(new Dictionary<long, IReadOnlyList<CellRecord>> { [Instance] = cells });
    }

    private static ColumnDef Column(int id, string code, CellDataType type)
    {
        var column = new ColumnDef(TableDefId, EcrCode.Create(code), Text(code), id, type);
        SetId(column, id);
        return column;
    }

    private static LocalizedText Text(string s) => new(new Dictionary<string, string> { ["en"] = s });

    private static void SetId<T>(T e, int id) where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(e, id);

    // ⚠ РЕАЛЬНИЙ формульний рушій, не заглушка: предмет цих тестів — що саме
    // правило бачить у комірці, а заглушка не обчислює виразу взагалі.
    private ValidateDocumentHandler Handler() => new(
        _cells, _rows, _metadata, _results, new ValidationEngine(new RealFormulaEngine()),
        _headers, _clock, _uow, _access, _user);
}
