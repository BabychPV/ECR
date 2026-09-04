using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.Infrastructure.Startup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ecr.Infrastructure;

/// <summary>Реєстрація інфраструктури в контейнері.</summary>
public static class DependencyInjection
{
    /// <summary>Додає EF Core, сховища, кеш, безпеку і планувальник.</summary>
    public static IServiceCollection AddEcrInfrastructure(this IServiceCollection services, IConfiguration configuration)
        => throw new NotImplementedException(
            "TODO:\n" +
            "services.AddDbContext<EcrDbContext>(o => o.UseSqlServer(cs, sql => {\n" +
            "    sql.CommandTimeout(...); sql.EnableRetryOnFailure(); }));\n" +
            "AddScoped<ICellStore, NormalizedCellStore>();  // Hybrid — лише за результатом гейта\n" +
            "AddSingleton<IMetadataCache, MetadataCache>();\n" +
            "AddScoped<IAccessDecisionService, AccessDecisionService>();\n" +
            "AddSingleton<IPasswordHasher, PasswordHasher>();\n" +
            "AddSingleton<ISqlCapabilities, SqlCapabilitiesProbe>();\n" +
            "AddScoped<IUnitOfWork, UnitOfWork>();\n" +
            "AddScoped<IAuditWriter, AuditWriter>();\n" +
            "AddSingleton<IBackgroundJobScheduler, QuartzJobScheduler>();\n" +
            "AddQuartz(...) + AddQuartzHostedService();\n" +
            "AddDistributedSqlServerCache(...)  // без Redis (D-06)\n" +
            "AddMemoryCache();\n" +
            "AddSingleton<IClock, SystemClock>();\n" +
            "⚠ IExternalDataSink НЕ реєструється: його не існує (D-44).");
}
