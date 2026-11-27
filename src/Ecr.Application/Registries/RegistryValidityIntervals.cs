// src/Ecr.Application/Registries/RegistryValidityIntervals.cs
using System.Collections.Concurrent;
using Ecr.Application.Ports;
using Ecr.Expressions.Evaluation;

namespace Ecr.Application.Registries;

/// <summary>
/// ✎ DESIGN-sg-flaring §5.2a (E6): інтервали чинності завантажених довідників — відсортована множина всіх меж
/// <c>ValidFrom</c>/<c>ValidTo</c> записів джерела. Дві дати одного інтервалу бачать ОДИН і той самий набір записів.
/// </summary>
/// <remarks>
/// ⛔ Видимість запису на дату — лише <c>[ValidFrom, ValidTo)</c>, <c>IsActive</c> і <c>IsDeleted</c>
/// (<see cref="RegistryResolver.IsSelectable"/>); видимість батька композиції — те саме правило для його запису. Дві останні
/// ознаки від дати не залежать, тож знімок змінюється лише на межах вікон — і на межах УСІХ записів джерела, зокрема
/// цілей <c>Lookup</c>, які завантажувач додає сам (транзитивно; D3 рецензії): інтервали рахуються з записів джерела, а
/// не з довідників, названих у скрипті.
/// <para>
/// ⚠ Джерело іншого виду (не <see cref="RegistrySnapshotSource"/>, напр. підставне в тестах) записів не показує — тоді
/// кожна дата — власний «інтервал»: знімок однаково правильний, лише будується частіше.
/// </para>
/// </remarks>
public sealed class RegistryValidityIntervals
{
    private readonly DateOnly[]? _boundaries;

    private RegistryValidityIntervals(DateOnly[]? boundaries) => _boundaries = boundaries;

    /// <summary>Межі (відсортовані, без повторів); <c>null</c> — джерело записів не показує.</summary>
    public IReadOnlyList<DateOnly>? Boundaries => _boundaries;

    /// <summary>Інтервали з джерела знімків.</summary>
    /// <param name="source">Джерело, прочитане завантажувачем.</param>
    public static RegistryValidityIntervals Of(IRegistrySnapshotSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source is not RegistrySnapshotSource loaded)
        {
            return new RegistryValidityIntervals(null);
        }

        var set = new SortedSet<DateOnly>();
        foreach (var entry in loaded.Entries)
        {
            if (entry.ValidFrom is { } from)
            {
                set.Add(from);
            }

            if (entry.ValidTo is { } to)
            {
                set.Add(to);
            }
        }

        return new RegistryValidityIntervals([.. set]);
    }

    /// <summary>
    /// Ключ інтервалу дати: кількість меж, не пізніших за дату (межа — перший день нового інтервалу, бо
    /// <c>ValidFrom</c> включна, а <c>ValidTo</c> — перший НЕчинний день). Без меж — <c>DayNumber</c> самої дати.
    /// </summary>
    /// <param name="date">Бізнес-дата.</param>
    public long KeyOf(DateOnly date)
    {
        if (_boundaries is null)
        {
            return -1L - date.DayNumber;
        }

        var index = Array.BinarySearch(_boundaries, date);
        return index >= 0 ? index + 1 : ~index;
    }
}

/// <summary>
/// ✎ DESIGN-sg-flaring §5.2a (E3/E6): джерело знімків довідників одного прогону з пам'яттю «один знімок на інтервал
/// чинності». Спільне для всіх документів прогону (<see cref="RegistrySnapshotCache.GetOrLoadSourceAsync"/>).
/// </summary>
/// <remarks>⚠ Потокобезпечне: прив'язки пакета йдуть паралельно, а знімок незмінний.</remarks>
public sealed class DatedRegistrySnapshots
{
    private readonly IRegistrySnapshotSource _source;
    private readonly ConcurrentDictionary<long, Lazy<IRegistrySnapshot>> _built = new();
    private int _builds;

    /// <summary>Створює пам'ять над джерелом.</summary>
    /// <param name="source">Прочитане джерело.</param>
    public DatedRegistrySnapshots(IRegistrySnapshotSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        Intervals = RegistryValidityIntervals.Of(source);
    }

    /// <summary>Інтервали чинності джерела.</summary>
    public RegistryValidityIntervals Intervals { get; }

    /// <summary>Скільки знімків побудовано (AC-8: не більше, ніж інтервалів, що їх торкнулися рядки).</summary>
    public int BuildCount => Volatile.Read(ref _builds);

    /// <summary>Знімок на дату: з пам'яті інтервалу або побудований (будь-яка дата інтервалу дає той самий знімок).</summary>
    /// <param name="date">Бізнес-дата.</param>
    public IRegistrySnapshot Get(DateOnly date)
        => _built.GetOrAdd(
                Intervals.KeyOf(date),
                _ => new Lazy<IRegistrySnapshot>(() =>
                {
                    Interlocked.Increment(ref _builds);
                    return _source.Build(date);
                }))
            .Value;
}
