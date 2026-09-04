using Microsoft.Extensions.DependencyInjection;

namespace Ecr.Adapters.PiAf;

/// <summary>Реєстрація адаптерів PI AF.</summary>
public static class DependencyInjection
{
    /// <summary>Додає обидва транспорти читання.</summary>
    /// <remarks>
    /// ⚠ Поки не реєструє нічого: <c>CollectionRunner</c> і
    /// <c>CatchUpPlanner</c> залежать від <c>ICollectionStore</c>, чия
    /// реалізація належить Етапу 5 (`Q-051`).
    ///
    /// ⚠ <c>IExternalDataSink</c> не реєструється ніколи — його не існує
    /// (D-44): система читає із зовнішніх джерел і не пише в них.
    /// </remarks>
    public static IServiceCollection AddPiAfAdapters(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}
