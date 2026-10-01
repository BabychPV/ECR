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

namespace Ecr.Api.Tests.Errors;

/// <summary>
/// Подробиця відмови з <c>messageKey</c> підставляє ВСІ значення подробиць, а не лише рядки.
/// </summary>
/// <remarks>
/// ⛔ Клас дефекту <c>D1</c>: <c>ExceptionHandlingMiddleware.ResolveGenericMessageAsync</c> брав
/// лише <c>string</c>, тож <c>["maxLength"] = 64</c> (<c>ChangeDocumentKeyHandler</c>) лишав людині
/// «…до {maxLength} символів» фігурними дужками — поле в подробицях було, а сторож кидків
/// (<c>MessageKeyRatchetTests</c>) за іменем його й бачив. ТИП значення з тексту джерела не
/// встановити, тому тут — прогін справжнього винятку крізь сам обробник.
/// </remarks>
public sealed class ProblemDetailParamsTests
{
    private const string Key = "err.ECR-DOC-0422.sample";

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData(64, "64")]
    [InlineData(64L, "64")]
    [InlineData(1234.5, "1234.5")]
    [InlineData(true, "true")]
    public async Task Нерядкове_значення_підставляється_в_detail(object value, string expected)
    {
        var detail = await DetailAsync("Key up to {max} characters.", value);

        Assert.Equal($"Key up to {expected} characters.", detail);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Рядок_як_і_раніше_підставляється()
    {
        var detail = await DetailAsync("Key up to {max} characters.", "ten");

        Assert.Equal("Key up to ten characters.", detail);
    }

    private static async Task<string?> DetailAsync(string template, object value)
    {
        var catalog = Substitute.For<IUiStringCatalog>();
        catalog.GetAsync("en", Arg.Any<CancellationToken>())
            .Returns(new UiStringCatalog("en", 1, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [Key] = template,
            }));

        var user = Substitute.For<ICurrentUser>();
        user.Language.Returns("en");

        var services = new ServiceCollection()
            .AddSingleton(catalog)
            .AddSingleton(user)
            .BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();

        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new BusinessRuleException(
                "ECR-DOC-0422",
                "Сире речення розробника.",
                new Dictionary<string, object?> { ["messageKey"] = Key, ["max"] = value }),
            NullLogger<ExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        return json.RootElement.GetProperty("detail").GetString();
    }
}
