using System.Reflection;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// Авторство структури шаблону — формула колонки чи рядка (<c>W5.3</c>), за
/// зразком <c>SheetDefTests</c> (<c>W5.0</c>).
/// </summary>
/// <remarks>
/// ⛔ Батьківські таблицю, колонку й рядок цей тест заводить НАПРЯМУ через
/// конструктори домену, а не через HTTP: паралельно (в ізольованих робочих
/// копіях) ідуть зрізи <c>TableDef</c>+<c>ColumnDef</c> (W5.1) і
/// <c>RowDef</c> (W5.2), і чекати на їхні API означало б, що цей тест не
/// компілюється, доки хтось інший не зіллє свою гілку.
/// </remarks>
public sealed class FormulaDefTests
{
    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly PropertyInfo IdProperty = typeof(Entity<int>).GetProperty("Id")!;

    private readonly ITemplateVersionStore _store = Substitute.For<ITemplateVersionStore>();
    private readonly IMetadataCache _metadataCache = Substitute.For<IMetadataCache>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    // ⚠ Не мок: обробник читає й пише через ЦЕЙ агрегат (`GetWithStructureAsync`
    // повертає його самого) — так само, як `_draft` у `SheetDefTests`.
    private readonly TemplateVersion _draft =
        new(templateId: 1, version: "1.0.0.0", createdByUserId: 7, utcNow: Now);

    private readonly TableDef _table;
    private readonly ColumnDef _column;
    private readonly RowDef _row;

    public FormulaDefTests()
    {
        var sheet = new SheetDef(_draft.Id, EcrCode.Create("Water"), Text("Water"), 0);
        SetId(sheet, 1);
        _draft.AddSheet(sheet);

        _table = new TableDef(
            sheet.Id, EcrCode.Create("Main"), Text("Main"), 0,
            TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        SetId(_table, 10);
        sheet.AddTable(_table);

        _column = new ColumnDef(_table.Id, EcrCode.Create("Total"), Text("Total"), 0, CellDataType.Decimal);
        SetId(_column, 100);
        _table.AddColumn(_column);

        _row = new RowDef(_table.Id, RowKey.Create("7001001"), 0, Text("7001001"), RowKind.Item);
        SetId(_row, 200);
        _table.AddRow(_row);

        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.Edit").Build());

        _store.GetWithStructureAsync(1, Arg.Any<CancellationToken>()).Returns(_draft);
        _store.HasDocumentsAsync(1, Arg.Any<CancellationToken>()).Returns(false);
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId(Entity<int> entity, int id) => IdProperty.SetValue(entity, id);

    private static SaveFormulaDefCommand Command(string expression = "[Jan] + [Feb]")
        => new(ExpressionDialect.Template, expression);

    private SaveFormulaDefHandler Save()
        => new(_store, new ChangeClassifier(), _metadataCache, _audit, _uow, _clock, _access, _user);

    private DeleteFormulaDefHandler Delete()
        => new(_store, new ChangeClassifier(), _metadataCache, _audit, _uow, _clock, _access, _user);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Нова_формула_колонки_зберігається_і_потрапляє_в_аудит()
    {
        var saved = await Save().HandleAsync(
            1, _table.Id, FormulaScope.Column, _column.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Command(), CancellationToken.None);

        Assert.Equal(_column.Id, saved.ColumnDefId);
        Assert.Null(saved.RowDefId);
        Assert.Equal("[Jan] + [Feb]", saved.Expression);
        Assert.Single(_table.Formulas);

        await _audit.Received(1).WriteStructureChangeAsync(
            Arg.Is<StructureChangeRecord>(r => r.EntityType == "FormulaDef" && r.Operation == "Create"),
            Arg.Any<CancellationToken>());

        // ⛔ Без цього виклику прогрітий кеш метаданих віддавав би структуру
        // без щойно доданої формули — див. коментар у SaveFormulaDefHandler.
        await _metadataCache.Received(1).InvalidateAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Формула_рядка_адресується_RowKey_а_не_числовим_ідентифікатором()
    {
        var saved = await Save().HandleAsync(
            1, _table.Id, FormulaScope.Row, _row.RowKeyValue, Command("[Total]"), CancellationToken.None);

        Assert.Equal(_row.Id, saved.RowDefId);
        Assert.Null(saved.ColumnDefId);
        Assert.Single(_table.Formulas);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Повторний_запис_тією_самою_ціллю_оновлює_а_не_дублює()
    {
        await Save().HandleAsync(
            1, _table.Id, FormulaScope.Column, _column.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Command("[Jan]"), CancellationToken.None);

        var updated = await Save().HandleAsync(
            1, _table.Id, FormulaScope.Column, _column.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Command("[Jan] + [Feb]"), CancellationToken.None);

        // ⛔ Ідентичність — сама ціль (колонка): другий запис на ту саму
        // колонку ЗМІНЮЄ наявну формулу, а не заводить другу (D2-147).
        Assert.Single(_table.Formulas);
        Assert.Equal("[Jan] + [Feb]", updated.Expression);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Запис_на_неіснуючу_колонку_відхиляється_404()
    {
        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => Save().HandleAsync(1, _table.Id, FormulaScope.Column, "9999", Command(), CancellationToken.None));

        Assert.Equal("ECR-TMPL-0404", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Запис_на_неіснуючий_рядок_відхиляється_404()
    {
        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => Save().HandleAsync(1, _table.Id, FormulaScope.Row, "missing", Command(), CancellationToken.None));

        Assert.Equal("ECR-TMPL-0404", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Запис_на_неіснуючу_таблицю_відхиляється_404()
    {
        var error = await Assert.ThrowsAsync<NotFoundException>(() => Save().HandleAsync(
            1, 999, FormulaScope.Column, _column.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Command(), CancellationToken.None));

        Assert.Equal("ECR-TMPL-0404", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Область_Cell_тут_не_приймається()
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Save().HandleAsync(
            1, _table.Id, FormulaScope.Cell, _column.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Command(), CancellationToken.None));

        Assert.Equal(ErrorCodes.TemplateInvalid, error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-7.1")]
    public async Task Запис_формули_в_опублікованій_версії_відхиляється()
    {
        _draft.Publish(publishedByUserId: 8, utcNow: Now);

        var error = await Assert.ThrowsAsync<DomainException>(() => Save().HandleAsync(
            1, _table.Id, FormulaScope.Column, _column.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Command(), CancellationToken.None));

        Assert.Equal("ECR-TMPL-0409", error.ErrorCode);
        Assert.Empty(_table.Formulas);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-7.6")]
    public async Task Видалення_прибирає_формулу_м_яко_а_не_фізично()
    {
        var target = _column.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await Save().HandleAsync(1, _table.Id, FormulaScope.Column, target, Command(), CancellationToken.None);
        _metadataCache.ClearReceivedCalls();

        await Delete().HandleAsync(1, _table.Id, FormulaScope.Column, target, CancellationToken.None);

        // ⛔ Запис лишається: на неї може посилатися граф залежностей інших
        // формул навіть у чернетці (ФВ-7.6).
        var formula = Assert.Single(_table.Formulas);
        Assert.True(formula.IsDeleted);

        await _metadataCache.Received(1).InvalidateAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Видалення_неіснуючої_формули_відхиляється_404()
    {
        var error = await Assert.ThrowsAsync<NotFoundException>(() => Delete().HandleAsync(
            1, _table.Id, FormulaScope.Column, _column.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            CancellationToken.None));

        Assert.Equal("ECR-TMPL-0404", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-7.1")]
    public async Task Видалення_формули_в_опублікованій_версії_відхиляється()
    {
        var target = _column.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await Save().HandleAsync(1, _table.Id, FormulaScope.Column, target, Command(), CancellationToken.None);
        _draft.Publish(publishedByUserId: 8, utcNow: Now);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => Delete().HandleAsync(1, _table.Id, FormulaScope.Column, target, CancellationToken.None));

        Assert.Equal("ECR-TMPL-0409", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Без_права_Template_Edit_формула_не_записується()
    {
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.View").Build());

        await Assert.ThrowsAsync<AccessDeniedException>(() => Save().HandleAsync(
            1, _table.Id, FormulaScope.Column, _column.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Command(), CancellationToken.None));

        Assert.Empty(_table.Formulas);
    }
}
