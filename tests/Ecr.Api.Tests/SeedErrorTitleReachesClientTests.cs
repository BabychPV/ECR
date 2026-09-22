using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.Api.Errors;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Errors;
using Ecr.TestKit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Заголовок, ЗАВЕДЕНИЙ У СІДІ, доїжджає до клієнта текстом, а не кодом.
/// </summary>
/// <remarks>
/// ⛔ Чим цей тест відрізняється від <c>LocalizedErrorTitleTests</c>. Той
/// доводить МЕХАНІЗМ і годує конвеєр рядком, написаним поруч у тесті, — тобто
/// лишався б зеленим і тоді, коли в <c>09-seed.sql</c> немає жодного заголовка
/// (саме так воно й було: 76 кодів проти 11 заголовків, і жоден тест цього не
/// бачив). Тут каталог будується з ФАЙЛУ сіду, а перевірка йде на коді, який
/// заголовка не мав.
///
/// ⚠ Доповнює сторожа <c>ErrorTitleCatalogTests</c>, а не дублює: той звіряє
/// два ТЕКСТИ (каталог констант і сід) і нічого не знає про конвеєр; цей
/// проганяє відмову крізь справжній <c>ExceptionHandlingMiddleware</c> і
/// читає <c>title</c> з тіла <c>problem+json</c> — рівно те поле, яке
/// <c>ErrorAlert.tsx</c> рендерить заголовком плашки.
///
/// ⚠ Бази не потребує: <c>DefaultHttpContext</c> і каталог у пам'яті,
/// наповнений розбором сіду.
/// </remarks>
public sealed partial class SeedErrorTitleReachesClientTests
{
    /// <summary>
    /// Код для перевірки: заводиться цією ж роботою і лежить на найчастішому
    /// шляху, де оператор упирається в бізнес-правило.
    /// </summary>
    private const string Code = ErrorCodes.PeriodClosed;

    private const string ExpectedTitle = "Period state conflict";

    /// <summary>Сире (серверне) речення обробника — воно має лишитися подробицею.</summary>
    private const string Detail = "Період 202601 закрито 2026-02-10.";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-14.9a")]
    public async Task Заголовок_із_сіду_стає_title_а_не_кодом()
    {
        var problem = await ProblemAsync(SeedCatalog());

        var title = problem.GetProperty("title").GetString();

        Assert.Equal(ExpectedTitle, title);

        // ⛔ Головне твердження — саме це: заголовком їде ТЕКСТ, а не код.
        // Рівність вище без нього читалася б як звірка рядків; дефект полягав
        // рівно в тому, що в цьому полі стояв `ECR-PRD-0409`.
        Assert.NotEqual(Code, title);

        // Код нікуди не дівається: клієнт розрізняє причини саме за ним.
        Assert.Equal(Code, problem.GetProperty("errorCode").GetString());

        // І подробиця лишається конкретною — заголовок її не витіснив
        // («щось пішло не так» заборонено, `07-checkpoints` Етап 6).
        Assert.Equal(Detail, problem.GetProperty("detail").GetString());
    }

    /// <summary>
    /// Той самий код без сіду — заголовком лишається сам код.
    /// </summary>
    /// <remarks>
    /// ⚠ Це не послаблення, а фіксація ДЕФЕКТУ, який знімає ця робота: так
    /// виглядала відповідь для 65 кодів із 76. Без цього твердження перевірка
    /// вище не доводила б, що текст прийшов саме з каталогу.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-14.9a")]
    public async Task Без_каталогу_заголовком_лишається_сам_код()
    {
        var problem = await ProblemAsync(new FakeUiStringCatalog());

        Assert.Equal(Code, problem.GetProperty("title").GetString());
    }

    /// <summary>
    /// Каталог у пам'яті, наповнений рядками <c>err.&lt;код&gt;</c> зі
    /// СПРАВЖНЬОГО <c>09-seed.sql</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ Береться файл, а не копія рядка в тесті: копія доводила б лише те, що
    /// <c>FakeUiStringCatalog</c> віддає покладене. Рядки з суфіксом у ключі
    /// сюди не потрапляють навмисно — <c>LocalizedTitleAsync</c> їх не читає.
    /// </remarks>
    private static FakeUiStringCatalog SeedCatalog()
    {
        var path = Path.Combine(
            RepoRoot(), "src", "Ecr.Infrastructure", "Persistence", "Sql", "09-seed.sql");

        Assert.True(File.Exists(path), $"Не знайдено {path}");

        var catalog = new FakeUiStringCatalog();
        var rows = SeedRow().Matches(File.ReadAllText(path));

        // ⛔ Порожній розбір означав би, що сід змінив форму рядка, а тест про
        // це мовчить: далі він годував би конвеєр порожнім каталогом і
        // перевіряв би запасний шлях замість заявленого.
        Assert.NotEmpty(rows);

        foreach (Match row in rows)
        {
            catalog.Add(
                row.Groups[2].Value,
                row.Groups[1].Value,
                row.Groups[3].Value.Replace("''", "'", StringComparison.Ordinal),
                row.Groups[4].Value == "0" ? UiStringScope.Public : UiStringScope.Private);
        }

        return catalog;
    }

    /// <summary>Рядок сіду <c>(N'err.ECR-…', N'en', N'…', 0|1)</c> без суфікса в ключі.</summary>
    [GeneratedRegex(
        @"\(\s*N'(err\.ECR-[A-Z]{3,4}-\d{4})'\s*,\s*N'(\w+)'\s*,\s*N'((?:[^']|'')*)'\s*,\s*([01])\s*\)")]
    private static partial Regex SeedRow();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Ecr.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException(
                $"Корінь репозиторію (Ecr.sln) не знайдено від {AppContext.BaseDirectory}.");
    }

    /// <summary>Проганяє відмову крізь конвеєр і повертає тіло <c>problem+json</c>.</summary>
    private static async Task<JsonElement> ProblemAsync(IUiStringCatalog catalog)
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.Language.Returns("en");

        var services = new ServiceCollection();
        services.AddSingleton(catalog);
        services.AddSingleton(currentUser);

        using var provider = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = provider };
        using var body = new MemoryStream();
        context.Response.Body = body;

        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new BusinessRuleException(Code, Detail),
            NullLogger<ExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        body.Position = 0;
        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
