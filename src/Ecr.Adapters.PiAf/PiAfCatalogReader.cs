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
public sealed class PiAfCatalogReader
{
    /// <summary>Дерево елементів бази AF.</summary>
    /// <param name="dataSourceId">Джерело, з якого читаємо.</param>
    /// <param name="parentPath">Вузол; <c>null</c> — корінь.</param>
    /// <param name="ct">Скасування.</param>
    public Task<IReadOnlyList<SourceEntityDescriptor>> BrowseAsync(
        int dataSourceId, string? parentPath, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: обхід ієрархії ПОРІВНЯМИ, а не рекурсією вглиб: база AF велика, і " +
            "повний обхід у конфігураторі означав би хвилини очікування. " +
            "Повертати елементи одного рівня плюс ознаку, чи є в них діти.");

    /// <summary>Атрибути елемента разом із їхнім UOM.</summary>
    /// <remarks>
    /// UOM обов'язковий у результаті: одиниця джерела — «найчастіше джерело
    /// мовчазних розбіжностей у числах» (ФВ-16.9), і побачити її треба вже
    /// при налаштуванні, а не при звірці.
    /// </remarks>
    public Task<IReadOnlyList<SourceEntityDescriptor>> AttributesAsync(
        int dataSourceId, string elementPath, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: атрибути елемента з їхнім UOM і типом значення; атрибут без UOM " +
            "позначати явно, а не підставляти безрозмірність мовчки.");
}
