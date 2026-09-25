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
/// Авторство структури шаблону — другий вертикальний зріз: колонка таблиці
/// (<c>ФВ-2.1</c>..<c>ФВ-2.5</c>, <c>W5.2</c>), за зразком
/// <see cref="SheetDefTests"/> (<c>W5.0</c>).
/// </summary>
/// <remarks>
/// ⚠ Таблиця тут заводиться НАПРЯМУ через доменний конструктор
/// (<see cref="TemplateBuilder"/>), а не через HTTP: створення таблиці —
/// окремий, паралельний зріз (<c>W5.1</c>), і ці тести його не чіпають —
/// достатньо, що таблиця вже існує в графі версії.
/// </remarks>
public sealed class ColumnDefTests
{
    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly ITemplateVersionStore _store = Substitute.For<ITemplateVersionStore>();
    private readonly IMetadataCache _metadataCache = Substitute.For<IMetadataCache>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    private readonly TemplateBuilder _builder = new() { TemplateVersionId = 1 };

    // ⚠ Не мок: обробник читає й пише через ЦЕЙ агрегат (`GetWithStructureAsync`
    // повертає його самого) — так само, як `_draft` у `SheetDefTests`.
    private readonly TemplateVersion _draft =
        new(templateId: 1, version: "1.0.0.0", createdByUserId: 7, utcNow: Now);

    private readonly TableDef _table;

    public ColumnDefTests()
    {
        // ⛔ Q-244: обробники тепер виконують запис/аудит/SaveChanges через
        // IUnitOfWork.ExecuteInTransactionAsync(Func<CancellationToken, Task>, ...).
        // Без цього налаштування NSubstitute ніколи не викликає передане
        // замикання — жодна перевірка нижче не виконалась би насправді.
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));

        var sheet = _builder.Sheet("Water");
        _table = _builder.Table(sheet, "Main");
        _draft.AddSheet(sheet);

        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.Edit").Build());

        _store.GetWithStructureAsync(1, Arg.Any<CancellationToken>()).Returns(_draft);
        _store.HasDocumentsAsync(1, Arg.Any<CancellationToken>()).Returns(false);
    }

    private static Dictionary<string, string> Header(string en) => new(StringComparer.OrdinalIgnoreCase) { ["en"] = en };

    private static SaveColumnDefCommand Command(
        string en = "January", int? ordinal = null, CellDataType dataType = CellDataType.Decimal,
        bool required = false, bool readOnly = false, bool hidden = false,
        byte? precision = null, byte? scale = null,
        string? defaultValue = null, string? displayFormat = null, int? styleId = null,
        int? lookupRegistryDefId = null, string? lookupFilter = null, int? unitId = null)
        => new(
            Header(en), ordinal, dataType, required, readOnly, hidden,
            precision, scale, defaultValue, displayFormat, styleId,
            lookupRegistryDefId, lookupFilter, unitId);

    private SaveColumnDefHandler Save()
        => new(_store, new ChangeClassifier(), _metadataCache, _audit, _uow, _clock, _access, _user);

    private DeleteColumnDefHandler Delete()
        => new(_store, new ChangeClassifier(), _metadataCache, _audit, _uow, _clock, _access, _user);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-2.1")]
    public async Task Нова_колонка_зберігається_і_потрапляє_в_аудит_структурних_змін()
    {
        var saved = await Save().HandleAsync(1, _table.Id, "Jan", Command(), CancellationToken.None);

        Assert.Equal("Jan", saved.Code);
        Assert.Equal(0, saved.Ordinal);
        Assert.Equal(CellDataType.Decimal, saved.DataType);
        Assert.Single(_table.Columns);

        await _audit.Received(1).WriteStructureChangeAsync(
            Arg.Is<StructureChangeRecord>(r => r.EntityType == "ColumnDef" && r.Operation == "Create"),
            Arg.Any<CancellationToken>());

        // ⛔ Без цього виклику прогрітий кеш метаданих віддавав би структуру
        // без щойно доданої колонки — див. коментар у `SaveColumnDefHandler`.
        await _metadataCache.Received(1).InvalidateAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-2.1")]
    public async Task Повторний_запис_тим_самим_кодом_оновлює_а_не_дублює()
    {
        await Save().HandleAsync(1, _table.Id, "Jan", Command(en: "January v1"), CancellationToken.None);
        var updated = await Save().HandleAsync(
            1, _table.Id, "Jan", Command(en: "January v2", required: true), CancellationToken.None);

        // ⛔ Ідентичність — код: другий запис тим самим кодом ЗМІНЮЄ наявну
        // колонку, а не заводить другу (`D2-147`).
        Assert.Single(_table.Columns);
        Assert.Equal("January v2", updated.HeaderL10n.Get("en"));
        Assert.True(updated.IsRequired);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-2.1")]
    public async Task Ordinal_не_переданий_явно_бере_наступний_за_наявними()
    {
        await Save().HandleAsync(1, _table.Id, "First", Command(), CancellationToken.None);
        var second = await Save().HandleAsync(1, _table.Id, "Second", Command(), CancellationToken.None);

        Assert.Equal(1, second.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Тип_даних_незмінний_повторним_записом()
    {
        await Save().HandleAsync(1, _table.Id, "Jan", Command(dataType: CellDataType.Decimal), CancellationToken.None);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().HandleAsync(1, _table.Id, "Jan", Command(dataType: CellDataType.String), CancellationToken.None));

        Assert.Equal("ECR-TMPL-0422", error.ErrorCode);
        Assert.Equal("err.ECR-TMPL-0422.columnDataTypeImmutable", error.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Довідник_на_колонці_не_типу_Lookup_відхиляється_доменом()
    {
        var error = await Assert.ThrowsAsync<DomainException>(
            () => Save().HandleAsync(
                1, _table.Id, "Jan", Command(dataType: CellDataType.Decimal, lookupRegistryDefId: 5),
                CancellationToken.None));

        Assert.Equal("ECR-TMPL-0422", error.ErrorCode);
        Assert.Equal("err.ECR-TMPL-0422.lookupRequiresLookupType", error.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Таблиці_з_таким_Id_у_версії_немає()
    {
        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => Save().HandleAsync(1, tableDefId: 999, "Jan", Command(), CancellationToken.None));

        Assert.Equal("ECR-TMPL-0404", error.ErrorCode);
        Assert.Equal("err.ECR-TMPL-0404.table", error.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-7.1")]
    public async Task Запис_колонки_в_опублікованій_версії_відхиляється()
    {
        _draft.Publish(publishedByUserId: 8, utcNow: Now);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => Save().HandleAsync(1, _table.Id, "Jan", Command(), CancellationToken.None));

        Assert.Equal("ECR-TMPL-0409", error.ErrorCode);
        Assert.Empty(_table.Columns);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-7.6")]
    public async Task Видалення_прибирає_колонку_м_яко_а_не_фізично()
    {
        await Save().HandleAsync(1, _table.Id, "Jan", Command(), CancellationToken.None);
        _metadataCache.ClearReceivedCalls();

        await Delete().HandleAsync(1, _table.Id, "Jan", CancellationToken.None);

        var column = Assert.Single(_table.Columns);
        Assert.True(column.IsDeleted);

        await _metadataCache.Received(1).InvalidateAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Видалення_неіснуючої_колонки_відхиляється_404()
    {
        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => Delete().HandleAsync(1, _table.Id, "Missing", CancellationToken.None));

        Assert.Equal("ECR-TMPL-0404", error.ErrorCode);
        Assert.Equal("err.ECR-TMPL-0404.columnCode", error.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-7.1")]
    public async Task Видалення_колонки_в_опублікованій_версії_відхиляється()
    {
        await Save().HandleAsync(1, _table.Id, "Jan", Command(), CancellationToken.None);
        _draft.Publish(publishedByUserId: 8, utcNow: Now);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => Delete().HandleAsync(1, _table.Id, "Jan", CancellationToken.None));

        Assert.Equal("ECR-TMPL-0409", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Код_видаленої_колонки_дає_ЗРОЗУМІЛУ_відмову_а_не_помилку_про_тип()
    {
        // ⛔ Аудит 2026-09-16, §4.4. Пошук наявної колонки йшов без фільтра
        // `!IsDeleted`, тож `PUT` за кодом видаленої колонки тихо потрапляв у
        // гілку ОНОВЛЕННЯ мертвої сутності. Найяскравіше це було видно саме тут:
        // користувач, який видалив Decimal-колонку `LIMIT` і хоче завести нову
        // String-колонку з тим самим кодом, отримував `ECR-TMPL-0422` про
        // незмінність типу МЕРТВОЇ колонки — помилку, у якій немає жодної
        // підказки, що робити.
        //
        // ⚠ Рішення: ЗАБОРОНА З ПОЯСНЕННЯМ, а не «оживлення». Код — ідентичність,
        // і `TableDef.AddColumn` тримає його унікальним включно з м'яко
        // видаленими; операції відродження в системі немає, і вигадувати її в
        // цьому фіксі було б рішенням про семантику даних, а не про формулювання
        // помилки.
        await Save().HandleAsync(
            1, _table.Id, "LIMIT", Command(dataType: CellDataType.Decimal), CancellationToken.None);

        await Delete().HandleAsync(1, _table.Id, "LIMIT", CancellationToken.None);
        Assert.True(Assert.Single(_table.Columns).IsDeleted);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().HandleAsync(
                1, _table.Id, "LIMIT", Command(dataType: CellDataType.String), CancellationToken.None));

        Assert.Equal("ECR-TMPL-0422", error.ErrorCode);
        Assert.Equal("err.ECR-TMPL-0422.columnCodeTakenByDeleted", error.Details!["messageKey"]);

        // ⛔ Головне: повідомлення каже про ВИДАЛЕНУ колонку, а не про
        // «незмінність типу» — саме заміна цього тексту і є фіксом.
        Assert.Contains("видаленою колонкою", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("незмінний", error.Message, StringComparison.Ordinal);

        // І нічого не змінилося: мертва колонка лишилася мертвою, живої немає.
        Assert.True(Assert.Single(_table.Columns).IsDeleted);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Повторний_запис_за_кодом_живої_колонки_і_далі_оновлює_її()
    {
        // Зворотний бік §4.4: фільтр `!IsDeleted` не має перетворити звичайне
        // оновлення на дублювання — саме це й перевіряє пара з тестом вище.
        await Save().HandleAsync(1, _table.Id, "Jan", Command(en: "v1"), CancellationToken.None);
        await Save().HandleAsync(1, _table.Id, "Jan", Command(en: "v2"), CancellationToken.None);

        var column = Assert.Single(_table.Columns);
        Assert.Equal("v2", column.HeaderL10n.Get("en"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Без_права_Template_Edit_колонка_не_записується()
    {
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.View").Build());

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Save().HandleAsync(1, _table.Id, "Jan", Command(), CancellationToken.None));

        Assert.Empty(_table.Columns);
    }

    private GetColumnDefHandler Get()
        => new(_store, _access, _user);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-2.1")]
    public async Task Читання_колонки_віддає_всі_поля_які_приймає_PUT()
    {
        // ⛔ X-02: форма правки будувала чернетку з бідного опису структури, і
        // повторний PUT (заміна цілком) стирав точність, одиницю, значення за
        // замовчуванням і стиль. Читання мусить віддавати рівно те, що PUT
        // приймає, — інакше форма знову не матиме звідки їх узяти.
        await Save().HandleAsync(
            1, _table.Id, "Limit",
            Command(precision: 18, scale: 4, defaultValue: "0", displayFormat: "N2", styleId: 3, unitId: 12),
            CancellationToken.None);

        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.View").Build());

        var read = await Get().HandleAsync(1, _table.Id, "Limit", CancellationToken.None);

        Assert.Equal("Limit", read.Code);
        Assert.Equal((byte)18, read.Precision);
        Assert.Equal((byte)4, read.Scale);
        Assert.Equal("0", read.DefaultValue);
        Assert.Equal("N2", read.DisplayFormat);
        Assert.Equal(3, read.StyleId);
        Assert.Equal(12, read.UnitId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Читання_колонки_Lookup_віддає_довідник_і_фільтр()
    {
        await Save().HandleAsync(
            1, _table.Id, "Substance",
            Command(dataType: CellDataType.Lookup, lookupRegistryDefId: 5, lookupFilter: "kind=gas"),
            CancellationToken.None);

        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.View").Build());

        var read = await Get().HandleAsync(1, _table.Id, "Substance", CancellationToken.None);

        Assert.Equal(5, read.LookupRegistryDefId);
        Assert.Equal("kind=gas", read.LookupFilter);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Читання_видаленої_колонки_404()
    {
        await Save().HandleAsync(1, _table.Id, "Gone", Command(), CancellationToken.None);
        await Delete().HandleAsync(1, _table.Id, "Gone", CancellationToken.None);

        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.View").Build());

        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => Get().HandleAsync(1, _table.Id, "Gone", CancellationToken.None));

        Assert.Equal("err.ECR-TMPL-0404.columnCode", error.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Без_права_Template_View_колонка_не_читається()
    {
        await Save().HandleAsync(1, _table.Id, "Jan", Command(), CancellationToken.None);

        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Build());

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Get().HandleAsync(1, _table.Id, "Jan", CancellationToken.None));
    }
}
