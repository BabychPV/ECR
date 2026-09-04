using Ecr.Application.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace Ecr.Adapters.PiAf;

/// <summary>Реєстрація адаптерів PI AF.</summary>
public static class DependencyInjection
{
    /// <summary>Додає обидва транспорти читання.</summary>
    public static IServiceCollection AddPiAfAdapters(this IServiceCollection services)
        => throw new NotImplementedException(
            "TODO:\n" +
            "AddScoped<IExternalDataSource, PiSqlClientDataSource>();\n" +
            "AddScoped<IExternalDataSource, PiWebApiDataSource>();  // резолвиться як колекція\n" +
            "AddScoped<CollectionRunner>();\n" +
            "AddScoped<CatchUpPlanner>();\n" +
            "AddScoped<SourceUnitConverter>();\n" +
            "AddHttpClient<PiWebApiDataSource>(...) з таймаутом і politikою ретраїв;\n" +
            "⚠ IExternalDataSink не реєструється — його не існує (D-44).");
}
