using System.Text.Json;
using Ecr.Api.Errors;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
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
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Відмови збереження комірки (<c>PatchCellsHandler</c>) доїжджають до клієнта
/// МОВОЮ КОРИСТУВАЧА, а не готовим українським реченням (`Q-341`).
/// </summary>
/// <remarks>
/// ⛔ Тест проганяє РЕАЛЬНИЙ обробник крізь РЕАЛЬНИЙ
/// <see cref="ExceptionHandlingMiddleware"/>, а не відтворює кидок літералом
/// поруч. Відтворення довело б лише те, що механізм `messageKey` працює (це вже
/// доводить <c>GenericMessageKeyLocalizationTests</c>) — і лишилося б зеленим,
/// якби ключ із самого обробника прибрали. Предмет саме в тому, що ключ несе
/// обробник.
///
/// ⚠ Мови продукту — `en`/`ru`/`kz`; української серед них немає взагалі. Тому
/// перевірка «немає кирилиці» — не косметика: кирилиця в <c>detail</c> означає,
/// що користувач бачить речення мовою, якої в переліку мов продукту не існує,
/// поруч із уже локалізованим <c>title</c> (`D-95`).
/// </remarks>
public sealed class PatchCellsLocalizedErrorTests
{
    private const long TableInstance = 500;
    private const int Period = 202601;
    private const int VolumeColumnId = 11;
    private const string RowKeyValue = "7001001";

    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly IDocumentStore _documents = Substitute.For<IDocumentStore>();
    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IMethodologyStore _methodologies = Substitute.For<IMethodologyStore>();
    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();

    /// <summary>Шапка документа — тести цього файлу її не читають.</summary>
    private readonly IDocumentHeaderStore _headers = CreateHeaderStore();

    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();

    /// <summary>Читач журналу — джерело автора й часу чужої правки (`BE-06`).</summary>
    private readonly IAuditReader _auditReader = Substitute.For<IAuditReader>();
    private readonly IBackgroundJobScheduler _jobs = Substitute.For<IBackgroundJobScheduler>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public PatchCellsLocalizedErrorTests()
    {
        var column = new ColumnDef(
            tableDefId: 3, EcrCode.Create("Volume"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Volume" }), 1, CellDataType.Decimal);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(column, VolumeColumnId);

        var sheet = new SheetDef(
            templateVersionId: 2, EcrCode.Create("Water"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Water" }), 1);

        var table = new TableDef(
            sheetDefId: 1, EcrCode.Create("Main"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Main" }), 1,
            TableLayoutKind.MonthsInColumns, TableRowMode.Dynamic);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(table, 3);
        table.AddColumn(column);
        sheet.AddTable(table);

        var snapshot = new TemplateVersionSnapshot(
            TemplateVersionId: 2, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: new Dictionary<int, ColumnDef> { [VolumeColumnId] = column },
            RowsByKey: new Dictionary<(int, string), RowDef>());

        _user.UserId.Returns(9);
        _user.Language.Returns("en");

        // ⛔ Годинник тепер МАЄ бути заданий, і це не косметика фікстури.
        // `BE-06` рахує від нього вікно журналу (`UtcNow.AddMonths(-13)`), а
        // непіднастроєний `Substitute` віддає `DateTime.MinValue` — відняти від
        // якого тринадцять місяців неможливо. Наслідок був видимий рівно тут:
        // замість локалізованого `ECR-CELL-0409` приїжджала «внутрішня
        // помилка», тобто 500 замість 409.
        _clock.UtcNow.Returns(new DateTime(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc));
        _rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
             .Returns(new TableInstanceRef(
                 TableInstance, DocumentId: 700, TableDefId: 3, TemplateVersionId: 2, PeriodKey: Period));
        _metadata.GetAsync(2, Arg.Any<CancellationToken>()).Returns(snapshot);
        _methodologies.GetMethodologyIdsBoundToTableAsync(3, Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult<IReadOnlyList<int>>([]));
        _rows.GetRowVersionsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, string> { [RowKeyValue] = "0x0A" });
        _rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, long> { [RowKeyValue] = 1001L });
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(Profile());
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());
        _access.CanEditSliceAsync(Arg.Any<AccessProfile>(), TableInstance, Arg.Any<CancellationToken>())
               .Returns(new Dictionary<CellAddress, EditDecision>());
    }

    private static AccessProfile Profile() => new()
    {
        CacheKey = "p1", UserId = 9, SecurityStamp = "s",
        Permissions = new HashSet<string>(), Grants = new Dictionary<string, GrantLevel>(),
        Denies = new HashSet<string>(), RoleIds = new HashSet<int>()
    };

    private PatchCellsHandler Handler()
        => new(_cells, _rows, _documents, _periods, _metadata, _access,
               new ValidationEngine(new RealFormulaEngine()),
               _methodologies, _registries, _headers, _audit, _auditReader, _jobs, _uow, _user, _clock,
               Substitute.For<ISheetEditGate>());

    private static IDocumentHeaderStore CreateHeaderStore()
    {
        var store = Substitute.For<IDocumentHeaderStore>();
        store.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ExpressionValue>());
        return store;
    }

    /// <summary>Каталог із рівно тими ключами, які заводить `09-seed.sql`.</summary>
    private static FakeUiStringCatalog Catalog()
        => new FakeUiStringCatalog()
            .Add(
                "en", "err.ECR-AUTH-0401.anonymousWrite",
                "An anonymous request cannot change data: sign in again.", UiStringScope.Private)
            .Add(
                "en", "err.ECR-CELL-0422.unknownColumn",
                "There is no column \"{columnCode}\" in this template version.", UiStringScope.Private)
            .Add(
                "en", "err.ECR-CELL-0409.batchStale",
                "The batch was rejected: {rowCount} row(s) changed since you loaded them.", UiStringScope.Private);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Невідома_колонка_доїжджає_англійською_з_підстановкою_коду_колонки()
    {
        var problem = await ProblemAsync(() => Handler().HandleAsync(
            new PatchCellsRequest(
                TableInstance, Period, "UserEdit",
                [new PatchRow(RowKeyValue, "0x0A", [new PatchCell("ZZZ", 1m)])]),
            CancellationToken.None));

        var detail = problem.GetProperty("detail").GetString();

        Assert.Equal("There is no column \"ZZZ\" in this template version.", detail);
        AssertNoCyrillic(detail);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Розбіжність_версії_рядка_доїжджає_англійською_з_кількістю_рядків()
    {
        // Клієнт заявив версію, якої в базі вже немає, — типова гонка двох
        // аналітиків в останній день періоду.
        var problem = await ProblemAsync(() => Handler().HandleAsync(
            new PatchCellsRequest(
                TableInstance, Period, "UserEdit",
                [new PatchRow(RowKeyValue, "0xSTALE", [new PatchCell("Volume", 1m)])]),
            CancellationToken.None));

        var detail = problem.GetProperty("detail").GetString();

        Assert.Equal("The batch was rejected: 1 row(s) changed since you loaded them.", detail);
        AssertNoCyrillic(detail);

        // ⚠ Структурована подробиця лишається на місці: клієнт показує
        // розбіжності по рядках саме з неї (`client.ts`, `conflicts`), і
        // локалізація тексту не має права її витіснити.
        Assert.NotEqual(0, problem.GetProperty("conflicts").GetArrayLength());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Анонімний_запит_доїжджає_англійською()
    {
        _user.UserId.Returns((int?)null);

        var problem = await ProblemAsync(() => Handler().HandleAsync(
            new PatchCellsRequest(
                TableInstance, Period, "UserEdit",
                [new PatchRow(RowKeyValue, "0x0A", [new PatchCell("Volume", 1m)])]),
            CancellationToken.None));

        var detail = problem.GetProperty("detail").GetString();

        Assert.Equal("An anonymous request cannot change data: sign in again.", detail);
        AssertNoCyrillic(detail);
    }

    /// <summary>
    /// Відмова на заборонену комірку не несе в подробицях <c>detail</c> — і в
    /// тілі відповіді рівно один <c>detail</c>, англійський (B-06).
    /// </summary>
    /// <remarks>
    /// ⚠ Перша половина дивиться на ВИНЯТОК обробника, а не на JSON: конвеєр
    /// тепер сам відкидає зарезервовані імена (<c>ProblemReservedMembersTests</c>),
    /// тож без неї повернення <c>["detail"]</c> в обробник лишилося б невидимим.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Заборонена_комірка_не_кладе_detail_у_подробиці_і_доїжджає_англійською()
    {
        // `CanEditSliceAsync` повертає порожній словник — рішення на комірку
        // немає, отже відмова `NoGrant` з українським `Detail` рішення.
        var request = new PatchCellsRequest(
            TableInstance, Period, "UserEdit",
            [new PatchRow(RowKeyValue, "0x0A", [new PatchCell("Volume", 1m)])]);

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(
            () => Handler().HandleAsync(request, CancellationToken.None));

        Assert.Equal("err.ECR-ACCS-0403.deniedCells", denied.Details!["messageKey"]);
        Assert.False(denied.Details.ContainsKey("detail"), "Подробиці несуть зарезервований член `detail`.");

        var body = await ProblemReservedMembersTests.ProblemTextAsync(denied);

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(body, "\"detail\""));
        Assert.Equal(
            "Cells you may not edit in this batch: 1. Reason for the first: NoGrant.",
            JsonDocument.Parse(body).RootElement.GetProperty("detail").GetString());
    }

    /// <summary>
    /// Ключа немає в каталозі — лишається сире (українське) речення обробника.
    /// </summary>
    /// <remarks>
    /// ⚠ Це НЕ послаблення попередніх перевірок, а фіксація запасного шляху:
    /// `ResolveGenericMessageAsync` навмисно віддає перевагу написаному
    /// розробником реченню перед показом самого ключа. Обробник цього права
    /// не втрачає — саме тому українські речення в ньому ЛИШИЛИСЯ.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Порожній_каталог_лишає_запасне_українське_речення()
    {
        var problem = await ProblemAsync(
            () => Handler().HandleAsync(
                new PatchCellsRequest(
                    TableInstance, Period, "UserEdit",
                    [new PatchRow(RowKeyValue, "0x0A", [new PatchCell("ZZZ", 1m)])]),
                CancellationToken.None),
            new FakeUiStringCatalog());

        Assert.Equal(
            "Колонки з кодом 'ZZZ' немає в структурі версії.",
            problem.GetProperty("detail").GetString());
    }

    private static void AssertNoCyrillic(string? text)
    {
        Assert.NotNull(text);
        Assert.DoesNotMatch("[а-яА-ЯіІїЇєЄ]", text);
    }

    /// <summary>Проганяє відмову обробника через конвеєр і повертає `problem+json`.</summary>
    private async Task<JsonElement> ProblemAsync(Func<Task> act, IUiStringCatalog? catalog = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(catalog ?? Catalog());
        services.AddSingleton(_user);

        using var provider = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = provider };
        using var body = new MemoryStream();
        context.Response.Body = body;

        var middleware = new ExceptionHandlingMiddleware(
            _ => act(), NullLogger<ExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        body.Position = 0;
        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
