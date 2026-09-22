// src/Ecr.Application/Ports/ISourceCatalogReader.cs

namespace Ecr.Application.Ports;

/// <summary>
/// Каталог імен джерела для конфігуратора мапінгу (ФВ-13.13): імена обираються
/// зі списку, а не вводяться руками.
/// </summary>
/// <remarks>
/// Реалізація — <c>PiAfCatalogReader</c> в адаптері PI AF; порт існує, бо
/// застосунок не бачить збірки адаптерів. Тільки читання (D-44).
/// </remarks>
public interface ISourceCatalogReader
{
    /// <summary>Елементи під вузлом; <paramref name="parentPath"/> <c>null</c> — корінь.</summary>
    public Task<IReadOnlyList<SourceEntityDescriptor>> BrowseAsync(
        int dataSourceId, string? parentPath, CancellationToken ct);

    /// <summary>Атрибути елемента разом з одиницею джерела.</summary>
    public Task<IReadOnlyList<SourceEntityDescriptor>> AttributesAsync(
        int dataSourceId, string elementPath, CancellationToken ct);
}
