using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ecr.Application.Registries;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Кеш складу газу з документів HSE400 (PERF-11, аудит 21.11): один на процес, ключ — ВІДБИТОК усіх входів складу.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Коректність важливіша за швидкість: запис кешу читається лише за ТОЧНОГО збігу відбитка, який
/// <c>Hse400CompositionSnapshotLoader</c> рахує на кожному виклику дешевими запитами (екземпляри, рядки, описи, актуальні
/// прогони, затверджені місяці — без читання комірок). Жодного TTL і жодної ручної інвалідації: інше значення будь-якого
/// входу — інший відбиток, старий запис просто перестає адресуватися.
/// </para>
/// <para>
/// ⚠ Що саме бачить відбиток: кількість, найбільший і СУМА <c>doc.TableRow.RowVersion</c> обраних рядків (версію піднімає
/// кожен запис комірок через <c>NormalizedCellStore</c>; сума — бо версії видаються на початку транзакцій, а комітяться в
/// іншому порядку: максимум і кількість не бачать коміту «старішої» транзакції, сума бачить); набір обраних екземплярів
/// (інший «останній документ року» чи видалений рядок); коди таблиць-потоків; код/порядок рядків і місячні колонки; Id
/// актуальних прогонів (нові розрахункові <c>F_&lt;X&gt;</c> = новий актуальний прогін); затверджені місяці.
/// ⛔ Шляхи, що пишуть комірки БЕЗ піднімання версії рядка (перерахунок формул документа, прив'язка до дозволів), відбитку
/// невидимі: тому за наявності формули чи прив'язки на таблицях складу завантажувач кеш не вмикає (див. його remarks).
/// Пряма SQL-правка <c>doc.CellValue</c> без версії рядка також не видна — як і оптимістичному блокуванню рядка.
/// </para>
/// <para>
/// ⚠ Пам'ять процесу (PERF-17): кожен вузол Api/воркера має свій кеш і однаково коректний — відбиток читається з бази, а не
/// передається між вузлами. Один запис на область (<c>сервер|база|шаблон|довідник</c>) — найновіший; областей мало, стеля
/// <see cref="MaxScopes"/> захищає від розростання.
/// </para>
/// </remarks>
public sealed class Hse400CompositionCache
{
    /// <summary>Скільки областей тримати (база × шаблон × довідник); понад — кеш скидається цілком.</summary>
    public const int MaxScopes = 16;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private long _hits;
    private long _misses;

    /// <summary>Скільки разів склад узято з кешу.</summary>
    public long Hits => Interlocked.Read(ref _hits);

    /// <summary>Скільки разів склад довелося читати з документів.</summary>
    public long Misses => Interlocked.Read(ref _misses);

    /// <summary>Склад під цим відбитком, якщо він у кеші.</summary>
    /// <param name="scope">Область (<c>сервер|база|шаблон|довідник</c>).</param>
    /// <param name="fingerprint">Відбиток входів.</param>
    /// <param name="value">Склад і роки документів.</param>
    /// <returns><c>true</c> — збіг відбитка.</returns>
    internal bool TryGet(
        string scope, string fingerprint, out (IReadOnlyList<Hse400CompositionRow> Rows, IReadOnlyCollection<int> Years) value)
    {
        if (_entries.TryGetValue(scope, out var entry) && string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _hits);
            value = (entry.Rows, entry.Years);
            return true;
        }

        Interlocked.Increment(ref _misses);
        value = default;
        return false;
    }

    /// <summary>Кладе склад під відбиток (замінює запис області).</summary>
    /// <param name="scope">Область.</param>
    /// <param name="fingerprint">Відбиток входів.</param>
    /// <param name="value">Склад і роки документів (незмінні записи).</param>
    internal void Set(string scope, string fingerprint, (IReadOnlyList<Hse400CompositionRow> Rows, IReadOnlyCollection<int> Years) value)
    {
        if (_entries.Count >= MaxScopes && !_entries.ContainsKey(scope))
        {
            _entries.Clear();
        }

        _entries[scope] = new Entry(fingerprint, value.Rows, value.Years);
    }

    /// <summary>Скидає кеш (тести, діагностика).</summary>
    public void Clear() => _entries.Clear();

    /// <summary>Відбиток входів складу: SHA-256 за впорядкованим текстовим поданням.</summary>
    internal static string Fingerprint(
        bool approvedOnly,
        IEnumerable<(long Id, long DocumentId, int TableDefId, int PeriodKey)> instances,
        long rowCount,
        byte[] rowStamp,
        decimal rowSum,
        IReadOnlyDictionary<int, string> tableCodes,
        IEnumerable<(int TableDefId, string RowKey, int Ordinal)> rowDefs,
        IEnumerable<(int Id, int TableDefId, string Code, int Month)> monthColumns,
        IEnumerable<(long DocumentId, int PeriodKey, long RunId)> currentRuns,
        IEnumerable<(long DocumentId, int TableDefId, int Year, int Month)> approvedMonths)
    {
        var text = new StringBuilder();
        text.Append(approvedOnly ? "A" : "Y").Append('\n');
        Section(text, "i", instances.OrderBy(x => x.Id), x => F($"{x.Id},{x.DocumentId},{x.TableDefId},{x.PeriodKey}"));
        text.Append("[r]").Append(CultureInfo.InvariantCulture, $"{rowCount},{rowSum},").Append(Convert.ToHexString(rowStamp)).Append((char)10);
        Section(text, "t", tableCodes.OrderBy(x => x.Key), x => F($"{x.Key},{x.Value}"));
        Section(text, "d", rowDefs.OrderBy(x => x.TableDefId).ThenBy(x => x.RowKey, StringComparer.Ordinal).ThenBy(x => x.Ordinal),
            x => F($"{x.TableDefId},{x.RowKey},{x.Ordinal}"));
        Section(text, "c", monthColumns.OrderBy(x => x.Id), x => F($"{x.Id},{x.TableDefId},{x.Code},{x.Month}"));
        Section(text, "u", currentRuns.OrderBy(x => x.DocumentId).ThenBy(x => x.PeriodKey).ThenBy(x => x.RunId),
            x => F($"{x.DocumentId},{x.PeriodKey},{x.RunId}"));
        Section(text, "a", approvedMonths.OrderBy(x => x.DocumentId).ThenBy(x => x.TableDefId).ThenBy(x => x.Year).ThenBy(x => x.Month),
            x => F($"{x.DocumentId},{x.TableDefId},{x.Year},{x.Month}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static string F(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);

    private static void Section<T>(StringBuilder text, string tag, IEnumerable<T> items, Func<T, string> render)
    {
        text.Append('[').Append(tag).Append(']');
        foreach (var item in items)
        {
            text.Append(render(item)).Append(';');
        }

        text.Append('\n');
    }

    private sealed record Entry(string Fingerprint, IReadOnlyList<Hse400CompositionRow> Rows, IReadOnlyCollection<int> Years);
}
