// tests/Ecr.Api.Tests/Security/PipelineProbes.cs

using System.Net;
using Ecr.Api.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecr.Api.Tests.Security;

/// <summary>Складання тестового хоста з навмисно зламаною ланкою конвеєра.</summary>
public static class FailingPipeline
{
    /// <summary>
    /// Той самий застосунок, але <c>CorrelationIdMiddleware</c> падає зсередини.
    /// </summary>
    /// <param name="app">Базова фабрика (вона ж збирає лог сервера).</param>
    public static WebApplicationFactory<Program> WithFailingCorrelationLogger(EcrApiFactory app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<ILogger<CorrelationIdMiddleware>>(
                new ThrowingScopeLogger<CorrelationIdMiddleware>())));
    }
}

/// <summary>
/// Підміняє адресу сокета значенням заголовка — інакше обмежувач частоти
/// неперевірний.
/// </summary>
/// <remarks>
/// ⛔ <c>TestServer</c> лишає <c>Connection.RemoteIpAddress</c> порожнім, тобто
/// ВСІ тестові клієнти потрапляють в один розділ обмежувача. Без цієї підміни
/// зворотний бік вимоги — «сусід з іншої адреси заходить, поки один вичерпав
/// межу» — перевірити неможливо в принципі, а саме він відрізняє захист від
/// відмови в обслуговуванні своїм же.
///
/// ⚠ Це <see cref="IStartupFilter"/>, а не middleware в конвеєрі застосунку:
/// фільтри старту обгортають ВЕСЬ конвеєр <c>Program.cs</c>, тобто підміна
/// відбувається до першого рядка продуктового коду — рівно там, де мережа
/// віддала б справжню адресу. Продуктовий код про цей заголовок не знає нічого.
/// </remarks>
public sealed class RemoteIpStartupFilter : IStartupFilter
{
    /// <summary>Заголовок, у якому тест передає адресу «сокета».</summary>
    public const string HeaderName = "X-Test-Remote-Ip";

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);

        return app =>
        {
            app.Use(async (context, following) =>
            {
                var value = context.Request.Headers[HeaderName].ToString();

                if (IPAddress.TryParse(value, out var address))
                {
                    context.Connection.RemoteIpAddress = address;
                }

                await following().ConfigureAwait(false);
            });

            next(app);
        };
    }
}

/// <summary>
/// Журнал, який ПАДАЄ на <see cref="ILogger.BeginScope{TState}(TState)"/>.
/// </summary>
/// <remarks>
/// ⛔ Єдиний спосіб кинути виняток УСЕРЕДИНІ <c>CorrelationIdMiddleware</c>, не
/// змінюючи його самого. Журнал приходить туди параметром
/// <c>InvokeAsync</c>, тобто резолвиться з <c>RequestServices</c> на КОЖЕН
/// запит — підміна реєстрації в тестовому контейнері й дає кидок рівно в тому
/// місці конвеєра, заради якого порядок і міняли (<c>S-23</c>).
///
/// ⚠ Падає саме <c>BeginScope</c>, а не <c>Log</c>: <c>Log</c> у цьому
/// middleware не викликається взагалі, тож тест мовчки нічого не перевіряв би.
/// </remarks>
public sealed class ThrowingScopeLogger<T> : ILogger<T>
{
    /// <summary>Текст винятку — щоб тест упізнав СВІЙ збій, а не чужий.</summary>
    public const string Message = "Навмисний збій усередині CorrelationIdMiddleware.";

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => throw new InvalidOperationException(Message);

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => false;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        // Навмисно порожньо: предмет перевірки — BeginScope.
    }
}
