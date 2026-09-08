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
/// Авторство структури шаблону — другий вертикальний зріз: таблиця на
/// аркуші (<c>W5.1</c>), той самий патерн, що й <c>SheetDefTests</c> (W5.0).
/// </summary>
public sealed class TableDefTests
{
    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly ITemplateVersionStore _store = Substitute.For<ITemplateVersionStore>();
    private readonly IMetadataCache _metadataCache = Substitute.For<IMetadataCache>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    // ⚠ Не мок: обробник читає й пише через ЦЕЙ агрегат, і саме на ньому
    // тримається перевірка «код уже існує» та підйом Ordinal — так само, як
    // `_draft` у `SheetDefTests`.
    private readonly TemplateVersion _draft =
        new(templateId: 1, version: "1.0.0.0", createdByUserId: 7, utcNow: Now);

    private readonly SheetDef _sheet;

    public TableDefTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.Edit").Build());

        _store.GetWithStructureAsync(1, Arg.Any<CancellationToken>()).Returns(_draft);
        _store.HasDocumentsAsync(1, Arg.Any<CancellationToken>()).Returns(false);

        _sheet = new SheetDef(1, EcrCode.Create("Water"), Name("Water balance"), ordinal: 0);
        _draft.AddSheet(_sheet);
    }

    private static LocalizedText Name(string en)
        => new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = en });

    private static SaveTableDefCommand Command(
        string en = "Balances", int? ordinal = null,
        TableLayoutKind layoutKind = TableLayoutKind.PerPeriodInstance,
        TableRowMode rowMode = TableRowMode.Fixed, int? maxDynamicRows = null)
        => new(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = en },
            ordinal, layoutKind, rowMode, maxDynamicRows);

    private SaveTableDefHandler Save()
        => new(_store, new ChangeClassifier(), _metadataCache, _audit, _uow, _clock, _access, _user);

    private DeleteTableDefHandler Delete()
        => new(_store, new ChangeClassifier(), _metadataCache, _audit, _uow, _clock, _access, _user);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Нова_таблиця_зберігається_і_потрапляє_в_аудит_структурних_змін()
    {
        var saved = await Save().HandleAsync(1, "Water", "Balances", Command(), CancellationToken.None);

        Assert.Equal("Balances", saved.Code);
        Assert.Equal(0, saved.Ordinal);
        Assert.Equal(TableLayoutKind.PerPeriodInstance, saved.LayoutKind);
        Assert.Equal(TableRowMode.Fixed, saved.RowMode);
        Assert.Single(_sheet.Tables);

        // ⚠ Аудит — не косметика: структурна зміна версії має слід, інакше
        // питання «хто додав цю таблицю» не має відповіді.
        await _audit.Received(1).WriteStructureChangeAsync(
            Arg.Is<StructureChangeRecord>(r => r.EntityType == "TableDef" && r.Operation == "Create"),
            Arg.Any<CancellationToken>());

        // ⛔ Без цього виклику прогрітий кеш метаданих віддавав би структуру
        // без щойно доданої таблиці — див. коментар у `SaveTableDefHandler`.
        await _metadataCache.Received(1).InvalidateAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Повторний_запис_тим_самим_кодом_оновлює_а_не_дублює()
    {
        await Save().HandleAsync(1, "Water", "Balances", Command(en: "Balances v1"), CancellationToken.None);
        var updated = await Save().HandleAsync(
            1, "Water", "Balances",
            Command(en: "Balances v2", rowMode: TableRowMode.Mixed, maxDynamicRows: 50),
            CancellationToken.None);

        // ⛔ Ідентичність — код: другий запис тим самим кодом ЗМІНЮЄ наявну
        // таблицю, а не заводить другу (`D2-147`).
        Assert.Single(_sheet.Tables);
        Assert.Equal("Balances v2", updated.NameL10n.Get("en"));
        Assert.Equal(TableRowMode.Mixed, updated.RowMode);
        Assert.Equal(50, updated.MaxDynamicRows);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Ordinal_не_переданий_явно_бере_наступний_за_наявними_на_цьому_аркуші()
    {
        await Save().HandleAsync(1, "Water", "First", Command(), CancellationToken.None);
        var second = await Save().HandleAsync(1, "Water", "Second", Command(), CancellationToken.None);

        Assert.Equal(1, second.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Перемикання_на_фіксовані_рядки_зі_стелею_відхиляється()
    {
        // ⛔ `SetMaxDynamicRows` викликається ПІСЛЯ `SetRowMode` саме тому, що
        // стеля має сенс лише там, де рядки додає користувач: перемикання на
        // `Fixed` зі стелею, що лишилася від `Dynamic`, має впасти, а не
        // мовчки зберегти нечинну комбінацію.
        await Save().HandleAsync(
            1, "Water", "Balances",
            Command(rowMode: TableRowMode.Dynamic, maxDynamicRows: 50), CancellationToken.None);

        var error = await Assert.ThrowsAsync<DomainException>(() => Save().HandleAsync(
            1, "Water", "Balances",
            Command(rowMode: TableRowMode.Fixed, maxDynamicRows: 50), CancellationToken.None));

        Assert.Equal("ECR-TMPL-0422", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Запис_таблиці_на_неіснуючому_аркуші_відхиляється_404()
    {
        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => Save().HandleAsync(1, "Missing", "Balances", Command(), CancellationToken.None));

        Assert.Equal("ECR-TMPL-0404", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-7.1")]
    public async Task Запис_таблиці_в_опублікованій_версії_відхиляється()
    {
        _draft.Publish(publishedByUserId: 8, utcNow: Now);

        // ⛔ Опублікована версія структурно незмінна: додавання чи зміна
        // таблиці тихо змінили б уже подані форми.
        var error = await Assert.ThrowsAsync<DomainException>(
            () => Save().HandleAsync(1, "Water", "Balances", Command(), CancellationToken.None));

        Assert.Equal("ECR-TMPL-0409", error.ErrorCode);
        Assert.Empty(_sheet.Tables);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-7.6")]
    public async Task Видалення_прибирає_таблицю_м_яко_а_не_фізично()
    {
        await Save().HandleAsync(1, "Water", "Balances", Command(), CancellationToken.None);
        _metadataCache.ClearReceivedCalls();

        await Delete().HandleAsync(1, "Water", "Balances", CancellationToken.None);

        // ⛔ Запис лишається: на нього можуть посилатися формули, правила
        // валідації й зв'язки між таблицями (ФВ-7.6).
        var table = Assert.Single(_sheet.Tables);
        Assert.True(table.IsDeleted);

        await _metadataCache.Received(1).InvalidateAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Видалення_неіснуючої_таблиці_відхиляється_404()
    {
        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => Delete().HandleAsync(1, "Water", "Missing", CancellationToken.None));

        Assert.Equal("ECR-TMPL-0404", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Видалення_на_неіснуючому_аркуші_відхиляється_404()
    {
        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => Delete().HandleAsync(1, "Missing", "Balances", CancellationToken.None));

        Assert.Equal("ECR-TMPL-0404", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-7.1")]
    public async Task Видалення_таблиці_в_опублікованій_версії_відхиляється()
    {
        await Save().HandleAsync(1, "Water", "Balances", Command(), CancellationToken.None);
        _draft.Publish(publishedByUserId: 8, utcNow: Now);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => Delete().HandleAsync(1, "Water", "Balances", CancellationToken.None));

        Assert.Equal("ECR-TMPL-0409", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Без_права_Template_Edit_таблиця_не_записується()
    {
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.View").Build());

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Save().HandleAsync(1, "Water", "Balances", Command(), CancellationToken.None));

        Assert.Empty(_sheet.Tables);
    }
}
