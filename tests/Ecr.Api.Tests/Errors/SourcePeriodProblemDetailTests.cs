using System.Text.Json;
using Ecr.Api.Errors;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Workflow;
using Ecr.TestKit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests.Errors;

/// <summary>
/// UI-тест 20.11: відмова воріт місяця джерела (<c>ECR-SUB-4221</c>) показувала сирий ключ періоду («місяць 202601»).
/// Тепер <c>detail</c> називає місяць мовою читача; <c>errorCode</c>, <c>messageKey</c> і сам параметр у відповіді не змінюються.
/// </summary>
public sealed class SourcePeriodProblemDetailTests
{
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData("en", "Month January 2026 of HSE400.")]
    [InlineData("ru", "Месяц Январь 2026 HSE400.")]
    public async Task Місяць_джерела_у_відмові_названо_мовою_читача_а_не_ключем(string language, string expected)
    {
        var (status, code, key, detail, rawParam) = await RunAsync(language);

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, status);
        Assert.Equal("ECR-SUB-4221", code);
        Assert.Equal(SourceMonthApprovalCheck.MessageKey, key);
        Assert.Equal(expected, detail);
        Assert.Equal("202601", rawParam); // контракт для споживача API не змінено
    }

    private static async Task<(int Status, string? Code, string? MessageKey, string? Detail, string? Param)> RunAsync(string language)
    {
        var strings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SourceMonthApprovalCheck.MessageKey] = language == "ru"
                ? "Месяц {sourcePeriodKey} {sourceProjectCode}."
                : "Month {sourcePeriodKey} of {sourceProjectCode}.",
            ["periods.monthOf"] = "{month} {year}",
            ["periods.month.1"] = language == "ru" ? "Январь" : "January",
        };

        var catalog = Substitute.For<IUiStringCatalog>();
        catalog.GetAsync(language, Arg.Any<CancellationToken>()).Returns(new UiStringCatalog(language, 1, strings));

        var user = Substitute.For<ICurrentUser>();
        user.Language.Returns(language);

        var services = new ServiceCollection().AddSingleton(catalog).AddSingleton(user).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();

        var gate = Substitute.For<ISourceMonthApprovalGate>();
        gate.FindUnapprovedSourceMonthAsync(7, 202601, Arg.Any<CancellationToken>())
            .Returns(new UnapprovedSourceMonth(9, 202601, "HSE400", "HSE400_2026M"));

        var middleware = new ExceptionHandlingMiddleware(
            _ => SourceMonthApprovalCheck.EnsureApprovedAsync(gate, 7, 202601, "Подання", CancellationToken.None),
            NullLogger<ExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        var root = json.RootElement;

        return (
            context.Response.StatusCode,
            root.GetProperty("errorCode").GetString(),
            root.TryGetProperty("messageKey", out var mk) ? mk.GetString() : null,
            root.GetProperty("detail").GetString(),
            root.TryGetProperty("sourcePeriodKey", out var p) ? p.GetString() : null);
    }
}
