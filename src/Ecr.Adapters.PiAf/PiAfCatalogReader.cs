using Ecr.Application.Errors;
using Ecr.Application.Ports;

namespace Ecr.Adapters.PiAf;

/// <summary>
/// Читає каталог елементів і атрибутів PI AF для конфігуратора.
/// </summary>
/// <remarks>
/// Потрібен, щоб імена сутностей джерела **обиралися зі списку**, а не
/// вводилися руками: друкарська помилка в шляху AF виявляється не при
/// налаштуванні, а через місяць порожнім збором.
///
/// ⛔ Тільки читання. Методів, які створюють щось у базі джерела, тут немає і
/// не буде (<c>D-44</c>, ФВ-11.2: «адаптер не створює артефактів у базі джерела»).
/// </remarks>
public sealed class PiAfCatalogReader(IEnumerable<IExternalDataSource> sources, ICollectionStore store)
{
    /// <summary>Скільки вузлів одного рівня має сенс показати.</summary>
    /// <remarks>
    /// Рівень із тисячею вузлів людина не переглядає — вона шукає. Стеля тут
    /// існує, щоб конфігуратор не намагався намалювати те, чим не можна
    /// скористатися.
    /// </remarks>
    public const int MaxNodesPerLevel = 1_000;

    private const string SourceUnavailable = "ECR-INT-0503";

    /// <summary>Дерево елементів бази AF.</summary>
    /// <param name="dataSourceId">Джерело, з якого читаємо.</param>
    /// <param name="parentPath">Вузол; <c>null</c> — корінь.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⚠ Обхід **порівнями**, а не вглиб: база AF велика, і повний обхід у
    /// конфігураторі означав би хвилини очікування на дереві, у якому
    /// користувач відкриє два вузли.
    /// </remarks>
    public async Task<IReadOnlyList<SourceEntityDescriptor>> BrowseAsync(
        int dataSourceId, string? parentPath, CancellationToken ct)
    {
        var descriptors = await DiscoverAsync(dataSourceId, ct).ConfigureAwait(false);

        return descriptors
            .Where(d => IsChildOf(d.EntityPath, parentPath))
            .OrderBy(d => d.Code, StringComparer.Ordinal)
            .Take(MaxNodesPerLevel)
            .ToList();
    }

    /// <summary>Атрибути елемента разом із їхнім UOM.</summary>
    /// <param name="dataSourceId">Джерело, з якого читаємо.</param>
    /// <param name="elementPath">Елемент, чиї атрибути потрібні.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// UOM обов'язковий у результаті: одиниця джерела — «найчастіше джерело
    /// мовчазних розбіжностей у числах» (ФВ-16.9), і побачити її треба вже
    /// при налаштуванні, а не при звірці.
    /// <para>
    /// ⚠ Атрибут без UOM віддається з <c>SourceUnitSymbol = null</c> —
    /// відсутність одиниці **видно**. Підставлена мовчки безрозмірність
    /// виглядала б як свідоме рішення того, хто налаштовував.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<SourceEntityDescriptor>> AttributesAsync(
        int dataSourceId, string elementPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(elementPath);

        var descriptors = await DiscoverAsync(dataSourceId, ct).ConfigureAwait(false);

        return descriptors
            .Where(d => d.DataType is not "Element")
            .Where(d => string.Equals(d.EntityPath, elementPath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.Code, StringComparer.Ordinal)
            .Take(MaxNodesPerLevel)
            .ToList();
    }

    /// <summary>Каталог від адаптера, обраного за транспортом джерела.</summary>
    private async Task<IReadOnlyList<SourceEntityDescriptor>> DiscoverAsync(
        int dataSourceId, CancellationToken ct)
    {
        var source = await store.FindDataSourceAsync(dataSourceId, ct).ConfigureAwait(false)
                     ?? throw new BusinessRuleException(
                         SourceUnavailable,
                         $"Джерело {dataSourceId} не існує або вимкнене.",
                         new Dictionary<string, object?> { ["dataSourceId"] = dataSourceId });

        // Транспорт — налаштування, не гілка коду (ФВ-11.2): каталог читає той
        // самий адаптер, який потім збиратиме дані. Інакше конфігуратор
        // показував би список, з якого частина позицій не збирається.
        var adapter = sources.FirstOrDefault(s => s.Transport == source.Transport)
                      ?? throw new BusinessRuleException(
                          SourceUnavailable,
                          $"Транспорт {source.Transport} не зареєстровано.");

        return await adapter.DiscoverAsync(dataSourceId, ct).ConfigureAwait(false);
    }

    /// <summary>Чи є вузол прямою дитиною батька.</summary>
    /// <remarks>
    /// Корінь — це вузол без батька або з батьком у один сегмент. Порівняння
    /// без урахування регістру: AF регістру в іменах не розрізняє, і вимога
    /// точного збігу дала б порожній рівень там, де вузли є.
    /// </remarks>
    private static bool IsChildOf(string? path, string? parentPath)
    {
        if (string.IsNullOrWhiteSpace(parentPath))
        {
            return true;
        }

        return path is not null
               && path.StartsWith(parentPath, StringComparison.OrdinalIgnoreCase)
               && path.Length > parentPath.Length;
    }
}
