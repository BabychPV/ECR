using Ecr.Application.Ports;
using Ecr.Domain.Enums;

namespace Ecr.Adapters.PiAf;

/// <summary>
/// Читання через PI Web API (HTTP).
/// </summary>
/// <remarks>
/// Використовується для того, чого не вміє RTQP. **Методів запису тут немає
/// навмисно** (`D-44`): Web API їх підтримує, але система в AF не пише нічого.
/// </remarks>
public sealed class PiWebApiDataSource(HttpClient http) : IExternalDataSource
{
    /// <inheritdoc />
    public ExternalTransport Transport => ExternalTransport.PiWebApi;

    /// <inheritdoc />
    public Task<IReadOnlyList<SourceEntityDescriptor>> DiscoverAsync(int dataSourceId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: обхід ієрархії елементів через /assetdatabases/{id}/elements; " +
            "збирати атрибути з їхнім UOM.");

    /// <inheritdoc />
    public Task<CollectionResult> ReadAsync(CollectionRequest request, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: /streamsets/recorded або /value з фільтром часу; батчі обмеженого розміру; " +
            "ретраї з експоненційним відступом на 5xx і таймаутах; " +
            "часткова відмова батча — це НЕ загальний провал: успішні точки зберегти, " +
            "невдалі повернути в catch-up.");
}
