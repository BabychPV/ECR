// src/Ecr.Worker/WorkerProgram.cs

using System.Data.Common;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Startup;
using Ecr.Worker.Isolation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ecr.Worker;

/// <summary>
/// Точка входу: <c>--supervisor [-- аргументи дитини]</c>, <c>--child</c> (дочірній
/// воркер черги, I1) або <c>--child &lt;аргументи заглушки&gt;</c> (перевірки P1).
/// </summary>
internal static partial class WorkerProgram
{
    /// <summary>Невідомий режим чи аргумент.</summary>
    public const int ExitUsage = 2;

    /// <summary>Недійсна конфігурація <c>Jobs:Workers:*</c> або ключів дочірнього (<see cref="WorkerConfigurationValidation"/>).</summary>
    public const int ExitInvalidConfiguration = 3;

    /// <summary>Наглядач поза Windows.</summary>
    public const int ExitUnsupportedPlatform = 4;

    /// <summary>
    /// Дочірній: міграції бази й збірки розходяться (або схему не вдалося звірити) — задач не бере
    /// (R6-X4/X4-02, <c>ECR-SYS-5031</c>).
    /// </summary>
    public const int ExitSchemaIncompatible = 5;

    /// <summary>Ім'я служби наглядача (P2 реєструє її в MSI).</summary>
    public const string ServiceName = "EcrWorker";

    /// <summary>Файл налаштувань поруч з exe; не <c>appsettings.json</c>, бо exe лежить у теці Ecr.Api.</summary>
    public const string SettingsFile = "worker.settings.json";

    private const string Usage =
        "Використання: Ecr.Worker --supervisor [-- аргументи дочірнього]  |  "
        + "Ecr.Worker --child  |  "
        + "Ecr.Worker --child --stub [--eat-mb N] [--hang] [--exit-after-ms N] [--exit-code C]";

    /// <summary>Запускає режим за першим аргументом; повертає код виходу.</summary>
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);

        var rest = args.Skip(1).ToList();
        switch (args.FirstOrDefault())
        {
            case "--supervisor":
                var separator = rest.IndexOf("--");
                if (separator != 0 && rest.Count > 0)
                {
                    break;
                }

                return await RunSupervisorAsync(separator == 0 ? rest.Skip(1).ToList() : [], cancellationToken)
                    .ConfigureAwait(false);
            case "--child" when rest.Count == 0:
                return await RunChildAsync(cancellationToken).ConfigureAwait(false);
            case "--child" when ChildStubOptions.Parse(rest) is { } stub:
                using (var host = CreateBuilder(stub).Build())
                {
                    await host.RunAsync(cancellationToken).ConfigureAwait(false);
                }

                return Environment.ExitCode;
        }

        await Console.Error.WriteLineAsync(Usage).ConfigureAwait(false);
        return ExitUsage;
    }

    private static async Task<int> RunSupervisorAsync(IReadOnlyList<string> childExtra, CancellationToken cancellationToken)
    {
        var builder = CreateBuilder(stub: null);
        builder.Services.AddWindowsService(o => o.ServiceName = ServiceName);

        // ⛔ R5-U1/U1-07: і ключі, які читає дочірній (Database:*, Calculations:*), — служба не стартує з
        // недійсним значенням, як і Api (U19), а не дає дітям мовчки взяти дефолт.
        var problems = WorkerPoolOptions.TryLoad(builder.Configuration, out var options)
            .Concat(WorkerConfigurationValidation.Validate(builder.Configuration))
            .ToList();
        if (problems.Count == 0)
        {
            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton(SelfAsChild(childExtra));
            builder.Services.AddSingleton(sp => new WorkerSupervisor(
                sp.GetRequiredService<WorkerPoolOptions>(),
                sp.GetRequiredService<ChildCommand>(),
                sp.GetRequiredService<ILogger<WorkerSupervisor>>()));
            builder.Services.AddHostedService<SupervisorService>();
        }

        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Ecr.Worker");

        if (problems.Count > 0)
        {
            var message = "Недійсна конфігурація воркера — служба не стартує:"
                + Environment.NewLine + string.Join(Environment.NewLine, problems.Select(p => "  " + p));
            LogFatal(logger, message);
            await Console.Error.WriteLineAsync(message).ConfigureAwait(false);
            return ExitInvalidConfiguration;
        }

        if (!OperatingSystem.IsWindows())
        {
            LogFatal(logger, JobObject.UnsupportedMessage);
            await Console.Error.WriteLineAsync(JobObject.UnsupportedMessage).ConfigureAwait(false);
            return ExitUnsupportedPlatform;
        }

        await host.RunAsync(cancellationToken).ConfigureAwait(false);
        return Environment.ExitCode;
    }

    /// <summary>
    /// Дочірній воркер (I1): лейн перерахунку черги в базі, роль <c>wrk</c>, одна
    /// задача на процес. Рядок підключення — зі змінної <c>ECR_ConnectionStrings__Ecr</c>
    /// оточення, успадкованого від наглядача (служби).
    /// </summary>
    private static async Task<int> RunChildAsync(CancellationToken cancellationToken)
    {
        var builder = CreateBuilder(stub: null);

        // ⚠ Перевірка контейнера при побудові ВИМКНЕНА навмисно (у Development її
        // вмикає хост): складання Infrastructure несе й реєстрації лише для Api —
        // захист секретів сповіщень через Data Protection, — яких задачі
        // перерахунку не резолвлять. Перевірка вимагала б сертифікат Data
        // Protection і в воркера без жодної потреби. Те, що граф перерахунку
        // резолвиться без Data Protection, доводить ChildCompositionTests.
        builder.ConfigureContainer(new DefaultServiceProviderFactory(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = false }));

        var problems = WorkerPoolOptions.TryLoad(builder.Configuration, out var pool)
            .Concat(WorkerConfigurationValidation.Validate(builder.Configuration))
            .ToList();
        if (problems.Count > 0)
        {
            await Console.Error.WriteLineAsync(string.Join(Environment.NewLine, problems)).ConfigureAwait(false);
            return ExitInvalidConfiguration;
        }

        try
        {
            Child.ChildComposition.AddChildWorker(builder.Services, builder.Configuration, pool);
            builder.Services.AddHostedService<ChildStopListener>();
        }
        catch (InvalidOperationException ex)
        {
            // Немає рядка підключення тощо: код виходу називає причину наглядачеві.
            await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return ExitInvalidConfiguration;
        }

        using var host = builder.Build();

        // ⛔ R6-X4/X4-02: до першої задачі — звірка міграцій бази зі збіркою (обидва напрями), як
        // Api на старті в режимі Validate. Вузли оновлюються не одночасно (D-32): інакше старий
        // воркер вузла B (перезавантаження у вікні, -SkipOtherNodesCheck) брав би задачі recalc
        // нового формату на новій схемі, а новий — на ще не накоченій. Відмова — код
        // ExitSchemaIncompatible; наглядач перезапускає дитину з наростаючою паузою (RestartBackoff),
        // тож після оновлення схеми чи вузла воркер підхоплюється сам, а задачі лишаються в черзі.
        var schemaProblem = await CheckSchemaAsync(host.Services, cancellationToken).ConfigureAwait(false);
        if (schemaProblem is not null)
        {
            var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Ecr.Worker");
            LogFatal(logger, schemaProblem);
            await Console.Error.WriteLineAsync(schemaProblem).ConfigureAwait(false);
            return ExitSchemaIncompatible;
        }

        return await RunBuiltChildAsync(host, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Робочий цикл уже побудованого дочірнього хоста зі скиданням метрик ДО його звільнення.
    /// </summary>
    /// <remarks>
    /// ⛔ Y6-02: тут стояло <c>try { host.RunAsync } finally { Flush(host.Services) }</c>. Розширення
    /// <c>RunAsync</c> у власному <c>finally</c> звільняє хост, а звільнений <c>ServiceProvider</c>
    /// на <c>GetService</c> кидає <see cref="ObjectDisposedException"/> — тож КОЖНА штатна зупинка
    /// справжнього <c>--child</c> закінчувалася необробленим винятком (код <c>0xE0434352</c>, подія
    /// «Application Error»), а справжній виняток з <c>RunAsync</c> підмінявся цим. Тому
    /// <c>RunAsync</c> розгорнуто: старт і очікування зупинки (<c>StopAsync</c> усередині),
    /// скидання буфера, а звільнення хоста лишається власникові (<c>using</c> у викликача).
    /// </remarks>
    internal static async Task<int> RunBuiltChildAsync(IHost host, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);

        try
        {
            await host.StartAsync(cancellationToken).ConfigureAwait(false);
            await host.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Дочірній короткоживучий: без скидання буфера метрики останньої задачі зникли б.
            Child.ChildTelemetry.Flush(host.Services);
        }

        return Environment.ExitCode;
    }

    /// <summary>
    /// Звірка міграцій бази зі збіркою для дочірнього (X4-02): текст відмови або <c>null</c>.
    /// </summary>
    /// <remarks>
    /// Fail-closed: база недоступна чи запит упав — теж відмова (не звірили — задач не беремо);
    /// наглядач повторить спробу з паузою.
    /// </remarks>
    private static async Task<string?> CheckSchemaAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<EcrDbContext>();
            var mismatch = await SchemaValidator.MigrationMismatchAsync(db, cancellationToken).ConfigureAwait(false);

            // ⛔ R6-X4/X4-03: і штамп релізу схеми — старий воркер на SQL-схемі новішого релізу.
            mismatch ??= await SchemaValidator.SchemaReleaseMismatchAsync(db, SchemaValidator.CodeRelease, cancellationToken)
                .ConfigureAwait(false);
            return mismatch is null ? null : "Дочірній воркер не бере задач — схема бази іншої версії: " + mismatch;
        }
        catch (DbException ex)
        {
            return "Дочірній воркер не бере задач — схему бази не звірено: " + ex.Message;
        }
    }

    /// <summary>
    /// Хост воркера: тека exe як корінь, <see cref="SettingsFile"/>, файл майданчика
    /// (<see cref="SiteSettingsPath"/>) і <c>ECR_</c> поверх.
    /// </summary>
    /// <param name="stub">Заглушка дочірнього для тестів наглядача.</param>
    /// <param name="contentRoot">Корінь вмісту; <c>null</c> — тека exe.</param>
    /// <param name="commonApplicationData">Корінь <c>%ProgramData%</c>; <c>null</c> — системний (тести дають тимчасову теку).</param>
    /// <remarks>
    /// ⛔ L2-12: exe лежить у теці Api (<c>Worker.wxs</c>: INSTALLFOLDER), а типові джерела
    /// <c>HostApplicationBuilder</c> підхоплюють звідти <c>appsettings.json</c> і
    /// <c>appsettings.{Environment}.json</c> Api — з <c>Jobs:Queue:Mode = Quartz</c>, телеметрією й
    /// <c>Calculations:*</c> Api. Їх прибрано: решта типових джерел (змінні оточення, логування)
    /// лишається, конфігурація воркера — лише його файл і <c>ECR_</c>.
    /// ⛔ R5-U1/U1-07 (аудит 2026-10-09): і файл майданчика — той самий
    /// <c>%ProgramData%\ECR\config\appsettings.Production.json</c>, що читає Api (runbook §2: «налаштування
    /// майданчика редагуйте тут»). Без нього <c>Database:*</c> і <c>Calculations:*</c>, задані там, діяли
    /// лише на Api, а перерахунок (з I2-2 — у дочірньому воркері) лишався на дефолтах. Порядок: файл
    /// воркера (у теці програми, переписується MSI) &lt; файл майданчика &lt; <c>ECR_</c>. Режим черги з
    /// файлу майданчика дочірньому не шкодить: його складання прибирає планувальники й лейни Api.
    /// Без перечитування на льоту (<c>reloadOnChange: false</c>): перевірка старту
    /// (<see cref="WorkerConfigurationValidation"/>) інакше не бачила б нового значення.
    /// </remarks>
    internal static HostApplicationBuilder CreateBuilder(
        ChildStubOptions? stub, string? contentRoot = null, string? commonApplicationData = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = contentRoot ?? AppContext.BaseDirectory,
        });
        foreach (var apiFile in builder.Configuration.Sources
                     .OfType<Microsoft.Extensions.Configuration.Json.JsonConfigurationSource>()
                     .Where(source => source.Path?.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) == true)
                     .ToList())
        {
            builder.Configuration.Sources.Remove(apiFile);
        }

        builder.Configuration.AddJsonFile(SettingsFile, optional: true, reloadOnChange: false);
        builder.Configuration.AddJsonFile(
            SiteSettingsPath(commonApplicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)),
            optional: true,
            reloadOnChange: false);
        builder.Configuration.AddEnvironmentVariables(prefix: "ECR_");
        if (stub is not null)
        {
            builder.Services.AddSingleton(stub);
            builder.Services.AddHostedService<ChildStub>();
            builder.Services.AddHostedService<ChildStopListener>();
        }

        return builder;
    }

    /// <summary>Файл майданчика — той самий шлях, що <c>ProgramDataConfiguration.AddProgramDataConfig</c> в Api.</summary>
    /// <param name="commonApplicationData">Корінь <c>%ProgramData%</c>.</param>
    internal static string SiteSettingsPath(string commonApplicationData)
        => Path.Combine(commonApplicationData, "ECR", "config", "appsettings.Production.json");

    /// <summary>Цей самий exe у ролі <c>--child</c> (через <c>dotnet</c> — з шляхом до dll).</summary>
    private static ChildCommand SelfAsChild(IReadOnlyList<string> extra)
    {
        var path = Environment.ProcessPath
            ?? throw new InvalidOperationException("Шлях до власного exe невідомий — дочірній воркер не запустити.");
        var arguments = new List<string>();
        if (string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add(typeof(WorkerProgram).Assembly.Location);
        }

        arguments.Add("--child");
        arguments.AddRange(extra);
        return new ChildCommand(path, arguments);
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "{Message}")]
    private static partial void LogFatal(ILogger logger, string message);

    /// <summary>Хост наглядача: пул живе, доки живе служба.</summary>
    private sealed class SupervisorService(WorkerSupervisor supervisor) : BackgroundService
    {
        /// <inheritdoc />
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => supervisor.RunAsync(stoppingToken);
    }
}
