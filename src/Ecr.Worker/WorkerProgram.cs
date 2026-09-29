// src/Ecr.Worker/WorkerProgram.cs

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ecr.Worker;

/// <summary>
/// Точка входу: <c>--child [аргументи заглушки]</c>.
/// </summary>
internal static class WorkerProgram
{
    /// <summary>Невідомий режим чи аргумент.</summary>
    public const int ExitUsage = 2;

    /// <summary>Файл налаштувань поруч з exe; не <c>appsettings.json</c>, бо exe лежить у теці Ecr.Api.</summary>
    public const string SettingsFile = "worker.settings.json";

    private const string Usage =
        "Використання: Ecr.Worker --child [--eat-mb N] [--hang] [--exit-after-ms N] [--exit-code C]";

    /// <summary>Запускає режим за першим аргументом; повертає код виходу.</summary>
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);

        var rest = args.Skip(1).ToList();
        if (args.FirstOrDefault() == "--child" && ChildStubOptions.Parse(rest) is { } stub)
        {
            using var host = CreateBuilder(stub).Build();
            await host.RunAsync(cancellationToken).ConfigureAwait(false);
            return Environment.ExitCode;
        }

        await Console.Error.WriteLineAsync(Usage).ConfigureAwait(false);
        return ExitUsage;
    }

    private static HostApplicationBuilder CreateBuilder(ChildStubOptions stub)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Configuration.AddJsonFile(SettingsFile, optional: true, reloadOnChange: false);
        builder.Configuration.AddEnvironmentVariables(prefix: "ECR_");
        builder.Services.AddSingleton(stub);
        builder.Services.AddHostedService<ChildStub>();
        return builder;
    }
}
