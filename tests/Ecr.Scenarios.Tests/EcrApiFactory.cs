using System.Collections.Concurrent;
using Ecr.TestKit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace Ecr.Scenarios.Tests;

/// <summary>
/// Піднімає застосунок на базі, яку вже підготувала <see cref="SqlServerFixture"/>.
/// </summary>
/// <remarks>
/// ⚠ Копія `tests/Ecr.Api.Tests/EcrApiFactory.cs`, а не посилання на неї:
/// Правило 1 (§3.2 директиви) забороняє цьому проєкту залежати від
/// <c>Ecr.Api.Tests</c> — дозволені лише <c>Ecr.Api</c> (заради <c>Program</c>),
/// <c>Ecr.TestKit</c> (лише <see cref="SqlServerFixture"/>) і пакети HTTP-тестування.
/// Друга копія того самого файлу в двох проєктах — прийнятна плата за
/// фізичну відсутність шляху до обробників.
///
/// ⚠ Схема вже вибудувана фікстурою, тому режим старту — <c>Validate</c>:
/// застосунок має ПЕРЕВІРИТИ схему, а не домігрувати її (D-66) — так само,
/// як у проді.
/// </remarks>
public sealed class EcrApiFactory(SqlServerFixture sql, int stampCacheSeconds = 0)
    : WebApplicationFactory<Program>
{
    /// <summary>Помилки, які застосунок записав у лог під час прогону.</summary>
    /// <remarks>
    /// Без цього невдалий сценарій показує голий <c>500</c>: <c>ExceptionHandlingMiddleware</c>
    /// навмисно не віддає клієнту ні тексту винятку, ні стека (ФВ-6.11).
    /// </remarks>
    public ConcurrentQueue<string> ServerErrors { get; } = new();

    /// <summary>Увесь лог застосунку, а не лише помилки.</summary>
    public ConcurrentQueue<string> ServerLog { get; } = new();

    /// <summary>Серверні помилки одним рядком — для повідомлення асерту.</summary>
    public string ErrorsText => ServerErrors.IsEmpty
        ? "(сервер не записав жодної помилки)"
        : string.Join("\n", ServerErrors);

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // ⚠ Через ЗМІННІ ОТОЧЕННЯ, а не ConfigureAppConfiguration — так само,
        // як у оригіналі: Program.cs додає AddEnvironmentVariables(prefix: "ECR_")
        // САМ, і це джерело йде після appsettings.json.
        Environment.SetEnvironmentVariable("ECR_ConnectionStrings__Ecr", sql.ConnectionString);
        Environment.SetEnvironmentVariable("ECR_Schema__StartupMode", "Validate");
        Environment.SetEnvironmentVariable("ECR_Auth__RequireHttps", "false");

        // ⚠ Negotiate вимкнений: TestServer не має IConnectionItemsFeature,
        // якого вимагає обробник Negotiate на кожен запит.
        Environment.SetEnvironmentVariable("ECR_Auth__EnableNegotiate", "false");

        Environment.SetEnvironmentVariable(
            "ECR_Auth__StampCacheSeconds",
            stampCacheSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

        builder.UseEnvironment("Development");
        builder.ConfigureLogging(logging =>
        {
            // ⛔ Типові постачальники прибираються ЦІЛКОМ, і головний тут —
            // `EventLog`. Quartz тримає постачальника логів у СТАТИЧНОМУ полі
            // процесу: його ставить перший піднятий застосунок, а кожен
            // сценарій піднімає свій. Коли фонова задача падає, Quartz пише
            // про це через фабрику логів ПЕРШОГО застосунку, якого вже немає,
            // і виняток постачальника виходить із `WebApplicationFactory.Dispose()`
            // — сценарій червонів на `Dispose`, маючи всі перевірки зеленими
            // (той самий дефект, що й `tests/Ecr.Api.Tests/EcrApiFactory.cs`,
            // виправлений там і скопійований сюди — Правило 1 забороняє
            // посилання на `Ecr.Api.Tests`, тож копія, а не спільний файл).
            //
            // ⚠ Нічого не втрачається: увесь лог і так збирає
            // `CapturingLoggerProvider`.
            logging.ClearProviders();
            logging.AddProvider(new CapturingLoggerProvider(ServerErrors, ServerLog));
        });
    }

    private sealed class CapturingLoggerProvider(
        ConcurrentQueue<string> errors, ConcurrentQueue<string> all) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, errors, all);

        public void Dispose() { }

        private sealed class CapturingLogger(
            string category, ConcurrentQueue<string> errors, ConcurrentQueue<string> all) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                {
                    return;
                }

                ArgumentNullException.ThrowIfNull(formatter);

                var line = $"[{category}] {formatter(state, exception)}\n{exception}";
                all.Enqueue(line);

                if (logLevel >= LogLevel.Error)
                {
                    errors.Enqueue(line);
                }
            }
        }
    }
}
