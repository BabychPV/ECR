using Ecr.Adapters.PiAf;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;

namespace Ecr.Adapters.Tests.PiAf;

/// <summary>
/// Синтетичне джерело PI для тестів навантаження й продовження (D-462): <c>tags</c> атрибутів, точка кожні
/// <c>step</c> від <c>origin</c>, значення — номер точки. Точки не зберігаються, а обчислюються на запит —
/// рік хвилинних точок п'ятдесяти тегів не займає пам'яті.
/// </summary>
/// <remarks>
/// ⚠ Поводиться як обидва адаптери PI: <c>[From, To)</c>, не більше <c>MaxPoints</c>, повний батч — хвіст
/// <c>[мітка останньої, To)</c>. Сам клас читає по атрибуту (як PI SQL Client); <see cref="SyntheticBatchPi"/>
/// оголошує ще й <see cref="IBatchCollectionSource"/> (як PI Web API).
/// </remarks>
internal class SyntheticPi(int tags, DateTime origin, TimeSpan step, DateTime end) : IExternalDataSource
{
    private int inFlight;
    private int calls;

    /// <summary>Імена атрибутів: <c>T00|v</c> … .</summary>
    public IReadOnlyList<string> Paths { get; } =
        [.. Enumerable.Range(0, tags).Select(i => $"T{i:00}|v")];

    /// <summary>Затримка кожного звернення (імітація мережі).</summary>
    public TimeSpan Latency { get; init; } = TimeSpan.Zero;

    /// <summary>Звернення, з якого джерело «зависає» (до скасування); <c>null</c> — ніколи.</summary>
    public Func<CollectionRequest, bool>? HangWhen { get; set; }

    /// <summary>Кількість звернень (батч — одне звернення).</summary>
    public int Calls => Volatile.Read(ref calls);

    /// <summary>Найбільша кількість одночасних звернень.</summary>
    public int MaxInFlight { get; private set; }

    /// <summary>Кожен запит (батч — кожен його елемент).</summary>
    public System.Collections.Concurrent.ConcurrentQueue<CollectionRequest> Requests { get; } = new();

    /// <summary>Скільки всього точок за <c>[origin, end)</c>.</summary>
    public long PointsPerTag => (end - origin).Ticks / step.Ticks;

    /// <inheritdoc />
    public ExternalTransport Transport => ExternalTransport.PiSqlClient;

    /// <inheritdoc />
    public Task<IReadOnlyList<SourceEntityDescriptor>> DiscoverAsync(int dataSourceId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<SourceEntityDescriptor>>([]);

    /// <inheritdoc />
    public async Task<CollectionResult> ReadAsync(CollectionRequest request, CancellationToken ct)
    {
        await EnterAsync(ct).ConfigureAwait(false);

        try
        {
            return await PageAsync(request, ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref inFlight);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BatchReadItem>> ReadBatchAsync(
        DataSource source, IReadOnlyList<CollectionRequest> requests, CancellationToken ct)
    {
        await EnterAsync(ct).ConfigureAwait(false);

        try
        {
            var items = new List<BatchReadItem>(requests.Count);

            foreach (var request in requests)
            {
                items.Add(new BatchReadItem(await PageAsync(request, ct).ConfigureAwait(false), null));
            }

            return items;
        }
        finally
        {
            Interlocked.Decrement(ref inFlight);
        }
    }

    /// <summary>Номер точки за міткою.</summary>
    public long IndexOf(DateTime timestamp) => (timestamp - origin).Ticks / step.Ticks;

    private async Task EnterAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref calls);
        var now = Interlocked.Increment(ref inFlight);

        lock (Paths)
        {
            MaxInFlight = Math.Max(MaxInFlight, now);
        }

        if (Latency > TimeSpan.Zero)
        {
            await Task.Delay(Latency, ct).ConfigureAwait(false);
        }
    }

    private async Task<CollectionResult> PageAsync(CollectionRequest request, CancellationToken ct)
    {
        Requests.Enqueue(request);

        if (HangWhen?.Invoke(request) == true)
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }

        var first = Math.Max(0, Ceiling(request.FromUtc));
        var last = Math.Min(PointsPerTag, Ceiling(request.ToUtc));
        var count = (int)Math.Clamp(last - first, 0, request.MaxPoints);

        var points = new List<SourceDataPoint>(count);
        for (var i = 0; i < count; i++)
        {
            var index = first + i;
            points.Add(new SourceDataPoint(
                request.SourcePath, origin + TimeSpan.FromTicks(step.Ticks * index), index, null, null, "Good"));
        }

        var truncated = count > 0 && count >= request.MaxPoints;

        return new CollectionResult(
            points,
            truncated ? [new TimeInterval(points[^1].Timestamp, request.ToUtc)] : [],
            null);
    }

    /// <summary>Номер першої точки, що не раніша за <paramref name="at"/>.</summary>
    private long Ceiling(DateTime at)
    {
        var ticks = (at - origin).Ticks;
        return ticks <= 0 ? 0 : (ticks + step.Ticks - 1) / step.Ticks;
    }
}

/// <summary>Те саме джерело з пакетним читанням (як PI Web API, <see cref="IBatchCollectionSource"/>).</summary>
internal sealed class SyntheticBatchPi(int tags, DateTime origin, TimeSpan step, DateTime end)
    : SyntheticPi(tags, origin, step, end), IBatchCollectionSource;
