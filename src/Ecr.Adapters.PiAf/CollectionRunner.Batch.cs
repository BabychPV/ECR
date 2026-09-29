using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;

namespace Ecr.Adapters.PiAf;

/// <summary>Пакетне читання й читання наперед (аудит продуктивності P7).</summary>
public sealed partial class CollectionRunner
{
    /// <summary>Скільки пакетних читань одного прогону може йти одночасно.</summary>
    /// <remarks>
    /// ⚠ Константа, а не ключ конфігурації — судження: це повага до ЧУЖОГО
    /// сервера (PI AF деградує для всіх клієнтів, див.
    /// <see cref="MaxPointsPerRequest"/>), а не налаштування продукту, яке
    /// варто крутити на місці. Чотири запити одночасно при RTT 100–300 мс
    /// дають наздоганянню 30 діб погодинно (720 інтервалів) хвилину-дві замість
    /// годин, лишаючись одним «звичайним» клієнтом для PI Web API. Quartz може
    /// виконувати кілька прогонів різних сутностей паралельно — межа діє на
    /// кожен прогін окремо.
    /// </remarks>
    public const int DefaultMaxParallelReads = 4;

    /// <summary>Межа одночасних читань цього прогону: параметр конструктора (тести) або дефолт.</summary>
    private int ParallelReads => maxParallelReads is > 0 ? maxParallelReads.Value : DefaultMaxParallelReads;

    /// <summary>Скільки атрибута прочитано в поточному інтервалі.</summary>
    /// <param name="path">Шлях атрибута.</param>
    /// <param name="cursor">Початок інтервалу.</param>
    private sealed class PathCursor(string path, DateTime cursor)
    {
        public string Path { get; } = path;

        /// <summary>Перший НЕпрочитаний момент (межа ≥, див. <see cref="Carried"/>).</summary>
        public DateTime Cursor { get; set; } = cursor;

        /// <summary>Точки з мітками ≥ курсора, які вже прочитано попередньою сторінкою.</summary>
        public Dictionary<DateTime, int> Carried { get; set; } = [];

        public int Pages { get; set; }
    }

    /// <summary>Один раунд пакетного читання: сторінка кожного атрибута з його курсора.</summary>
    /// <remarks>
    /// ⚠ Запити будуються ДО першого <c>await</c>: курсори читаються в момент
    /// виклику, а не тоді, коли дійде черга до мережі.
    /// ⚠ Відмова адаптера цілком — це відмова кожного атрибута раунду з тією ж
    /// класифікацією, що в <see cref="ReadAsync"/>: автентифікація окремо
    /// (<c>H-20</c>), решта — «джерело недоступне». Скасування летить далі.
    /// </remarks>
    private static async Task<IReadOnlyList<ReadOutcome>> ReadRoundAsync(
        IBatchCollectionSource batch,
        DataSource dataSource,
        int sourceEntityId,
        IReadOnlyList<PathCursor> cursors,
        DateTime toUtc,
        CancellationToken ct)
    {
        var requests = cursors
            .Select(c => new CollectionRequest(
                dataSource.Id, sourceEntityId, c.Path, c.Cursor, toUtc, MaxPointsPerRequest))
            .ToList();

        if (requests.Count == 0)
        {
            return [];
        }

        try
        {
            var items = await batch.ReadBatchAsync(dataSource, requests, ct).ConfigureAwait(false);

            return items.Count == requests.Count
                ? [.. items.Select(Outcome)]
                : throw new InvalidOperationException(
                    $"Адаптер повернув {items.Count} відповідей на {requests.Count} запитів.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failed = Outcome(new BatchReadItem(null, ex));
            return [.. requests.Select(_ => failed)];
        }
    }

    private static ReadOutcome Outcome(BatchReadItem item) => item switch
    {
        { Collected: { } collected } => new ReadOutcome(collected, null, null),
        { Error: SourceAuthenticationException ex } => new ReadOutcome(null, ex.ErrorCode, ex.Message, Unauthorized: true),

        // ⛔ Текст — без стека (ФВ-6.11), як у ReadAsync.
        { Error: { } ex } => new ReadOutcome(null, SourceUnavailable, ex.Message),
        _ => new ReadOutcome(null, SourceUnavailable, null),
    };

    /// <summary>Перші раунди наступних інтервалів, запущені наперед, — не більше заданої кількості.</summary>
    /// <remarks>
    /// ⚠ Наперед іде ЛИШЕ читання з джерела. Збереження, покриття, пауза
    /// мапінгів і рішення про відмову — як і раніше, по черзі інтервалів у
    /// потоці прогону: сховище — один <c>DbContext</c>, і паралельних звернень
    /// до нього тут немає. Прочитане наперед для інтервалу, до якого прогін
    /// не дійшов (відмова, годинник), просто відкидається — покриття за нього
    /// не пишеться.
    /// <para>
    /// Одночасно в мережі — не більше <c>limit</c> запитів: <see cref="Take"/>
    /// тримає запущеними поточний інтервал і <c>limit − 1</c> наступних, а
    /// хвости поточного читаються, коли його перший раунд уже завершився.
    /// </para>
    /// </remarks>
    private sealed class ReadAhead(
        int count,
        int limit,
        Func<int, CancellationToken, (List<PathCursor> Cursors, Task<IReadOnlyList<ReadOutcome>> Round)> start,
        CancellationToken runToken)
        : IAsyncDisposable
    {
        private readonly CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(runToken);
        private readonly Dictionary<int, (List<PathCursor> Cursors, Task<IReadOnlyList<ReadOutcome>> Round)> started = [];
        private int next;

        /// <summary>Перший раунд інтервалу <paramref name="index"/>; наступні — запускаються наперед.</summary>
        public (List<PathCursor> Cursors, Task<IReadOnlyList<ReadOutcome>> Round) Take(int index)
        {
            next = Math.Max(next, index);

            for (; next < count && next < index + limit; next++)
            {
                started[next] = start(next, stop.Token);
            }

            var taken = started[index];
            started.Remove(index);
            return taken;
        }

        /// <summary>Скасовує й дочікується прочитаного наперед, до якого прогін не дійшов.</summary>
        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync().ConfigureAwait(false);

            foreach (var (_, round) in started.Values)
            {
                try
                {
                    await round.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Саме цього й просили: читання наперед більше не потрібне.
                }
            }

            started.Clear();
            stop.Dispose();
        }
    }
}
