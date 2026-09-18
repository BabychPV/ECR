using System.Text.Json;
using Ecr.Api.Errors;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Відмова «не знайдено» (<c>NotFoundException</c>, HTTP 404) доїжджає до
/// клієнта МОВОЮ КОРИСТУВАЧА, а не готовим українським реченням (`Q-341`).
/// </summary>
/// <remarks>
/// ⛔ Предмет ширший за один обробник. До цієї роботи 404 не піддавалися
/// локалізації СТРУКТУРНО, і зламано це було у двох місцях одразу:
/// (1) у <c>NotFoundException</c> не було параметра <c>Details</c>, тож
/// <c>messageKey</c> нікуди було покласти; (2) арм 404 у
/// <c>ExceptionHandlingMiddleware.Map</c> віддавав жорстку <c>null</c> замість
/// <c>e.Details</c> — тобто навіть принесений ключ відкидався на рядок раніше
/// за єдине місце, яке його читає. Цей тест червоніє, якщо повернути БУДЬ-ЯКУ
/// з двох половин.
///
/// ⛔ Прогін іде крізь РЕАЛЬНИЙ обробник і РЕАЛЬНИЙ конвеєр, а не відтворює
/// кидок літералом поруч: відтворення довело б лише те, що механізм
/// `messageKey` працює (це вже доводить <c>GenericMessageKeyLocalizationTests</c>),
/// і лишилося б зеленим, якби ключ із самого обробника прибрали.
///
/// ⚠ Обрано <c>GetTableSliceHandler</c> — ВІДКРИТТЯ сітки. Це найчастіший
/// шлях, на якому оператор узагалі може побачити 404 (`ECR-DOC-0404`).
/// </remarks>
public sealed class NotFoundLocalizedErrorTests
{
    private const long TableInstance = 500;
    private const long RouteDocument = 701;
    private const long RealDocument = 700;
    private const int Period = 202601;

    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IUnitCatalog _units = Substitute.For<IUnitCatalog>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IMethodologyStore _methodologies = Substitute.For<IMethodologyStore>();
    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();
    private readonly IStyleCatalog _styles = Substitute.For<IStyleCatalog>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public NotFoundLocalizedErrorTests()
    {
        _user.UserId.Returns(9);
        _user.Language.Returns("en");

        // Екземпляр таблиці існує, але належить ІНШОМУ документу, ніж той, що
        // в маршруті, — типовий застарілий укладач у вкладці браузера.
        _rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
             .Returns(new TableInstanceRef(
                 TableInstance, DocumentId: RealDocument, TableDefId: 3,
                 TemplateVersionId: 2, PeriodKey: Period));

        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(EditDecision.Allow());
    }

    private static AccessProfile Profile() => new()
    {
        CacheKey = "p1", UserId = 9, SecurityStamp = "s",
        Permissions = new HashSet<string>(StringComparer.Ordinal) { "Document.View" },
        Grants = new Dictionary<string, GrantLevel>(),
        Denies = new HashSet<string>(), RoleIds = new HashSet<int>()
    };

    private GetTableSliceHandler Handler()
        => new(_rows, _cells, _metadata, _units, _access, _methodologies, _periods, _styles);

    /// <summary>Каталог рівно з тим ключем, який заводить `09-seed.sql`.</summary>
    private static FakeUiStringCatalog Catalog()
        => new FakeUiStringCatalog()
            .Add(
                "en", "err.ECR-DOC-0404.tableInstanceNotInDocument",
                "Table instance {tableInstanceId} does not belong to document {documentId}.",
                UiStringScope.Private);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Чужий_екземпляр_таблиці_доїжджає_англійською_з_обома_ідентифікаторами()
    {
        var problem = await ProblemAsync(() => Handler().HandleAsync(
            RouteDocument, TableInstance, Profile(), "en", CancellationToken.None));

        var detail = problem.GetProperty("detail").GetString();

        Assert.Equal("Table instance 500 does not belong to document 701.", detail);
        AssertNoCyrillic(detail);

        // Код і статус не змінилися: локалізація подробиці не має права
        // перевизначати контракт (`02-contracts.md` §7).
        Assert.Equal(404, problem.GetProperty("status").GetInt32());
        Assert.Equal("ECR-DOC-0404", problem.GetProperty("errorCode").GetString());
    }

    /// <summary>
    /// Ключа немає в каталозі — лишається сире (українське) речення обробника.
    /// </summary>
    /// <remarks>
    /// ⚠ Це НЕ послаблення перевірки вище, а фіксація запасного шляху:
    /// `ResolveGenericMessageAsync` навмисно віддає перевагу написаному
    /// розробником реченню перед показом самого ключа. Саме тому українські
    /// речення в кидках ЛИШИЛИСЯ, і саме тому параметр `Details` у
    /// `NotFoundException` необов'язковий.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Порожній_каталог_лишає_запасне_українське_речення()
    {
        var problem = await ProblemAsync(
            () => Handler().HandleAsync(
                RouteDocument, TableInstance, Profile(), "en", CancellationToken.None),
            new FakeUiStringCatalog());

        Assert.Equal(
            "Екземпляр таблиці 500 не належить документу 701.",
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
