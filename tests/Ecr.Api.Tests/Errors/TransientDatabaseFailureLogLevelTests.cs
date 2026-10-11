// tests/Ecr.Api.Tests/Errors/TransientDatabaseFailureLogLevelTests.cs
using System.Reflection;
using Ecr.Api.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ecr.Api.Tests.Errors;

/// <summary>
/// RC19 P3-2: тайм-аут SQL (-2) на записі комірок — 503 <c>databaseBusy</c> з <c>Retry-After</c> і запис у журнал
/// рівня Warning, а не Error «Необроблений виняток» (це тимчасова зайнятість БД, не дефект коду).
/// </summary>
/// <remarks>
/// ⛔ Мутація: повернути <c>status >= 500 → LogUnhandled</c> для будь-якого 503 — Error з'являється, тест червоний.
/// ⚠ Справжній 500 (нетимчасовий збій) лишається Error: другий тест це стереже.
/// </remarks>
public sealed class TransientDatabaseFailureLogLevelTests
{
    [Theory]
    [Trait("Requirement", "ФВ-6.11")]
    [InlineData(-2)]
    [InlineData(1205)]
    public async Task Тайм_аут_команди_на_записі_комірок_дає_503_з_Retry_After_і_Warning_а_не_Error(int number)
    {
        var logger = new CapturingLogger();
        var context = NewContext();

        await Invoke(context, logger, new DbUpdateException("save failed", Sql(number)));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("5", context.Response.Headers.RetryAfter.ToString());
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Error);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    [Trait("Requirement", "ФВ-6.11")]
    public async Task Нетимчасовий_збій_лишається_500_з_Error()
    {
        var logger = new CapturingLogger();
        var context = NewContext();

        await Invoke(context, logger, new InvalidOperationException("boom"));

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);
    }

    private static DefaultHttpContext NewContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task Invoke(HttpContext context, CapturingLogger logger, Exception exception)
    {
        var middleware = new ExceptionHandlingMiddleware(_ => throw exception, logger);
        await middleware.InvokeAsync(context);
    }

    private sealed class CapturingLogger : ILogger<ExceptionHandlingMiddleware>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

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

        return (SqlException)create.Invoke(null, [errors, "16.0"])!;
    }

    private static object? Default(Type type)
        => type == typeof(string) ? "test"
            : type.IsValueType ? Activator.CreateInstance(type)
            : null;
}
