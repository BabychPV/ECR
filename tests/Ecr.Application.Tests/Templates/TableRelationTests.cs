using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// Зв'язки між таблицями версії: читання, запис і видалення (<c>ФВ-2.12</c>).
/// </summary>
/// <remarks>
/// ⛔ Головне правило тут не про зв'язки, а про версію: зв'язок — СТРУКТУРА,
/// бо від нього залежить, звідки в таблиці беруться числа. Тому правиться він
/// лише в чернетці (<c>ФВ-7.1</c>), а видалення у версії з документами —
/// відмова, а не попередження (<c>ФВ-7.4</c>).
/// </remarks>
public sealed class TableRelationTests
{
    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly IRepository<TemplateVersion, int> _versions =
        Substitute.For<IRepository<TemplateVersion, int>>();

    private readonly IRepository<TableRelationDef, int> _relations =
        Substitute.For<IRepository<TableRelationDef, int>>();

    private readonly ITemplateVersionStore _store = Substitute.For<ITemplateVersionStore>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    private readonly TemplateVersion _draft =
        new(templateId: 1, version: "1.0.0.0", createdByUserId: 7, utcNow: Now);

    public TableRelationTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }
                .Permission("Template.Edit").Permission("Template.View").Build());

        _versions.FindAsync(1, Arg.Any<CancellationToken>()).Returns(_draft);

        // Дві таблиці версії; третьої (99) у ній немає — на цьому тримається
        // перевірка належності.
        _store.ListTableCodesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, string> { [5] = "Main", [6] = "Consolidation" });
    }

    private static TableRelationDef Existing()
        => new(EcrCode.Create("WaterRollup"), 5, 6, TableRelationKind.Rollup, """{"by":"RowKey"}""");

    private static SaveTableRelationCommand Command(int source = 5, int target = 6)
        => new(source, target, TableRelationKind.Rollup, """{"by":"RowKey"}""", null, 0, true);

    private SaveTableRelationHandler Save()
        => new(_versions, _relations, _store, new ChangeClassifier(), _audit, _uow, _clock, _access, _user);

    private DeleteTableRelationHandler Delete()
        => new(_versions, _relations, _store, new ChangeClassifier(), _audit, _uow, _clock, _access, _user);

    private ListTableRelationsHandler List()
        => new(_versions, _store, _access, _user);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-2.12")]
    public async Task Новий_зв_язок_зберігається_і_потрапляє_в_аудит_структурних_змін()
    {
        var saved = await Save().HandleAsync(1, "WaterRollup", Command(), CancellationToken.None);

        Assert.Equal("WaterRollup", saved.Code);
        Assert.Equal("Main", saved.SourceTableCode);
        Assert.Equal("Consolidation", saved.TargetTableCode);
        Assert.True(saved.IsEditable);

        _relations.Received(1).Add(Arg.Any<TableRelationDef>());

        // ⚠ Аудит — не косметика: структурна зміна версії має слід, інакше
        // питання «хто прибрав цей rollup» не має відповіді.
        await _audit.Received(1).WriteStructureChangeAsync(
            Arg.Is<StructureChangeRecord>(r =>
                r.EntityType == "TableRelationDef" && r.Operation == "Create"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-7.1")]
    public async Task Правка_зв_язку_в_опублікованій_версії_відхиляється()
    {
        _draft.Publish(publishedByUserId: 8, utcNow: Now);

        // ⛔ Зв'язок вирішує, звідки в таблиці числа. Змінити його в
        // опублікованій версії означало б змінити вже подані форми заднім
        // числом.
        var error = await Assert.ThrowsAsync<DomainException>(
            () => Save().HandleAsync(1, "WaterRollup", Command(), CancellationToken.None));

        Assert.Equal("ECR-TMPL-0409", error.ErrorCode);
        _relations.DidNotReceive().Add(Arg.Any<TableRelationDef>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-2.12")]
    public async Task Таблиця_чужої_версії_у_зв_язку_відхиляється()
    {
        // ⛔ Зовнішній ключ таке прийняв би: він не знає про версії. Зв'язок
        // перетнув би межу версії, і клон переніс би лише його половину.
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().HandleAsync(1, "Crossing", Command(target: 99), CancellationToken.None));

        Assert.Equal("ECR-TMPL-0422", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-7.4")]
    public async Task Видалення_зв_язку_у_версії_з_документами_відхиляється_кодом_ECR_SCHM_0409()
    {
        _store.FindTableRelationAsync(1, "WaterRollup", Arg.Any<CancellationToken>())
            .Returns(Existing());
        _store.HasDocumentsAsync(1, Arg.Any<CancellationToken>()).Returns(true);

        // ⚠ Саме відмова, а не попередження: числа в документах рахувалися з
        // урахуванням зв'язку.
        var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Delete().HandleAsync(1, "WaterRollup", CancellationToken.None));

        Assert.Equal("ECR-SCHM-0409", error.ErrorCode);
        _relations.DidNotReceive().Remove(Arg.Any<TableRelationDef>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-2.12")]
    public async Task Видалення_зв_язку_з_чернетки_без_документів_проходить()
    {
        _store.FindTableRelationAsync(1, "WaterRollup", Arg.Any<CancellationToken>())
            .Returns(Existing());
        _store.HasDocumentsAsync(1, Arg.Any<CancellationToken>()).Returns(false);

        await Delete().HandleAsync(1, "WaterRollup", CancellationToken.None);

        _relations.Received(1).Remove(Arg.Any<TableRelationDef>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-2.12")]
    public async Task Перелік_зв_язків_порожній_і_це_норма()
    {
        _store.ListTableRelationsAsync(1, Arg.Any<CancellationToken>())
            .Returns([]);

        // ⚠ Механізм опційний: шаблон без жодного зв'язку працює однаково.
        // Порожнеча тут — відповідь, а не незаповнена конфігурація.
        Assert.Empty(await List().HandleAsync(1, CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-7.1")]
    public async Task Перелік_каже_клієнту_чи_можна_правити_за_станом_версії()
    {
        _store.ListTableRelationsAsync(1, Arg.Any<CancellationToken>())
            .Returns([Existing()]);

        var draft = await List().HandleAsync(1, CancellationToken.None);
        Assert.True(draft[0].IsEditable);

        _draft.Publish(publishedByUserId: 8, utcNow: Now);

        // ⛔ Рахує СЕРВЕР. Клієнт, який виводив би це сам, тримав би другу
        // копію правила «опублікована незмінна».
        var published = await List().HandleAsync(1, CancellationToken.None);
        Assert.False(published[0].IsEditable);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Без_права_Template_Edit_зв_язок_не_записується()
    {
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.View").Build());

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Save().HandleAsync(1, "WaterRollup", Command(), CancellationToken.None));
    }
}
