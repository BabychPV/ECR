using Ecr.Application.Localization;
using Ecr.Application.Ports;

namespace Ecr.TestKit;

/// <summary>
/// Каталог рядків у пам'яті: ті самі правила fallback і версії, без бази.
/// </summary>
/// <remarks>
/// Fallback і композицію бере з <see cref="UiStringResolver"/>, а не повторює.
/// Копія правил у підробці означала б, що тест перевіряє підробку.
/// </remarks>
public sealed class FakeUiStringCatalog : IUiStringCatalog
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Language, string Key), Row> _rows = [];
    private int _revision = 1;

    /// <summary>Поточна версія каталогу.</summary>
    public int Revision
    {
        get
        {
            lock (_gate)
            {
                return _revision;
            }
        }
    }

    /// <summary>Додає рядок.</summary>
    /// <param name="languageCode">Мова.</param>
    /// <param name="key">Ключ.</param>
    /// <param name="value">Текст.</param>
    /// <param name="scope">Область.</param>
    public FakeUiStringCatalog Add(
        string languageCode, string key, string value, UiStringScope scope = UiStringScope.Private)
    {
        lock (_gate)
        {
            _rows[(languageCode, key)] = new Row(value, scope);
        }

        return this;
    }

    /// <inheritdoc />
    public Task<UiStringCatalog> GetAsync(string languageCode, CancellationToken ct)
        => Task.FromResult(Build(languageCode, scope: null));

    /// <inheritdoc />
    public Task<UiStringCatalog> GetScopedAsync(string languageCode, UiStringScope scope, CancellationToken ct)
        => Task.FromResult(Build(languageCode, scope));

    /// <inheritdoc />
    public Task<int> GetRevisionAsync(CancellationToken ct) => Task.FromResult(Revision);

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Мови беруться з ДОДАНИХ рядків, а не зі списку в підробці. Інакше
    /// фікстура знала б більше за систему: тест «переклад казахською видно»
    /// проходив би й тоді, коли казахської в каталозі немає жодного рядка.
    /// Мова за замовчуванням названа одна — та сама, що в `UiStringResolver`.
    /// </remarks>
    public Task<IReadOnlyList<LanguageDto>> ListLanguagesAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<LanguageDto> languages =
            [
                .. _rows.Keys
                    .Select(key => key.Language)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .Select(code => new LanguageDto(
                        code,
                        code,
                        string.Equals(
                            code, UiStringResolver.DefaultLanguage, StringComparison.OrdinalIgnoreCase)))
            ];

            return Task.FromResult(languages);
        }
    }

    /// <inheritdoc />
    public Task<UiStringWriteResult> SetAsync(UiStringWrite write, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(write);

        lock (_gate)
        {
            var id = (write.LanguageCode, write.Key);
            var previous = _rows.TryGetValue(id, out var existing) ? existing.Scope : (UiStringScope?)null;

            _rows[id] = new Row(write.Value, write.Scope);

            // Запис і інкремент версії — одна неподільна операція, як OUTPUT у
            // сховищі: інакше два записи могли б отримати ту саму версію.
            _revision++;
            return Task.FromResult(new UiStringWriteResult(_revision, previous));
        }
    }

    private UiStringCatalog Build(string languageCode, UiStringScope? scope)
    {
        lock (_gate)
        {
            var requested = Slice(languageCode, scope);
            var defaults = string.Equals(
                languageCode, UiStringResolver.DefaultLanguage, StringComparison.OrdinalIgnoreCase)
                ? requested
                : Slice(UiStringResolver.DefaultLanguage, scope);

            return new UiStringCatalog(
                languageCode, _revision, UiStringResolver.Compose(defaults, requested));
        }
    }

    private Dictionary<string, string> Slice(string languageCode, UiStringScope? scope)
        => _rows
            .Where(r => string.Equals(r.Key.Language, languageCode, StringComparison.OrdinalIgnoreCase)
                        && (scope is null || r.Value.Scope == scope))
            .ToDictionary(r => r.Key.Key, r => r.Value.Value, StringComparer.Ordinal);

    private sealed record Row(string Value, UiStringScope Scope);
}
