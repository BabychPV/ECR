using Ecr.Application;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Calculations;
using Ecr.Calculations.Scripting.Parity;
using Ecr.Domain.Abstractions;
using Ecr.Infrastructure;
using Ecr.MethodologyScriptMigration.Loading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ecr.MethodologyScriptMigration.Cli;

/// <summary>
/// Складання графа сервісів для команди <c>parity</c>: справжні сховища (<c>AddEcrInfrastructure</c>), рушій розрахунків
/// (<c>AddEcrCalculations</c>) і застосунок (<c>AddEcrApplication</c> — дефолти колонок), плюс перелік цілей
/// <see cref="EfParityTargetSource"/> і <see cref="ParityRunner"/>.
/// </summary>
/// <remarks>
/// Нічого, що пише в базу, тут не викликається: модулі лише повертають значення. Користувач і розклад задач — заглушки:
/// жоден сервіс графа порівняння їх не використовує.
/// </remarks>
public static class ParityHost
{
    /// <summary>Будує постачальника сервісів для рядка підключення.</summary>
    /// <param name="connectionString">Рядок підключення до копії бази.</param>
    public static ServiceProvider Build(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Ecr"] = connectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEcrInfrastructure(configuration);
        services.AddEcrCalculations();
        services.AddEcrApplication();
        services.AddSingleton<ICurrentUser, ToolUser>();
        services.AddScoped<IParityTargetSource, EfParityTargetSource>();
        services.AddScoped<ParityRunner>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = false, ValidateOnBuild = false });
    }

    private sealed class ToolUser : ICurrentUser
    {
        public int? UserId => null;

        public string? UserName => "methodology-script-migration";

        public string CorrelationId => "parity";

        public string Language => "en";

        public IReadOnlyList<string> GroupSids => [];
    }
}
