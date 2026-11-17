using System.Collections.Concurrent;
using System.Globalization;
using Ecr.Scripting;

namespace Ecr.Calculations;

/// <summary>
/// Кеш скомпільованих скриптів (MS, план §4.4): ключ — версія методології, хеш її тексту й підпис бібліотек,
/// з якими компілювали. Singleton: компіляція одного тексту потрібна раз на процес, а не на прив'язку.
/// </summary>
/// <remarks>
/// ⚠ Місткість обмежена: при переповненні кеш очищається цілком (скриптів у системі десятки, переповнення —
/// ознака того, що ключі течуть, і вибирати «найстаріший» безглуздо). Стан версії не кешується — лише
/// чисте перетворення «текст + бібліотеки → програма», тому застарілого запису бути не може: інший текст дає
/// інший хеш, а інша редакція бібліотеки — інший підпис.
/// </remarks>
public sealed class ScriptProgramCache
{
    /// <summary>Скільки програм тримає кеш.</summary>
    public const int Capacity = 256;

    private readonly ConcurrentDictionary<string, CompiledScript> _items = new(StringComparer.Ordinal);

    /// <summary>Скільки програм у кеші зараз.</summary>
    public int Count => _items.Count;

    /// <summary>Ключ запису кешу.</summary>
    /// <param name="versionId">Версія методології.</param>
    /// <param name="hash">Хеш тексту.</param>
    /// <param name="librariesSignature">Підпис бібліотек (код:версія:хеш через кому); порожній — імпортів немає.</param>
    public static string Key(int versionId, byte[] hash, string librariesSignature)
    {
        ArgumentNullException.ThrowIfNull(hash);
        ArgumentNullException.ThrowIfNull(librariesSignature);

        return string.Create(CultureInfo.InvariantCulture, $"{versionId}|{Convert.ToHexString(hash)}|{librariesSignature}");
    }

    /// <summary>Повертає програму з кешу або компілює й кладе її.</summary>
    /// <param name="key">Ключ.</param>
    /// <param name="compile">Компіляція; викликається лише за відсутності запису.</param>
    public CompiledScript GetOrAdd(string key, Func<CompiledScript> compile)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(compile);

        if (_items.TryGetValue(key, out var found))
        {
            return found;
        }

        var compiled = compile();
        if (_items.Count >= Capacity)
        {
            _items.Clear();
        }

        _items[key] = compiled;
        return compiled;
    }
}
