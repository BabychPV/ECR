using System.Collections.Concurrent;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;

namespace Ecr.Calculations;

/// <summary>
/// PSS-08: прочитане фазою методологій ОДНОГО виклику <see cref="CalculationOrchestrator"/> (документ × період) —
/// екземпляри таблиць, ключі рядків і зрізи комірок, спільні для всіх прив'язок і паралельних гілок.
/// </summary>
/// <remarks>
/// ⛔ Чому це безпечно. Між першим і останнім читанням фаза методологій у <c>doc.CellValue</c> не пише нічого:
/// <see cref="CalculationOutputWriter"/> пише лише <c>calc.*</c> (D-69), фаза формул цього періоду вже завершилась, а
/// лок документа тримає задача. Результати інших методологій ЦЬОГО прогону (колонки <c>Calculated</c>, MS §13.9 —
/// «B читає числа A») тут НЕ кешуються: їх читає <see cref="ScriptRuntime"/> з <c>calc.CalculationResult</c> у пізнішій
/// пачці, після запису A.
/// <para>
/// ⛔ Межі життя. Кеш створюється на ОДИН виклик <c>RunAsync</c> і з ним зникає — не на <c>CalculationRunId</c>: задача
/// кличе той самий прогін для кожного документа × періоду, а між викликами пише формули наступного періоду, і зріз
/// перегляду місячних колонок (<c>MonthRoutedCellStore</c>) збирається з кількох періодів. Ключ — екземпляр і період,
/// з яким читали, тож зріз однієї таблиці не віддається іншій.
/// </para>
/// <para>
/// ⚠ Пам'ять. Утримується не більше <see cref="MaxRetainedCells"/> комірок (зрізи й доповнені дефолтами зрізи разом);
/// прочитане понад бюджет віддається викликачу, але не запам'ятовується — наступна прив'язка читає сама, як до PSS-08.
/// Типовий бюджет — <c>MaxParallelism × MaxInputCellsPerBinding</c>: стільки ж могли одночасно тримати паралельні гілки
/// й раніше.
/// </para>
/// <para>
/// ⚠ Потоки. Завантаження виконує ПЕРШИЙ викликач власними сховищами (свій scope і <c>DbContext</c>, Q-249); інші гілки
/// лише чекають його <see cref="Task"/> і контекстів чужого scope не торкаються. Невдале читання з кешу прибирається.
/// Віддані колекції спільні між гілками — споживачі їх лише читають.
/// </para>
/// </remarks>
internal sealed class MethodologyPhaseReadCache(long maxRetainedCells)
{
    private readonly ConcurrentDictionary<long, Lazy<Task<TableInstanceRef>>> _instances = new();
    private readonly ConcurrentDictionary<(long Instance, int Period), Lazy<Task<IReadOnlyDictionary<string, long>>>> _rowIds = new();
    private readonly ConcurrentDictionary<(long Instance, int Period), Lazy<Task<IReadOnlyList<CellRecord>>>> _slices = new();
    private readonly ConcurrentDictionary<long, Lazy<Task<TableRead>>> _tables = new();
    private long _retainedCells;

    /// <summary>Найбільше комірок, які кеш утримує (≥ 0).</summary>
    public long MaxRetainedCells { get; } = Math.Max(0, maxRetainedCells);

    /// <summary>Скільки комірок утримано зараз.</summary>
    public long RetainedCells => Interlocked.Read(ref _retainedCells);

    /// <summary>Типовий бюджет для лімітів прогону.</summary>
    /// <param name="limits">Ліміти прогону.</param>
    public static MethodologyPhaseReadCache For(CalculationLimits limits)
        => new((long)limits.MaxParallelism * limits.MaxInputCellsPerBinding);

    /// <summary>Екземпляр таблиці (незмінний за прогін: документ, період, таблиця, версія шаблону).</summary>
    internal Task<TableInstanceRef> InstanceAsync(long tableInstanceId, Func<Task<TableInstanceRef>> load)
        => GetAsync(_instances, tableInstanceId, load, static _ => 0);

    /// <summary>Ключ рядка → <c>TableRow.Id</c> екземпляра за періодом, з яким читають.</summary>
    internal Task<IReadOnlyDictionary<string, long>> RowIdsAsync(
        long tableInstanceId, PeriodKey period, Func<Task<IReadOnlyDictionary<string, long>>> load)
        => GetAsync(_rowIds, (tableInstanceId, period.Value), load, static _ => 0);

    /// <summary>Зріз комірок екземпляра як є у сховищі (без дефолтів колонок).</summary>
    internal Task<IReadOnlyList<CellRecord>> SliceAsync(
        long tableInstanceId, PeriodKey period, Func<Task<IReadOnlyList<CellRecord>>> load)
        => GetAsync(_slices, (tableInstanceId, period.Value), load, static s => s.Count);

    /// <summary>
    /// Екземпляр, рядки й зріз, доповнений дефолтами колонок (<see cref="MethodologyResolver.ReadTableAsync"/>), —
    /// за періодом самого екземпляра.
    /// </summary>
    internal Task<TableRead> TableAsync(long tableInstanceId, Func<Task<TableRead>> load)
        => GetAsync(_tables, tableInstanceId, load, static t => t.Slice.Count);

    private async Task<T> GetAsync<TKey, T>(
        ConcurrentDictionary<TKey, Lazy<Task<T>>> map, TKey key, Func<Task<T>> load, Func<T, int> cellsOf)
        where TKey : notnull
    {
        var mine = new Lazy<Task<T>>(load, LazyThreadSafetyMode.ExecutionAndPublication);
        var entry = map.GetOrAdd(key, mine);
        if (!ReferenceEquals(entry, mine))
        {
            return await entry.Value.ConfigureAwait(false);
        }

        T value;
        try
        {
            value = await entry.Value.ConfigureAwait(false);
        }
        catch
        {
            // Невдача (скасування, тайм-аут) не кешується: інакше вона дісталась би й прив'язці, що прийде пізніше.
            map.TryRemove(KeyValuePair.Create(key, entry));
            throw;
        }

        var cells = cellsOf(value);
        if (cells > 0 && Interlocked.Add(ref _retainedCells, cells) > MaxRetainedCells)
        {
            // Понад бюджет — віддаємо, але не тримаємо (ті, хто вже чекав цей запис, отримують те саме значення).
            Interlocked.Add(ref _retainedCells, -cells);
            map.TryRemove(KeyValuePair.Create(key, entry));
        }

        return value;
    }
}
