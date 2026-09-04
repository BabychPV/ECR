// src/Ecr.Application/Ports/IUiStringCatalog.cs
namespace Ecr.Application.Ports;

/// <summary>
/// Каталог рядків інтерфейсу (<c>ФВ-14.9</c>, <c>D-95</c>). Тут живуть і підписи
/// UI, і тексти помилок під ключами <c>err.&lt;код&gt;</c> (<c>ФВ-14.9a</c>,
/// <c>D-111</c>) — механізм локалізації один, не два.
/// </summary>
public interface IUiStringCatalog
{
    /// <summary>
    /// Весь каталог мови плюс версія для <c>ETag</c>. Відсутній ключ у мові
    /// підмінюється мовою за замовчуванням; ключа немає ніде — повертається
    /// сам ключ. Одна забута локалізація не має ламати екран.
    /// </summary>
    public Task<UiStringCatalog> GetAsync(string languageCode, CancellationToken ct);

    /// <summary>Поточна версія каталогу. Змінюється будь-яким записом.</summary>
    public Task<int> GetRevisionAsync(CancellationToken ct);
}

/// <param name="LanguageCode">Мова зрізу.</param>
/// <param name="Revision">Версія каталогу; слугує <c>ETag</c>.</param>
/// <param name="Strings">Ключ → текст, уже з розгорнутим fallback.</param>
public sealed record UiStringCatalog(
    string LanguageCode,
    int Revision,
    IReadOnlyDictionary<string, string> Strings);
