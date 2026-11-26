// tools/Ecr.MethodologyScriptMigration/Cli/SgfProvisionCommand.cs
using System.Globalization;
using Ecr.Application;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Infrastructure;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ecr.MethodologyScriptMigration.Cli;

/// <summary>
/// ✎ SGF-flaring: <c>sgf-provision</c> — завести довідники факелів (<see cref="FlaringRegistryProvisioner"/>) БЕЗ повторного
/// імпорту складу газу (на live імпорт разовий). Типово — сухий прогін: усе в транзакції, звіт «що буде створено», відкат.
/// <c>--apply</c> — комміт. Ідемпотентно: наявний довідник не чіпається, повтор нічого не створює. Автор — <c>svc-integration</c>,
/// як у задачі імпорту.
/// </summary>
public static class SgfProvisionCommand
{
    /// <summary>Рядок використання.</summary>
    public const string Usage =
        "  Ecr.MethodologyScriptMigration sgf-provision --connection <рядок> [--apply]   (типово сухий прогін: нічого не пише)";

    /// <summary>Розбирає аргументи й виконує.</summary>
    /// <param name="args">Аргументи (перший — назва команди).</param>
    /// <param name="output">Звіт.</param>
    /// <param name="error">Помилки.</param>
    /// <param name="ct">Скасування.</param>
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        string? connection = null;
        var apply = false;
        string? suffix = null;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--connection" when i + 1 < args.Length:
                    connection = args[++i];
                    break;
                case "--apply":
                    apply = true;
                    break;
                case "--registry-suffix" when i + 1 < args.Length:   // лише тести: довідники зі спільної тестової бази
                    suffix = args[++i];
                    break;
                default:
                    await error.WriteLineAsync($"Невідомий або неповний параметр: {args[i]}\n{Usage}").ConfigureAwait(false);
                    return MigrationCli.UsageOrEnvironment;
            }
        }

        if (string.IsNullOrWhiteSpace(connection))
        {
            await error.WriteLineAsync("sgf-provision потребує --connection.\n" + Usage).ConfigureAwait(false);
            return MigrationCli.UsageOrEnvironment;
        }

        var gas = suffix is null
            ? FlertRegistryCodes.Default
            : new FlertRegistryCodes($"STREAM_{suffix}", $"STREAM_CASE_{suffix}", $"GAS_COMPOSITION_{suffix}", $"COMPONENT_{suffix}");
        await using var provider = Build(connection);
        var report = await ProvisionAsync(provider, gas, apply, ct).ConfigureAwait(false);
        if (report is null)
        {
            await error.WriteLineAsync($"Немає довідників складу газу {gas.Stream}/{gas.Case}: спершу склад газу (імпорт FLERT).").ConfigureAwait(false);
            return MigrationCli.UsageOrEnvironment;
        }

        await output.WriteLineAsync(apply ? "sgf-provision: APPLIED (committed)." : "sgf-provision: DRY RUN — nothing written (rolled back).")
            .ConfigureAwait(false);
        await output.WriteLineAsync(report.Created.Count == 0
                ? "Registries to create: none (all exist; existing registries are never changed)."
                : "Registries to create: " + string.Join(", ", report.Created))
            .ConfigureAwait(false);
        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"Rows: {report.Rows}")).ConfigureAwait(false);
        foreach (var warning in report.Warnings)
        {
            await output.WriteLineAsync("Warning: " + warning).ConfigureAwait(false);
        }

        return MigrationCli.Ok;
    }

    /// <summary>Провізія в одній транзакції; без <paramref name="apply"/> — відкат. <c>null</c> — довідників складу немає.</summary>
    /// <param name="provider">Сервіси (<see cref="Build"/>).</param>
    /// <param name="gas">Коди довідників складу.</param>
    /// <param name="apply">Записати.</param>
    /// <param name="ct">Скасування.</param>
    public static async Task<FlaringRegistryReport?> ProvisionAsync(
        IServiceProvider provider, FlertRegistryCodes gas, bool apply, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(gas);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EcrDbContext>();
        var ids = await db.RegistryDefs.AsNoTracking()
            .Where(d => d.Code == gas.Stream || d.Code == gas.Case)
            .ToDictionaryAsync(d => d.Code, d => d.Id, ct)
            .ConfigureAwait(false);
        if (!ids.TryGetValue(gas.Stream, out var stream) || !ids.TryGetValue(gas.Case, out var @case))
        {
            return null;
        }

        using var author = await services.GetRequiredService<IntegrationActor>().EnterAsync(ct).ConfigureAwait(false);
        FlaringRegistryReport? report = null;
        try
        {
            await services.GetRequiredService<IUnitOfWork>().ExecuteInTransactionAsync(
                async tx =>
                {
                    report = await services.GetRequiredService<FlaringRegistryProvisioner>()
                        .EnsureAsync(FlertCompositionSyncJob.FlaringCodes(gas), stream, @case, tx).ConfigureAwait(false);
                    if (!apply)
                    {
                        throw new DryRunRollback();
                    }
                },
                ct).ConfigureAwait(false);
        }
        catch (DryRunRollback)
        {
            // Сухий прогін: транзакцію відкочено навмисно, звіт уже є.
        }

        return report;
    }

    /// <summary>Граф сервісів: сховища, застосунок, автор задачі (<c>svc-integration</c>).</summary>
    /// <param name="connectionString">Рядок підключення.</param>
    public static ServiceProvider Build(string connectionString)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Ecr"] = connectionString })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEcrApplication();
        services.AddEcrInfrastructure(configuration);
        services.AddScoped<JobActorScope>();
        services.AddScoped<ICurrentUser>(sp => new JobAwareCurrentUser(new NoRequestUser(), sp.GetRequiredService<JobActorScope>()));
        services.AddScoped(_ => new NoJobs());
        services.AddScoped<IBackgroundJobScheduler>(sp => sp.GetRequiredService<NoJobs>().Fail());
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = false, ValidateOnBuild = false });
    }

    private sealed class DryRunRollback : Exception
    {
        public DryRunRollback()
            : base("dry run")
        {
        }

        public DryRunRollback(string message)
            : base(message)
        {
        }

        public DryRunRollback(string message, Exception inner)
            : base(message, inner)
        {
        }
    }

    private sealed class NoJobs
    {
        public IBackgroundJobScheduler Fail()
            => throw new InvalidOperationException("sgf-provision does not schedule background jobs.");
    }

    private sealed class NoRequestUser : ICurrentUser
    {
        public int? UserId => null;

        public string? UserName => "sgf-provision";

        public string CorrelationId => "sgf-provision";

        public string Language => "en";

        public IReadOnlyList<string> GroupSids => [];
    }
}
