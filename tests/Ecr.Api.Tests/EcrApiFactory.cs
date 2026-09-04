using System.Collections.Concurrent;
using Ecr.TestKit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace Ecr.Api.Tests;

/// <summary>
/// Піднімає застосунок на базі, яку вже підготувала <see cref="SqlServerFixture"/>.
/// </summary>
/// <remarks>
/// ⚠ Схема вже вибудувана фікстурою, тому режим старту — <c>Validate</c>:
/// застосунок має **перевірити** схему, а не домігрувати її. Саме так він
/// працює в проді (D-66), і саме це має перевірятися тестами.
///
/// ⚠ Файла немає в дереві `05-skeleton.md` §1 (`Q-053`): без нього жоден
/// тест `Ecr.Api.Tests` не може підняти застосунок із реальною базою.
/// </remarks>
public sealed class EcrApiFactory(SqlServerFixture sql) : WebApplicationFactory<Program>
{
    /// <summary>
    /// Помилки, які застосунок записав у лог під час прогону.
    /// </summary>
    /// <remarks>
    /// Без цього невдалий тест показує голий <c>500</c> і жодного натяку на
    /// причину: <c>ExceptionHandlingMiddleware</c> навмисно не віддає клієнту
    /// ні тексту винятку, ні стека (ФВ-6.11). Тесту ця інформація потрібна, і
    /// взяти її можна лише з логу сервера.
    /// </remarks>
    public ConcurrentQueue<string> ServerErrors { get; } = new();

    /// <summary>Серверні помилки одним рядком — для повідомлення асерту.</summary>
    public string ErrorsText => ServerErrors.IsEmpty
        ? "(сервер не записав жодної помилки)"
        : string.Join("\n", ServerErrors);

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // ⚠ Через ЗМІННІ ОТОЧЕННЯ, а не ConfigureAppConfiguration. Причина не
        // стильова: Program.cs додає AddEnvironmentVariables(prefix: "ECR_")
        // САМ, і це джерело іде після appsettings.json, де ключ
        // ConnectionStrings:Ecr присутній із порожнім значенням (Q-029).
        // In-memory джерело фікстури лягає РАНІШЕ і програє порожньому рядку,
        // а падає це аж у SqlConnection. Заразом так тест конфігурує застосунок
        // тим самим способом, що й прод (D-11).
        Environment.SetEnvironmentVariable("ECR_ConnectionStrings__Ecr", sql.ConnectionString);
        Environment.SetEnvironmentVariable("ECR_Schema__StartupMode", "Validate");
        Environment.SetEnvironmentVariable("ECR_Auth__RequireHttps", "false");

        // ⚠ Negotiate вимкнений: його обробник виконується на кожен запит і
        // вимагає IConnectionItemsFeature, якого TestServer не має — без цього
        // 500 отримує навіть /health/live (`Q-054`). Наслідок чесний і його
        // треба знати: доменний вхід ЦИМИ тестами не покривається.
        Environment.SetEnvironmentVariable("ECR_Auth__EnableNegotiate", "false");

        builder.UseEnvironment("Development");
        builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(ServerErrors)));
    }

    private sealed class CapturingLoggerProvider(ConcurrentQueue<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, sink);

        public void Dispose() { }

        private sealed class CapturingLogger(string category, ConcurrentQueue<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                {
                    return;
                }

                ArgumentNullException.ThrowIfNull(formatter);
                sink.Enqueue($"[{category}] {formatter(state, exception)}\n{exception}");
            }
        }
    }
}
