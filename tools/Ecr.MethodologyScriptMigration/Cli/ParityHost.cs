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
    /// <param name="actingUserId">
    /// ✎ D-571: від чийого імені діють обробники <c>apply</c>/<c>switch</c> на КОПІЇ (права й журнал — цього користувача);
    /// <c>null</c> — анонімно (паритет нічого не пише й користувача не потребує).
    /// </param>
    public static ServiceProvider Build(string connectionString, int? actingUserId = null)
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
        services.AddSingleton<ICurrentUser>(new ToolUser(actingUserId));
        services.AddScoped<IParityTargetSource, EfParityTargetSource>();
        services.AddScoped<ParityRunner>();

        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = false, ValidateOnBuild = false });

        // ✎ P-1124-2 (F-3): реєстр розкладок шаблонів у хості API наповнює фонова служба (TemplateLayoutRegistryRefresher),
        // якої тут немає. Без неї реєстр порожній, і для річних версій (YearWithMonthColumns, Land 1.0.19+) читання йде
        // ФІЗИЧНИМ зрізом періоду, а не логічним (Q-17): не-місячні колонки якоря (статус D-425, коди) «зникають», і
        // паритет/прогони інструмента бачать інші входи, ніж прогін API. Тому реєстр наповнюється один раз тут.
        provider.GetRequiredService<ITemplateLayoutRegistry>().RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
        return provider;
    }

    private sealed class ToolUser(int? userId) : ICurrentUser
    {
        public int? UserId => userId;

        public string? UserName => "methodology-script-migration";

        public string CorrelationId => "parity";

        public string Language => "en";

        public IReadOnlyList<string> GroupSids => [];
    }
}
