// tests/Ecr.Api.Tests/Errors/TransientDatabaseFailureMappingTests.cs
using System.Reflection;
using System.Text.Json;
using Ecr.Api.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecr.Api.Tests.Errors;

/// <summary>
/// R5-E1/E1-04: тимчасовий збій БД (вичерпані повтори 1205, тайм-аут -2, обрив з'єднання) — 503
/// <c>ECR-SYS-0503</c> з <c>Retry-After</c>, а не 500 <c>ECR-SYS-0500</c>.
/// </summary>
public sealed class TransientDatabaseFailureMappingTests
{
    [Theory]
    [Trait("Requirement", "ФВ-6.11")]
    [InlineData(-2)]
    [InlineData(1205)]
    [InlineData(10054)]
    [InlineData(40613)]
    public void Тимчасовий_номер_SqlException_дає_503_databaseBusy(int number)
    {
        // ⛔ Мутація: прибрати арм `IsTransientDatabaseFailure` у Map → 500 ECR-SYS-0500.
        var (status, code, _, details) = Map(
            new DbUpdateException("save failed", Sql(number)));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status);
        Assert.Equal("ECR-SYS-0503", code);
        Assert.Equal("err.ECR-SYS-0503.databaseBusy", details!["messageKey"]);
    }

    [Fact]
    [Trait("Requirement", "ФВ-6.11")]
    public void Вичерпані_повтори_EF_дають_503()
    {
        var (status, code, _, _) = Map(
            new RetryLimitExceededException("retries exhausted", Sql(1205)));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status);
        Assert.Equal("ECR-SYS-0503", code);
    }

    [Fact]
    [Trait("Requirement", "ФВ-6.11")]
    public void Нетимчасовий_SqlException_і_голий_TimeoutException_лишаються_500()
    {
        // 2627 — порушення унікальності: помилка даних, повтор не допоможе.
        Assert.Equal(StatusCodes.Status500InternalServerError, Map(Sql(2627)).Status);

        // TimeoutException кидає й SMTP — «база зайнята» для нього була б неправдою.
        Assert.Equal(
            StatusCodes.Status500InternalServerError,
            Map(new TimeoutException("smtp")).Status);
    }

    [Fact]
    [Trait("Requirement", "ФВ-6.11")]
    public async Task Відповідь_503_несе_Retry_After_і_не_розкриває_текст_SQL()
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new RetryLimitExceededException("retries exhausted", Sql(1205)),
            NullLogger<ExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        // ⛔ Мутація: прибрати встановлення `Retry-After` у WriteAsync → заголовок порожній.
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal(
            "5",
            context.Response.Headers.RetryAfter.ToString());

        body.Position = 0;
        using var json = await JsonDocument.ParseAsync(body);
        Assert.Equal("ECR-SYS-0503", json.RootElement.GetProperty("errorCode").GetString());
        Assert.DoesNotContain("retries exhausted", System.Text.Encoding.UTF8.GetString(body.ToArray()), StringComparison.Ordinal);
    }

    /// <summary><c>ExceptionHandlingMiddleware.Map</c> (internal) — рефлексією, як у <c>ErrorContractTests</c>.</summary>
    private static (int Status, string Code, string Message, IReadOnlyDictionary<string, object?>? Details) Map(Exception exception)
        => ((int, string, string, IReadOnlyDictionary<string, object?>?))typeof(ExceptionHandlingMiddleware)
            .GetMethod("Map", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [exception])!;

    /// <summary><see cref="SqlException"/> з заданим номером: публічного конструктора немає, тож — рефлексією.</summary>
    private static SqlException Sql(int number)
    {
        var errorCtor = typeof(SqlError)
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        var args = errorCtor.GetParameters()
            .Select(p => p.Name == "infoNumber" ? number : Default(p.ParameterType))
            .ToArray();
        var error = (SqlError)errorCtor.Invoke(args);

        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        typeof(SqlErrorCollection)
            .GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(errors, [error]);

        var create = typeof(SqlException)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .First(m => m.Name == "CreateException"
                        && m.GetParameters().Select(p => p.ParameterType)
                            .SequenceEqual([typeof(SqlErrorCollection), typeof(string)]));

        var exception = (SqlException)create.Invoke(null, [errors, "16.0"])!;
        Assert.Equal(number, exception.Number);

        return exception;
    }

    private static object? Default(Type type)
        => type == typeof(string) ? "test"
            : type.IsValueType ? Activator.CreateInstance(type)
            : null;
}
