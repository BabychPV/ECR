// tests/Ecr.Api.Tests/ProblemReservedMembersTests.cs
using System.Text.Json;
using Ecr.Api.Errors;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.TestKit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Зарезервовані члени <c>problem+json</c> не дублюються подробицями винятку
/// (B-06, UX-прохід, четвертий раунд).
/// </summary>
/// <remarks>
/// ⛔ Що бачили аналітики: у тілі відмови ДВА ключі <c>detail</c> — перший
/// локалізований конвеєром, другий скопійований із <c>Details["detail"]</c>
/// обробника (українське речення або <c>null</c>). <c>JSON.parse</c> бере
/// останній, тож на екрані їхав саме другий. Тест дивиться на СИРИЙ текст
/// відповіді, а не на <see cref="JsonDocument"/>: розбір мовчки лишає один із
/// дублікатів і довів би не те.
/// </remarks>
public sealed class ProblemReservedMembersTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Подробиці_з_іменами_стандартних_членів_не_дублюють_і_не_переписують_їх()
    {
        var body = await ProblemTextAsync(new AccessDeniedException(
            "ECR-ACCS-0403",
            "Заборонених комірок у батчі: 1. Причина першої: NoGrant.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-ACCS-0403.deniedCells",
                ["deniedCount"] = "1",
                ["reason"] = "NoGrant",
                ["detail"] = "Рішення про доступ на комірку рядка 1001 не отримано.",
                ["title"] = "підмінений заголовок",
                ["status"] = 200,
                ["errorCode"] = "ECR-FAKE-0000",
                ["Detail"] = "ще один",
            }));

        Assert.Equal(1, Occurrences(body, "\"detail\""));
        Assert.Equal(1, Occurrences(body, "\"title\""));
        Assert.Equal(1, Occurrences(body, "\"status\""));
        Assert.Equal(1, Occurrences(body, "\"errorCode\""));
        Assert.DoesNotContain("\"Detail\"", body, StringComparison.Ordinal);

        var problem = JsonDocument.Parse(body).RootElement;

        // Справжні значення — ті, що склав конвеєр, а не ті, що принесли подробиці.
        Assert.Equal(
            "Cells you may not edit in this batch: 1. Reason for the first: NoGrant.",
            problem.GetProperty("detail").GetString());
        Assert.Equal("ECR-ACCS-0403", problem.GetProperty("errorCode").GetString());
        Assert.Equal(403, problem.GetProperty("status").GetInt32());
        Assert.DoesNotMatch("[а-яА-ЯіІїЇєЄ]", body);

        // ⚠ Нестандартні подробиці їдуть далі як і раніше — фільтр вузький.
        Assert.Equal("NoGrant", problem.GetProperty("reason").GetString());
    }

    private static int Occurrences(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>Проганяє виняток крізь справжній конвеєр і повертає сирий текст тіла.</summary>
    internal static async Task<string> ProblemTextAsync(Exception exception)
    {
        var user = Substitute.For<ICurrentUser>();
        user.Language.Returns("en");

        var services = new ServiceCollection();
        services.AddSingleton<IUiStringCatalog>(new FakeUiStringCatalog().Add(
            "en", "err.ECR-ACCS-0403.deniedCells",
            "Cells you may not edit in this batch: {deniedCount}. Reason for the first: {reason}.",
            UiStringScope.Private));
        services.AddSingleton(user);

        await using var provider = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = provider };
        using var stream = new MemoryStream();
        context.Response.Body = stream;

        var middleware = new ExceptionHandlingMiddleware(
            _ => throw exception, NullLogger<ExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
