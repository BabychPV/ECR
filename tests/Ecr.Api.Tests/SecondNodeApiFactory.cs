using System.Collections.Concurrent;
using Ecr.TestKit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace Ecr.Api.Tests;

/// <summary>
/// Той самий застосунок, піднятий як ДРУГИЙ ВУЗОЛ: спільна база, інший
/// каталог установки.
/// </summary>
/// <remarks>
/// ⛔ Навіщо окрема фабрика, коли <see cref="EcrApiFactory"/> уже піднімає
/// другий хост. Два хости з одного чекауту мають ОДНАКОВИЙ content root, а він
/// за замовчуванням і є ознакою застосунку для DataProtection
/// (<c>ApplicationDiscriminator</c>). Тому пара звичайних фабрик ділить кільце
/// ключів навіть тоді, коли в коді немає жодного <c>AddDataProtection</c>, —
/// перевірено мутацією: тест «cookie перейшла» лишався ЗЕЛЕНИМ на коді без
/// спільного кільця. Це рівно той різновид хибнозеленого, через який дефект і
/// прожив досі.
///
/// ⚠ Різний каталог — не штучність задля падіння тесту, а те, як виглядає
/// друга машина: служба розгортається зі свого каталогу, і дефолтна ознака в
/// неї своя. Спільна таблиця ключів без сталого імені застосунку від цього не
/// рятує — ключ із бази не підійде, бо ланцюжок призначення інший.
///
/// ⚠ Поруч кладуться копії <c>appsettings*.json</c>: конфігурацію застосунок
/// читає з content root, і другий вузол має отримати ту саму, а не порожню.
/// </remarks>
public sealed class SecondNodeApiFactory : WebApplicationFactory<Program>
{
    private readonly SqlServerFixture _sql;
    private readonly string _contentRoot;

    /// <summary>Створює каталог «другої машини» з копією конфігурації.</summary>
    public SecondNodeApiFactory(SqlServerFixture sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        _sql = sql;
        _contentRoot = Path.Combine(
            Path.GetTempPath(), $"ecr-node2-{Guid.NewGuid():N}");

        Directory.CreateDirectory(_contentRoot);

        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "appsettings*.json"))
        {
            File.Copy(file, Path.Combine(_contentRoot, Path.GetFileName(file)), overwrite: true);
        }
    }

    /// <summary>Помилки, які другий вузол записав у лог.</summary>
    public ConcurrentQueue<string> ServerErrors { get; } = new();

    /// <summary>Серверні помилки одним рядком — для повідомлення асерту.</summary>
    public string ErrorsText => ServerErrors.IsEmpty
        ? "(другий вузол не записав жодної помилки)"
        : string.Join("\n", ServerErrors);

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Ті самі змінні оточення й з тієї самої причини, що в `EcrApiFactory`:
        // Program.cs сам додає джерело з префіксом ECR_, і воно перекриває
        // порожній ConnectionStrings:Ecr з appsettings.json.
        Environment.SetEnvironmentVariable("ECR_ConnectionStrings__Ecr", _sql.ConnectionString);
        Environment.SetEnvironmentVariable("ECR_Schema__StartupMode", "Validate");
        Environment.SetEnvironmentVariable("ECR_Auth__RequireHttps", "false");
        Environment.SetEnvironmentVariable("ECR_Auth__EnableNegotiate", "false");
        Environment.SetEnvironmentVariable("ECR_Auth__StampCacheSeconds", "0");

        // ⛔ Ось воно, єдине, чим цей вузол відрізняється.
        builder.UseContentRoot(_contentRoot);
        builder.UseEnvironment("Development");

        builder.ConfigureLogging(logging =>
        {
            // Причини — ті самі, що в `EcrApiFactory`: постачальник EventLog
            // кидає після закриття хоста, а Quartz тримає фабрику логів у
            // статичному полі процесу.
            logging.ClearProviders();
            logging.AddProvider(new NodeLoggerProvider(ServerErrors));
        });
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
        {
            return;
        }

        try
        {
            Directory.Delete(_contentRoot, recursive: true);
        }
        catch (IOException)
        {
            // Тимчасовий каталог прибере ОС. Падати на прибиранні означало б
            // червоніти там, де перевірка вже відповіла.
        }
        catch (UnauthorizedAccessException)
        {
            // Те саме: права на тимчасовий каталог — не предмет цієї перевірки.
        }
    }

    private sealed class NodeLoggerProvider(ConcurrentQueue<string> errors) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new NodeLogger(categoryName, errors);

        public void Dispose() { }

        private sealed class NodeLogger(string category, ConcurrentQueue<string> errors) : ILogger
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
                errors.Enqueue($"[{category}] {formatter(state, exception)}\n{exception}");
            }
        }
    }
}
