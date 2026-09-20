// src/Ecr.Api/Observability/FileLog.cs

using System.Security;
using Ecr.Api.Auth;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace Ecr.Api.Observability;

/// <summary>Налаштування файлового журналу, секція <c>Logging:File</c>.</summary>
public sealed class FileLogOptions
{
    /// <summary>
    /// Тека журналу. Змінні оточення (<c>%ProgramData%</c>) розгортаються;
    /// відносний шлях — від теки застосунку; порожньо — файл не пишеться.
    /// </summary>
    public string? Directory { get; set; }

    /// <summary>Скільки добових файлів зберігати (рішення №10 директиви №15: 30 днів).</summary>
    public int RetainedFiles { get; set; } = 30;

    /// <summary>Стеля одного файлу, МБ; після неї — перекат у <c>ecr-yyyyMMdd_001.log</c>.</summary>
    public int FileSizeLimitMb { get; set; } = 100;
}

/// <summary>Що вийшло з файловим приймачем: куди пише або чому не пише.</summary>
/// <remarks>
/// ⚠ Заповнюється в момент створення постачальника. Якщо постачальника
/// прибрали (<c>ClearProviders()</c> у тестовій фабриці) — лишається порожнім,
/// і <c>/health/facts</c> чесно віддає <c>logDirectory: null</c>.
/// </remarks>
public sealed class FileLogStatus
{
    /// <summary>Тека, в яку справді пишеться файл; <c>null</c> — приймач не активний.</summary>
    public string? Directory { get; internal set; }

    /// <summary>Чому приймач не піднявся; <c>null</c> — піднявся або вимкнений налаштуванням.</summary>
    public string? Failure { get; internal set; }
}

/// <summary>
/// Файловий журнал (<c>D14-09</c>): Serilog як ЩЕ ОДИН постачальник
/// <see cref="Microsoft.Extensions.Logging.ILogger"/> поруч із консоллю та журналом подій ОС.
/// </summary>
/// <remarks>
/// ⚠ Рівні задає лише <c>Logging:LogLevel</c> — фільтр Microsoft.Extensions.Logging
/// стоїть ПЕРЕД постачальником, тому власний мінімум Serilog знято
/// (<c>Verbose</c>): два джерела правди про рівні розійшлися б першою ж правкою.
///
/// ⛔ Недоступна тека НЕ валить старт: служба без файлу журналу краща за службу,
/// яка не піднялась. Причина лягає у <see cref="FileLogStatus.Failure"/> і
/// пишеться попередженням у решту постачальників (<see cref="ReportFileLog"/>).
/// </remarks>
public static partial class FileLog
{
    /// <summary>Секція налаштувань.</summary>
    public const string Section = "Logging:File";

    /// <summary>Шаблон рядка: кореляція, користувач і машина — у КОЖНОМУ записі.</summary>
    public const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{CorrelationId}] [uid:{UserId}] "
        + "[{MachineName}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    /// <summary>Додає файлового постачальника.</summary>
    /// <param name="logging">Будівник журналювання хоста.</param>
    public static ILoggingBuilder AddEcrFileLog(this ILoggingBuilder logging)
    {
        ArgumentNullException.ThrowIfNull(logging);

        logging.Services.AddHttpContextAccessor();
        logging.Services.AddOptions<FileLogOptions>()
            .Configure<IConfiguration>((options, configuration) =>
                configuration.GetSection("Logging:File").Bind(options));
        logging.Services.TryAddSingleton<FileLogStatus>();
        logging.Services.AddSingleton<ILoggerProvider>(services => CreateProvider(
            services.GetRequiredService<IOptions<FileLogOptions>>().Value,
            services.GetRequiredService<IHttpContextAccessor>(),
            services.GetRequiredService<FileLogStatus>()));

        return logging;
    }

    /// <summary>Пише попередження, якщо файловий приймач не піднявся.</summary>
    /// <param name="app">Зібраний застосунок.</param>
    public static void ReportFileLog(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // ⚠ Логер береться ПЕРШИМ: саме він змушує фабрику створити
        // постачальників, тобто заповнити статус.
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(FileLog).FullName!);
        var status = app.Services.GetRequiredService<FileLogStatus>();

        if (status.Failure is not null)
        {
            LogUnavailable(logger, status.Failure);
        }
    }

    /// <summary>Абсолютна тека журналу або <c>null</c>, якщо файл вимкнено.</summary>
    /// <param name="configured">Значення <c>Logging:File:Directory</c>.</param>
    public static string? ResolveDirectory(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(configured.Trim());

        // ⚠ Поза Windows `%ProgramData%` не розгортається: буквальна тека
        // «%ProgramData%» у робочому каталозі гірша за чесне `logs`.
        if (expanded.Contains('%', StringComparison.Ordinal))
        {
            expanded = "logs";
        }

        return Path.GetFullPath(expanded, AppContext.BaseDirectory);
    }

    /// <summary>Перевіряє, що в теку можна писати; створює її, якщо немає.</summary>
    /// <param name="directory">Абсолютна тека.</param>
    /// <returns><c>null</c> — можна; інакше причина.</returns>
    public static string? Probe(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or SecurityException)
        {
            return $"{directory}: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static ILoggerProvider CreateProvider(
        FileLogOptions options, IHttpContextAccessor accessor, FileLogStatus status)
    {
        var directory = ResolveDirectory(options.Directory);
        if (directory is null)
        {
            return NullLoggerProvider.Instance;
        }

        if (Probe(directory) is { } failure)
        {
            status.Failure = failure;
            return NullLoggerProvider.Instance;
        }

        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.With(new RequestEnricher(accessor))
            .Enrich.WithProperty("MachineName", Environment.MachineName)
            .WriteTo.File(
                Path.Combine(directory, "ecr-.log"),
                outputTemplate: OutputTemplate,
                formatProvider: System.Globalization.CultureInfo.InvariantCulture,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: options.RetainedFiles,
                fileSizeLimitBytes: options.FileSizeLimitMb * 1024L * 1024L,
                rollOnFileSizeLimit: true,
                shared: false,
                // ⛔ Без буфера: запис про помилку, що лишився в пам'яті процесу,
                // який саме через цю помилку впав, — це відсутній запис.
                buffered: false)
            .CreateLogger();

        status.Directory = directory;
        return new SerilogLoggerProvider(logger, dispose: true);
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Файловий журнал вимкнено: у теку не вдається писати ({Failure}). Застосунок працює далі без файлу.")]
    private static partial void LogUnavailable(Microsoft.Extensions.Logging.ILogger logger, string failure);

    /// <summary>Користувач запиту.</summary>
    /// <remarks>
    /// ⚠ <c>CorrelationId</c> тут НЕ додається: він приходить зі scope
    /// <c>CorrelationIdMiddleware</c> (постачальник Serilog переносить scope у
    /// властивості запису), а єдиний, хто пише поза тим scope, —
    /// <c>ExceptionHandlingMiddleware</c> — несе його властивістю повідомлення.
    ///
    /// ⛔ <c>UserId</c> — числовий ідентифікатор із claim. Не логін і не SID.
    /// </remarks>
    private sealed class RequestEnricher(IHttpContextAccessor accessor) : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            if (accessor.HttpContext?.User.FindFirst(AuthenticationSetup.UserIdClaim)?.Value is { } userId)
            {
                logEvent.AddPropertyIfAbsent(new LogEventProperty("UserId", new ScalarValue(userId)));
            }
        }
    }
}
