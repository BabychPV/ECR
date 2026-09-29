// src/Ecr.Worker/WorkerProgram.cs

using Ecr.Worker.Isolation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ecr.Worker;

/// <summary>
/// Точка входу: <c>--supervisor [-- аргументи дитини]</c> або <c>--child [аргументи заглушки]</c>.
/// </summary>
internal static partial class WorkerProgram
{
    /// <summary>Невідомий режим чи аргумент.</summary>
    public const int ExitUsage = 2;

    /// <summary>Недійсна конфігурація <c>Jobs:Workers:*</c>.</summary>
    public const int ExitInvalidConfiguration = 3;

    /// <summary>Наглядач поза Windows.</summary>
    public const int ExitUnsupportedPlatform = 4;

    /// <summary>Ім'я служби наглядача (P2 реєструє її в MSI).</summary>
    public const string ServiceName = "EcrWorker";

    /// <summary>Файл налаштувань поруч з exe; не <c>appsettings.json</c>, бо exe лежить у теці Ecr.Api.</summary>
    public const string SettingsFile = "worker.settings.json";

    private const string Usage =
        "Використання: Ecr.Worker --supervisor [-- аргументи дочірнього]  |  "
        + "Ecr.Worker --child [--eat-mb N] [--hang] [--exit-after-ms N] [--exit-code C]";

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

        var problems = WorkerPoolOptions.TryLoad(builder.Configuration, out var options);
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
            var message = "Недійсна конфігурація пулу воркерів — служба не стартує:"
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

    private static HostApplicationBuilder CreateBuilder(ChildStubOptions? stub)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Configuration.AddJsonFile(SettingsFile, optional: true, reloadOnChange: false);
        builder.Configuration.AddEnvironmentVariables(prefix: "ECR_");
        if (stub is not null)
        {
            builder.Services.AddSingleton(stub);
            builder.Services.AddHostedService<ChildStub>();
        }

        return builder;
    }

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
