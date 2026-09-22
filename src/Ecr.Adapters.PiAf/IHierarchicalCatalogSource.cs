using Ecr.Application.Ports;

namespace Ecr.Adapters.PiAf;

/// <summary>
/// Адаптер, що вміє читати каталог джерела ліниво — рівень за рівнем, на
/// запитаний вузол, а не весь AF одразу через <see cref="IExternalDataSource.DiscoverAsync"/>.
/// </summary>
public interface IHierarchicalCatalogSource
{
    /// <summary>Прямі дочірні елементи вузла; <c>null</c> — корінь бази AF.</summary>
    public Task<IReadOnlyList<SourceEntityDescriptor>> BrowseAsync(
        int dataSourceId, string? parentPath, CancellationToken ct);

    /// <summary>Атрибути елемента з UOM джерела.</summary>
    public Task<IReadOnlyList<SourceEntityDescriptor>> AttributesAsync(
        int dataSourceId, string elementPath, CancellationToken ct);
}
