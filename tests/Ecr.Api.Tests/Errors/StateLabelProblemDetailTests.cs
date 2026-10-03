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
/// T2-09 (б): підміна сирої назви стану підписом каталогу змінює ЛИШЕ текст <c>detail</c> — код відповіді,
/// <c>errorCode</c> і <c>messageKey</c> лишаються тими самими, що й без підміни.
/// </summary>
public sealed class StateLabelProblemDetailTests
{
    private const string Key = "err.ECR-PRD-0409.reopenOnlyClosed";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Підпис_стану_міняє_лише_текст_а_не_код_і_статус_відповіді()
    {
        var (statusWith, codeWith, keyWith, detailWith) = await RunAsync(withLabel: true);
        var (statusWithout, codeWithout, keyWithout, detailWithout) = await RunAsync(withLabel: false);

        Assert.Equal(statusWithout, statusWith);
        Assert.Equal(StatusCodes.Status409Conflict, statusWith);
        Assert.Equal(codeWithout, codeWith);
        Assert.Equal("ECR-PRD-0409", codeWith);
        Assert.Equal(keyWithout, keyWith);

        Assert.Equal("The period is Grace period.", detailWith);
        Assert.Equal("The period is Grace.", detailWithout);
    }

    private static async Task<(int Status, string? Code, string? MessageKey, string? Detail)> RunAsync(bool withLabel)
    {
        var strings = new Dictionary<string, string>(StringComparer.Ordinal) { [Key] = "The period is {state}." };
        if (withLabel)
        {
            strings["status.period.Grace"] = "Grace period";
        }

        var catalog = Substitute.For<IUiStringCatalog>();
        catalog.GetAsync("en", Arg.Any<CancellationToken>()).Returns(new UiStringCatalog("en", 1, strings));

        var user = Substitute.For<ICurrentUser>();
        user.Language.Returns("en");

        var services = new ServiceCollection().AddSingleton(catalog).AddSingleton(user).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();

        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new BusinessRuleException(
                "ECR-PRD-0409",
                "Сире речення розробника.",
                new Dictionary<string, object?> { ["messageKey"] = Key, ["state"] = "Grace" }),
            NullLogger<ExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        var root = json.RootElement;

        return (
            context.Response.StatusCode,
            root.GetProperty("errorCode").GetString(),
            root.TryGetProperty("messageKey", out var mk) ? mk.GetString() : null,
            root.GetProperty("detail").GetString());
    }
}
