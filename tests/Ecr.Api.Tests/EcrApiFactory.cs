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
public sealed class EcrApiFactory(SqlServerFixture sql, int stampCacheSeconds = 0)
    : WebApplicationFactory<Program>
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

    /// <summary>
    /// **Увесь** лог застосунку, а не лише помилки.
    /// </summary>
    /// <remarks>
    /// Потрібен рівно для одного класу перевірок: секрет не має з'явитися в
    /// журналі ЖОДНОГО рівня (ФВ-6.11). Перевіряти лише помилки означало б
    /// пропустити найімовірніше місце витоку — Information від конвеєра.
    /// </remarks>
    public ConcurrentQueue<string> ServerLog { get; } = new();

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

        // ⚠ Кеш штампа типово вимкнений: інакше «негайно» в тесті означало б
        // «через п'ять секунд», і перевірка відкликання прав або спала б, або
        // стала б повільною і плавучою.
        //
        // ⛔ Але саме це вимкнення сховало `A7-21`: із живим кешем застосунок
        // виходив із сеансу, який щойно створив, бо в кеші лишався штамп до
        // зміни пароля. Фікстура вимикала механізм, який ламався, — тому
        // значення тепер задається, і принаймні один тест бере його увімкненим.
        Environment.SetEnvironmentVariable(
            "ECR_Auth__StampCacheSeconds",
            stampCacheSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

        builder.UseEnvironment("Development");
        builder.ConfigureLogging(logging =>
        {
            // ⛔ Типові постачальники прибираються ЦІЛКОМ, і головний тут —
            // `EventLog`. `WebApplication.CreateBuilder` додає його на Windows
            // мовчки, а він пише в журнал подій ОС і, на відміну від решти,
            // після закриття хосту КИДАЄ `ObjectDisposedException`
            // ('EventLogInternal').
            //
            // ⛔ Ціна цього була не косметичною. Quartz тримає постачальника
            // логів у СТАТИЧНОМУ полі процесу: його ставить перший піднятий
            // застосунок, а кожен тест піднімає свій. Коли фонова задача
            // падає (а вона падає — див. нижче), Quartz пише про це через
            // фабрику логів ПЕРШОГО застосунку, якого вже немає;
            // `Logger.Log` збирає виняток постачальника в
            // `AggregateException`, той виходить із `JobRunShell.Run`, а
            // `QuartzHostedService.StopAsync` віддає його з
            // `WebApplicationFactory.Dispose()`. Тест червонів на `Dispose`,
            // маючи всі перевірки зеленими, і робив це приблизно раз на п'ять
            // прогонів — тобто виглядав як «плаваючий» без жодної причини.
            //
            // ⚠ Сама задача падає з іншої причини, і вона теж процесна:
            // Quartz реєструє планувальник у статичному `SchedulerRepository`
            // за іменем, тож застосунок, піднятий другим, отримує планувальник
            // ПЕРШОГО. Задача виконується у вже звільненому контейнері й
            // помирає на `IMemoryCache`. Для проду це нічого не означає — там
            // один застосунок на процес, — але жоден тест, який ставить задачу
            // в чергу, без цього не буде стабільним.
            //
            // ⚠ Нічого не втрачається: увесь лог і так збирає
            // `CapturingLoggerProvider`, і саме з нього тести беруть причину
            // невдачі.
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
