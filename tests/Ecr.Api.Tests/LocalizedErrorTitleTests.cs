using System.Text.Json;
using Ecr.Api.Errors;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.TestKit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Текст помилки береться з каталогу рядків (<c>ФВ-14.9a</c>, <c>D-111</c>).
/// </summary>
/// <remarks>
/// ⛔ Механізм локалізації один, не два: повідомлення каталогу помилок — це
/// записи того самого <c>sys.UiString</c> під ключем <c>err.&lt;код&gt;</c>, і
/// <b>резолвить їх сервер</b>. Обидві половини механізму існували від Етапу 3 —
/// п'ять ключів <c>err.ECR-…</c> у <c>09-seed.sql</c> і
/// <c>UiStringResolver.ResolveError</c> із власним тестом, — а сполучної ланки
/// між ними не було. Наслідок мовчазний: ключі не читав НІХТО, заголовком
/// помилки їхав сам код, і користувач бачив «ECR-AUTH-0423» замість «обліковий
/// запис заблоковано», причому будь-якою мовою однаково.
///
/// ⚠ Бази ці тести не потребують: конвеєр помилок перевіряється на
/// <c>DefaultHttpContext</c> із підробленим каталогом.
/// </remarks>
public sealed class LocalizedErrorTitleTests
{
    private const string Code = "ECR-AUTH-0423";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-14.9a")]
    public async Task Заголовок_береться_з_ключа_err_код()
    {
        var catalog = new FakeUiStringCatalog()
            .Add("en", "err." + Code, "The account is locked.", UiStringScope.Public);

        var problem = await ProblemAsync(catalog, "en");

        Assert.Equal("The account is locked.", problem.GetProperty("title").GetString());

        // ⚠ Код нікуди не дівається і лишається СТАБІЛЬНИМ: клієнт розрізняє
        // причини саме за ним, а не за текстом, який щойно став змінним.
        Assert.Equal(Code, problem.GetProperty("errorCode").GetString());

        // ⚠ `Detail` теж лишається: каталог дає постійний текст на код, а
        // конкретику («обліковий запис ivanov») несе сервер. «Щось пішло не
        // так» заборонено (`07-checkpoints`, Етап 6).
        Assert.Equal("Обліковий запис заблоковано до 12:00.", problem.GetProperty("detail").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-14.9")]
    public async Task Переклад_береться_мовою_запиту()
    {
        // ⛔ Заради цього рядок і живе в реєстрі, а не в збірці (`ФВ-14.9`,
        // `D-95`): додати мову має означати запис у каталог, а не реліз.
        var catalog = new FakeUiStringCatalog()
            .Add("en", "err." + Code, "The account is locked.", UiStringScope.Public)
            .Add("ru", "err." + Code, "Учётная запись заблокирована.", UiStringScope.Public);

        var problem = await ProblemAsync(catalog, "ru");

        Assert.Equal("Учётная запись заблокирована.", problem.GetProperty("title").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-14.9a")]
    public async Task Без_ключа_заголовком_лишається_сам_код()
    {
        // ⛔ `UiStringResolver` підставляє САМ КЛЮЧ, коли перекладу немає ніде
        // — для підпису кнопки це краще за порожнечу, але заголовок
        // «err.ECR-AUTH-0423» гірший за код, який принаймні названий у
        // контракті й придатний для звернення в підтримку.
        //
        // ⚠ Це і є та поблажливість, яка дозволяє заводити переклади
        // поступово: каталог помилок має 59 кодів, а термінолог (`C-7`) ще не
        // прийшов. Незаведений ключ нічого не ламає.
        var problem = await ProblemAsync(new FakeUiStringCatalog(), "en");

        Assert.Equal(Code, problem.GetProperty("title").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Збій_каталогу_не_ламає_відповідь_про_помилку()
    {
        // ⛔ Це обробник ПОМИЛОК. 500-ті трапляються масово саме тоді, коли
        // база недоступна, — і похід за перекладом кинув би вдруге, уже поза
        // `try` конвеєра. Клієнт замість `problem+json` отримав би обірване
        // з'єднання, тобто найгірший з можливих варіантів: помилка без коду,
        // без кореляції і без пояснення.
        var broken = Substitute.For<IUiStringCatalog>();
        broken.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns<Task<UiStringCatalog>>(_ => throw new InvalidOperationException("база недоступна"));

        var problem = await ProblemAsync(broken, "en");

        Assert.Equal(Code, problem.GetProperty("title").GetString());
        Assert.Equal(Code, problem.GetProperty("errorCode").GetString());
    }

    /// <summary>Проганяє відмову через конвеєр і повертає тіло <c>problem+json</c>.</summary>
    private static async Task<JsonElement> ProblemAsync(IUiStringCatalog catalog, string language)
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.Language.Returns(language);

        var services = new ServiceCollection();
        services.AddSingleton(catalog);
        services.AddSingleton(currentUser);

        using var provider = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = provider };
        using var body = new MemoryStream();
        context.Response.Body = body;

        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new BusinessRuleException(Code, "Обліковий запис заблоковано до 12:00."),
            NullLogger<ExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        body.Position = 0;
        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
