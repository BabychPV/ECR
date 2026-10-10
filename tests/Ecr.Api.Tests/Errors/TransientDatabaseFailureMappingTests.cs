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
    [InlineData(2)]       // E1-05: сервер не знайдено
    [InlineData(40)]      // E1-05: не вдалося відкрити з'єднання
    [InlineData(53)]      // E1-05: мережевий шлях не знайдено
    [InlineData(121)]     // E1-05: тайм-аут семафора
    [InlineData(1205)]
    [InlineData(10054)]
    [InlineData(10061)]   // E1-05: з'єднання відхилено
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

    /// <summary>
    /// E1-05: вичерпаний пул з'єднань SqlClient — <c>InvalidOperationException</c>, а не <c>SqlException</c>; це 503,
    /// а інший <c>InvalidOperationException</c> лишається 500. Мутація: прибрати гілку <c>IsPoolExhausted</c> — 500.
    /// </summary>
    [Fact]
    [Trait("Requirement", "ФВ-6.11")]
    public void E1_05_вичерпаний_пул_з_єднань_дає_503_а_інший_InvalidOperationException_500()
    {
        var pool = new InvalidOperationException(
            "Timeout expired.  The timeout period elapsed prior to obtaining a connection from the pool.  "
            + "This may have occurred because all pooled connections were in use and max pool size was reached.");

        var (status, code, _, details) = Map(pool);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status);
        Assert.Equal("ECR-SYS-0503", code);
        Assert.Equal("err.ECR-SYS-0503.databaseBusy", details!["messageKey"]);

        // Пул у ланцюжку (EF обгортає) — теж.
        Assert.Equal(
            StatusCodes.Status503ServiceUnavailable, Map(new DbUpdateException("save failed", pool)).Status);

        Assert.Equal(
            StatusCodes.Status500InternalServerError,
            Map(new InvalidOperationException("Sequence contains no elements")).Status);
    }

    /// <summary>
    /// E1-06 (X5-03): <c>BadHttpRequestException</c> Kestrel — вина клієнта: 413 для тіла понад межу, 400 для решти;
    /// текст винятку клієнтові не їде. Мутація: прибрати арм — 500 <c>ECR-SYS-0500</c>.
    /// </summary>
    [Fact]
    [Trait("Requirement", "ФВ-6.11")]
    public void E1_06_BadHttpRequestException_дає_413_або_400_а_не_500()
    {
        var tooLarge = new BadHttpRequestException("Request body too large. secret-internal-detail", StatusCodes.Status413PayloadTooLarge);

        var (status, code, message, details) = Map(tooLarge);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, status);
        Assert.Equal("ECR-REQ-0422", code);
        Assert.Equal("err.ECR-REQ-0422.requestTooLarge", details!["messageKey"]);
        Assert.DoesNotContain("secret-internal-detail", message, StringComparison.Ordinal);

        var (badStatus, badCode, badMessage, badDetails) = Map(
            new BadHttpRequestException("Unexpected end of request content. secret-internal-detail", StatusCodes.Status400BadRequest));
        Assert.Equal(StatusCodes.Status400BadRequest, badStatus);
        Assert.Equal("ECR-REQ-0422", badCode);
        Assert.Equal("err.ECR-REQ-0422.malformedRequest", badDetails!["messageKey"]);
        Assert.DoesNotContain("secret-internal-detail", badMessage, StringComparison.Ordinal);
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
