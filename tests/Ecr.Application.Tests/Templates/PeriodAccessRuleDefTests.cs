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
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// Авторство правил доступу до періоду через API (<c>ФВ-2.15</c>, W5.4).
/// </summary>
/// <remarks>
/// ⛔ На відміну від <c>ValidationRuleTests</c>/<c>SheetDefTests</c>: тут три
/// дії, а не дві (<c>Create</c> POST + <c>Save</c> PUT-за-id + <c>Delete</c>),
/// бо в <c>PeriodAccessRuleDef</c> немає природного коду — докладніше в
/// <c>PeriodAccessRuleHandlers.cs</c>.
/// </remarks>
public sealed class PeriodAccessRuleDefTests
{
    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly ITemplateVersionStore _store = Substitute.For<ITemplateVersionStore>();
    private readonly IRepository<PeriodAccessRuleDef, int> _rules =
        Substitute.For<IRepository<PeriodAccessRuleDef, int>>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    private readonly TemplateVersion _draft;
    private readonly SheetDef _sheet;
    private readonly TableDef _table;
    private readonly ColumnDef _lookup;
    private readonly ColumnDef _decimal;
    private readonly SheetDef _otherSheet;
    private readonly TableDef _otherTable;
    private readonly ColumnDef _otherLookup;

    public PeriodAccessRuleDefTests()
    {
        // ⛔ Q-244: обробники тепер виконують запис/аудит/SaveChanges через
        // IUnitOfWork.ExecuteInTransactionAsync(Func<CancellationToken, Task>, ...).
        // Без цього налаштування NSubstitute ніколи не викликає передане
        // замикання — жодна перевірка нижче не виконалась би насправді.
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));

        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        _sheet = builder.Sheet("Water");
        _table = builder.Table(_sheet, "Main");
        _lookup = builder.Column(_table, "PERMIT", CellDataType.Lookup);
        _decimal = builder.Column(_table, "QTY", CellDataType.Decimal);
        _otherSheet = builder.Sheet("Air");
        _otherTable = builder.Table(_otherSheet, "Permits");
        _otherLookup = builder.Column(_otherTable, "PERMIT_REF", CellDataType.Lookup);

        _draft = new TemplateVersion(templateId: 1, version: "1.0.0.0", createdByUserId: 7, utcNow: Now);
        typeof(TemplateVersion).GetProperty(nameof(TemplateVersion.Id))!.SetValue(_draft, 1);
        typeof(TemplateVersion)
            .GetField("_sheets", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(_draft, new List<SheetDef> { _sheet, _otherSheet });

        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.Edit").Build());

        _store.GetWithStructureAsync(1, Arg.Any<CancellationToken>()).Returns(_draft);
        _store.HasDocumentsAsync(1, Arg.Any<CancellationToken>()).Returns(false);
    }

    private static PeriodAccessRuleDef WithId(PeriodAccessRuleDef rule, int id)
    {
        typeof(PeriodAccessRuleDef).GetProperty(nameof(PeriodAccessRuleDef.Id))!.SetValue(rule, id);
        return rule;
    }

    private static CreatePeriodAccessRuleCommand CreateCommand(
        PeriodAccessRuleKind kind = PeriodAccessRuleKind.AlwaysReadOnly,
        OutOfWindowBehavior onOutOfWindow = OutOfWindowBehavior.ReadOnly,
        int? sheetDefId = null, int? tableDefId = null, int? sourceColumnDefId = null,
        short? relativeOffset = null, string? conditionExpr = null)
        => new(
            kind, onOutOfWindow, sheetDefId, tableDefId, RoleId: null, RowKind: null,
            FromSequence: null, ToSequence: null, sourceColumnDefId, relativeOffset, conditionExpr);

    private CreatePeriodAccessRuleHandler Create()
        => new(_store, _rules, new ChangeClassifier(), _audit, _uow, _clock, _access, _user);

    private SavePeriodAccessRuleHandler Save()
        => new(_store, _rules, new ChangeClassifier(), _audit, _uow, _clock, _access, _user);

    private DeletePeriodAccessRuleHandler Delete()
        => new(_store, _rules, new ChangeClassifier(), _audit, _uow, _clock, _access, _user);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-2.15")]
    public async Task Нове_правило_заводиться_і_потрапляє_в_аудит_структурних_змін()
    {
        var created = await Create().HandleAsync(
            1, CreateCommand(sheetDefId: _sheet.Id), CancellationToken.None);

        Assert.Equal(PeriodAccessRuleKind.AlwaysReadOnly, created.RuleKind);
        Assert.Equal(_sheet.Id, created.SheetDefId);
        Assert.Equal(OutOfWindowBehavior.ReadOnly, created.OnOutOfWindow);

        _rules.Received(1).Add(Arg.Any<PeriodAccessRuleDef>());

        await _audit.Received(1).WriteStructureChangeAsync(
            Arg.Is<StructureChangeRecord>(r => r.EntityType == "PeriodAccessRuleDef" && r.Operation == "Create"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-2.15")]
    public async Task Вид_SourceWindow_без_SourceColumnDefId_відхиляється()
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Create().HandleAsync(
                1, CreateCommand(PeriodAccessRuleKind.SourceWindow, sheetDefId: _sheet.Id),
                CancellationToken.None));

        Assert.Equal(ErrorCodes.TemplateInvalid, error.ErrorCode);
        Assert.Equal("err.ECR-TMPL-0422.sourceWindowRequiresColumn", error.Details?["messageKey"]);
        _rules.DidNotReceive().Add(Arg.Any<PeriodAccessRuleDef>());
    }

    /// <summary>
    /// ⛔ A1-02 (аудит 09.10c): контроль — Lookup-колонка ТІЄЇ таблиці, про яку правило, приймається.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-2.15")]
    public async Task SourceWindow_з_Lookup_колонкою_своєї_таблиці_приймається()
    {
        var created = await Create().HandleAsync(
            1,
            CreateCommand(PeriodAccessRuleKind.SourceWindow, tableDefId: _table.Id, sourceColumnDefId: _lookup.Id),
            CancellationToken.None);

        Assert.Equal(_lookup.Id, created.SourceColumnDefId);
        _rules.Received(1).Add(Arg.Any<PeriodAccessRuleDef>());
    }

    /// <summary>
    /// ⛔ A1-02: колонка-джерело іншої таблиці, іншої версії чи не-Lookup не дає значень у рядках зрізу —
    /// правило «налаштоване», а не блокує нічого (fail-open). Відмова 422 з ключем, правило не заводиться.
    /// </summary>
    [Theory]
    [InlineData("otherTable")]
    [InlineData("otherVersion")]
    [InlineData("notLookup")]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-2.15")]
    public async Task SourceWindow_з_чужою_або_не_Lookup_колонкою_відхиляється_422(string variant)
    {
        var column = variant switch
        {
            "otherTable" => _otherLookup.Id,
            "otherVersion" => 999,
            _ => _decimal.Id,
        };

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Create().HandleAsync(
                1,
                CreateCommand(PeriodAccessRuleKind.SourceWindow, tableDefId: _table.Id, sourceColumnDefId: column),
                CancellationToken.None));

        Assert.Equal(ErrorCodes.TemplateInvalid, error.ErrorCode);
        Assert.Equal("err.ECR-TMPL-0422.sourceColumnNotInRuleTarget", error.Details?["messageKey"]);
        _rules.DidNotReceive().Add(Arg.Any<PeriodAccessRuleDef>());
    }

    /// <summary>
    /// ⛔ A1-02: аркуш і таблиця перевірялися кожен окремо — пара «аркуш A + таблиця з аркуша B» зберігалася, а
    /// <c>Targets</c> вимагає збігу обох: правило не накривало жодної комірки.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Таблиця_не_з_обраного_аркуша_відхиляється_422()
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Create().HandleAsync(
                1, CreateCommand(sheetDefId: _sheet.Id, tableDefId: _otherTable.Id), CancellationToken.None));

        Assert.Equal(ErrorCodes.TemplateInvalid, error.ErrorCode);
        Assert.Equal("err.ECR-TMPL-0422.tableNotInSheet", error.Details?["messageKey"]);
        _rules.DidNotReceive().Add(Arg.Any<PeriodAccessRuleDef>());
    }

    /// <summary>
    /// ⛔ A1-02: команда зміни колонку-джерело не несе, а таблицю міняє — колонку правила звіряють з НОВОЮ ціллю.
    /// Перенести SourceWindow на таблицю, де його колонки немає, означало б той самий мертвий замок.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Зміна_таблиці_SourceWindow_без_його_колонки_відхиляється_422()
    {
        var rule = WithId(
            PeriodAccessRuleDef.ForSourceWindow(1, _lookup.Id, OutOfWindowBehavior.ReadOnly).ForTable(_table.Id), 43);
        _rules.FindAsync(43, Arg.Any<CancellationToken>()).Returns(rule);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().HandleAsync(
                1, 43,
                new UpdatePeriodAccessRuleCommand(OutOfWindowBehavior.ReadOnly, null, _otherTable.Id, null, null),
                CancellationToken.None));

        Assert.Equal("err.ECR-TMPL-0422.sourceColumnNotInRuleTarget", error.Details?["messageKey"]);
        Assert.Equal(_table.Id, rule.TableDefId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Ні_аркуш_ні_таблиця_не_задані_CK_PAR_Target_відхиляється()
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Create().HandleAsync(1, CreateCommand(), CancellationToken.None));

        Assert.Equal(ErrorCodes.TemplateInvalid, error.ErrorCode);
        Assert.Equal("err.ECR-TMPL-0422.periodAccessRuleNoTarget", error.Details?["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Аркуш_чужої_версії_відхиляється()
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Create().HandleAsync(1, CreateCommand(sheetDefId: 999), CancellationToken.None));

        Assert.Equal(ErrorCodes.TemplateInvalid, error.ErrorCode);
        Assert.Equal("err.ECR-TMPL-0422.sheetNotInVersion", error.Details?["messageKey"]);
        Assert.Equal("999", error.Details?["sheetDefId"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-7.1")]
    public async Task Створення_у_опублікованій_версії_відхиляється()
    {
        _draft.Publish(publishedByUserId: 8, utcNow: Now);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => Create().HandleAsync(1, CreateCommand(sheetDefId: _sheet.Id), CancellationToken.None));

        Assert.Equal("ECR-TMPL-0409", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-2.16")]
    public async Task Змінює_прив_язку_і_поведінку_наявного_правила()
    {
        var rule = WithId(PeriodAccessRuleDef.AlwaysReadOnly(1, OutOfWindowBehavior.ReadOnly).ForSheet(_sheet.Id), 42);
        _rules.FindAsync(42, Arg.Any<CancellationToken>()).Returns(rule);

        var updated = await Save().HandleAsync(
            1, 42, new UpdatePeriodAccessRuleCommand(OutOfWindowBehavior.Warn, null, _table.Id, null, null),
            CancellationToken.None);

        Assert.Equal(OutOfWindowBehavior.Warn, updated.OnOutOfWindow);
        Assert.Null(updated.SheetDefId);
        Assert.Equal(_table.Id, updated.TableDefId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Зміна_на_Hide_відхиляється()
    {
        var rule = WithId(PeriodAccessRuleDef.AlwaysReadOnly(1, OutOfWindowBehavior.ReadOnly).ForSheet(_sheet.Id), 42);
        _rules.FindAsync(42, Arg.Any<CancellationToken>()).Returns(rule);

#pragma warning disable CS0618
        var command = new UpdatePeriodAccessRuleCommand(OutOfWindowBehavior.Hide, _sheet.Id, null, null, null);
#pragma warning restore CS0618

        var error = await Assert.ThrowsAsync<DomainException>(
            () => Save().HandleAsync(1, 42, command, CancellationToken.None));

        Assert.Equal("ECR-CFG-0422", error.ErrorCode);
        Assert.Equal("err.ECR-CFG-0422.hideRetired", error.Details?["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Зміна_правила_чужої_версії_відхиляється_404()
    {
        var rule = WithId(PeriodAccessRuleDef.AlwaysReadOnly(2, OutOfWindowBehavior.ReadOnly).ForSheet(1), 42);
        _rules.FindAsync(42, Arg.Any<CancellationToken>()).Returns(rule);

        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => Save().HandleAsync(
                1, 42, new UpdatePeriodAccessRuleCommand(OutOfWindowBehavior.ReadOnly, _sheet.Id, null, null, null),
                CancellationToken.None));

        Assert.Equal("ECR-TMPL-0404", error.ErrorCode);
        Assert.Equal("err.ECR-TMPL-0404.periodAccessRule", error.Details?["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-2.15")]
    public async Task Видалення_прибирає_правило_фізично()
    {
        var rule = WithId(PeriodAccessRuleDef.AlwaysReadOnly(1, OutOfWindowBehavior.ReadOnly).ForSheet(_sheet.Id), 42);
        _rules.FindAsync(42, Arg.Any<CancellationToken>()).Returns(rule);

        await Delete().HandleAsync(1, 42, CancellationToken.None);

        _rules.Received(1).Remove(rule);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Видалення_неіснуючого_правила_відхиляється_404()
    {
        _rules.FindAsync(42, Arg.Any<CancellationToken>()).Returns((PeriodAccessRuleDef?)null);

        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => Delete().HandleAsync(1, 42, CancellationToken.None));

        Assert.Equal("ECR-TMPL-0404", error.ErrorCode);
        Assert.Equal("err.ECR-TMPL-0404.periodAccessRule", error.Details?["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Без_права_Template_Edit_правило_не_заводиться()
    {
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.View").Build());

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Create().HandleAsync(1, CreateCommand(sheetDefId: _sheet.Id), CancellationToken.None));

        _rules.DidNotReceive().Add(Arg.Any<PeriodAccessRuleDef>());
    }
}
