// src/Ecr.Application/Ports/IUiStringCatalog.cs
namespace Ecr.Application.Ports;

/// <summary>
/// Область каталогу (<c>D-114</c>). Значення збігаються з
/// <c>sys_ecr.UiString.Scope</c>.
/// </summary>
public enum UiStringScope : byte
{
    /// <summary>Віддається **анонімно**: сторінка входу, chrome, помилки входу.</summary>
    Public = 0,

    /// <summary>Лише після входу: підписи адміністративних областей і назви прав.</summary>
    Private = 1,
}

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

    /// <summary>
    /// Те саме, але лише одна область (<c>D-114</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ Окремий метод, а не параметр у контрактному <see cref="GetAsync(string, CancellationToken)"/>:
    /// підпис контракту лишається без змін, а поділ областей без нього
    /// невиразний — анонімний каталог із назвами адміністративних областей і
    /// прав розкрив би поверхню функціоналу тому, хто ще не увійшов (ФВ-14.2,
    /// <c>Q-073</c>).
    /// </remarks>
    public Task<UiStringCatalog> GetScopedAsync(string languageCode, UiStringScope scope, CancellationToken ct);

    /// <summary>Поточна версія каталогу. Змінюється будь-яким записом.</summary>
    public Task<int> GetRevisionAsync(CancellationToken ct);

    /// <summary>
    /// Записує рядок і **одним** statement піднімає версію каталогу.
    /// </summary>
    /// <remarks>
    /// Інкремент і читання нової версії розділити не можна: два адміністратори,
    /// що правлять переклад одночасно, отримали б однакову версію, і другий
    /// <c>ETag</c> збігся б із першим при різному вмісті (R-B7).
    /// </remarks>
    public Task<UiStringWriteResult> SetAsync(UiStringWrite write, CancellationToken ct);
}

/// <param name="LanguageCode">Мова зрізу.</param>
/// <param name="Revision">Версія каталогу; слугує <c>ETag</c>.</param>
/// <param name="Strings">Ключ → текст, уже з розгорнутим fallback.</param>
public sealed record UiStringCatalog(
    string LanguageCode,
    int Revision,
    IReadOnlyDictionary<string, string> Strings);

/// <summary>Запис рядка каталогу.</summary>
/// <param name="Key">Ключ, напр. <c>nav.templates</c> або <c>err.ECR-PWD-0428</c>.</param>
/// <param name="LanguageCode">Мова.</param>
/// <param name="Value">Текст.</param>
/// <param name="Scope">Область видимості.</param>
/// <param name="ModifiedByUserId">Автор.</param>
/// <param name="ModifiedAt">Момент зміни в UTC.</param>
public sealed record UiStringWrite(
    string Key,
    string LanguageCode,
    string Value,
    UiStringScope Scope,
    int? ModifiedByUserId,
    DateTime ModifiedAt);

/// <summary>Результат запису.</summary>
/// <param name="Revision">Нова версія каталогу.</param>
/// <param name="PreviousScope">Область до запису; <c>null</c> — рядка не було.</param>
public sealed record UiStringWriteResult(int Revision, UiStringScope? PreviousScope);
