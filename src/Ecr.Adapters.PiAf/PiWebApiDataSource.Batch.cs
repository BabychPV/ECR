using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;

namespace Ecr.Adapters.PiAf;

/// <summary>Кеш WebId і пакетне читання <c>streamsets/recorded</c> (аудит продуктивності P7).</summary>
/// <remarks>
/// Було: на кожну пару інтервал × атрибут — <c>GET attributes?path=</c> і
/// <c>GET streams/{webId}/recorded</c>, послідовно. Наздоганяння 30 діб
/// погодинно на 20 атрибутах — ~14 400 запитів, половина з яких шукала той
/// самий WebId. Стало: WebId шукається раз на екземпляр (тобто на прогін —
/// адаптер живе в області DI одного прогону), точки всіх атрибутів одного
/// інтервалу — одним <c>streamsets/recorded</c>.
/// </remarks>
public sealed partial class PiWebApiDataSource
{
    /// <summary>Скільки WebId у одному <c>streamsets/recorded</c>.</summary>
    /// <remarks>
    /// ⚠ Судження, не довідник постачальника: п'ятдесят атрибутів по стелі
    /// <see cref="CollectionRunner.MaxPointsPerRequest"/> — до 250 000 точок у
    /// відповіді, тобто десятки мегабайт JSON. Більший пакет не дає виграшу в
    /// кількості запитів на типовій сутності (до 20–30 мапінгів), а відповідь
    /// і навантаження на AF у гіршому разі ростуть лінійно.
    /// </remarks>
    public const int MaxStreamsPerRequest = 50;

    /// <summary>Стеля довжини параметрів <c>webId=…</c> одного запиту, символів.</summary>
    /// <remarks>
    /// ⚠ WebID 2.0 атрибута — зазвичай 40–100 символів, але формат «шлях»
    /// буває й довшим. Дві тисячі — найменша поширена межа URL у проміжних
    /// проксі (і типова <c>maxQueryString</c> IIS — 2048); самі межі HTTP.sys
    /// PI Web API більші. Пакет ріжеться за тим, що настане раніше: 50 WebId
    /// або 2000 символів.
    /// </remarks>
    public const int MaxWebIdQueryLength = 2_000;

    /// <summary>WebId атрибута, його одиниця за замовчуванням або відмова «такого шляху немає».</summary>
    /// <param name="WebId">WebId; порожній — відповідь без WebId.</param>
    /// <param name="DefaultUnits"><c>DefaultUnitsName</c> атрибута.</param>
    /// <param name="NotFound">Відповідь <c>404</c> — та сама відмова, що кидав би прямий запит.</param>
    private sealed record AttributeRef(string? WebId, string? DefaultUnits, BusinessRuleException? NotFound);

    /// <summary>Знайдені WebId: ключ — джерело й шлях, значення — пошук (зокрема той, що ще йде).</summary>
    /// <remarks>
    /// ⚠ Зберігається ЗАДАЧА, а не результат: паралельні читання того самого
    /// шляху чекають один пошук, а не шлють кожне своє.
    /// <see cref="Lazy{T}"/> — бо <c>GetOrAdd</c> під гонкою може викликати
    /// фабрику двічі, і без обгортки два паралельні читання послали б два
    /// пошуки.
    /// </remarks>
    private readonly ConcurrentDictionary<(int DataSourceId, string Path), Lazy<Task<AttributeRef>>> webIds = new();

    /// <summary>WebId атрибута — з кешу або одним <c>attributes?path=</c>.</summary>
    /// <remarks>
    /// ⛔ У кеші лишаються лише ВІДПОВІДІ джерела про шлях: знайдено, без WebId
    /// або <c>404</c>. Будь-яка інша відмова (5xx, таймаут, автентифікація,
    /// скасування) з кешу прибирається: інакше одна мить недоступності
    /// перетворилася б на «атрибута немає» до кінця прогону.
    /// </remarks>
    private async Task<AttributeRef> AttributeAsync(DataSource source, string path, CancellationToken ct)
    {
        var key = (source.Id, path);
        var lookup = webIds.GetOrAdd(key, _ => new Lazy<Task<AttributeRef>>(() => LookupAsync(source, path, ct)));

        try
        {
            return await lookup.Value.ConfigureAwait(false);
        }
        catch
        {
            webIds.TryRemove(new KeyValuePair<(int, string), Lazy<Task<AttributeRef>>>(key, lookup));
            throw;
        }
    }

    private async Task<AttributeRef> LookupAsync(DataSource source, string path, CancellationToken ct)
    {
        try
        {
            using var attribute = await GetAsync(
                source.Endpoint, $"attributes?path={Uri.EscapeDataString(path)}", source.SecretName, ct)
                .ConfigureAwait(false);

            return new AttributeRef(
                Text(attribute.RootElement, "WebId"), Text(attribute.RootElement, "DefaultUnitsName"), null);
        }
        catch (BusinessRuleException ex) when (IsNotFound(ex))
        {
            return new AttributeRef(null, null, ex);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Запити з однаковими межами (перша сторінка інтервалу — у всіх
    /// атрибутів однакова) ідуть одним <c>streamsets/recorded</c> на пакет;
    /// хвости понад стелю мають кожен свій курсор і тому здебільшого йдуть
    /// пакетом з одного. Пакети одного виклику — послідовно: паралелізм
    /// задає збирач (<c>CollectionRunner.DefaultMaxParallelReads</c>),
    /// і другий рівень тут його б помножив.
    /// <para>
    /// ⚠ <c>maxCount</c> у <c>streamsets/recorded</c> — стеля на КОЖЕН потік,
    /// не на відповідь загалом; тому обрізання хвоста визначається так само,
    /// як для <c>streams/{webId}/recorded</c>: потік віддав рівно стелю.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<BatchReadItem>> ReadBatchAsync(
        DataSource source, IReadOnlyList<CollectionRequest> requests, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(requests);

        var items = new BatchReadItem?[requests.Count];
        var resolved = new List<(int Index, AttributeRef Attribute)>();

        for (var i = 0; i < requests.Count; i++)
        {
            var request = requests[i];

            if (request.Kind != SourceQueryKind.Raw)
            {
                items[i] = new BatchReadItem(null, IExternalDataSource.QueryKindNotSupported(request.Kind, Transport));
                continue;
            }

            try
            {
                var attribute = await AttributeAsync(source, request.SourcePath, ct).ConfigureAwait(false);

                if (attribute.NotFound is { } missing)
                {
                    items[i] = new BatchReadItem(null, missing);
                }
                else if (string.IsNullOrWhiteSpace(attribute.WebId))
                {
                    items[i] = new BatchReadItem(Unresolved(request), null);
                }
                else
                {
                    resolved.Add((i, attribute));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                items[i] = new BatchReadItem(null, ex);
            }
        }

        foreach (var chunk in Chunks(requests, resolved))
        {
            await ReadChunkAsync(source, requests, chunk, items, ct).ConfigureAwait(false);
        }

        return [.. items.Select(item => item ?? throw new InvalidOperationException("Запит пакета лишився без відповіді."))];
    }

    /// <summary>Пакети: однакові межі й стеля, не більше 50 WebId і 2000 символів.</summary>
    private static IEnumerable<List<(int Index, AttributeRef Attribute)>> Chunks(
        IReadOnlyList<CollectionRequest> requests, List<(int Index, AttributeRef Attribute)> resolved)
    {
        foreach (var group in resolved.GroupBy(r =>
                     (requests[r.Index].FromUtc, requests[r.Index].ToUtc, requests[r.Index].MaxPoints)))
        {
            var chunk = new List<(int Index, AttributeRef Attribute)>();
            var webIdsInChunk = new HashSet<string>(StringComparer.Ordinal);
            var length = 0;

            foreach (var entry in group)
            {
                var webId = entry.Attribute.WebId!;
                var cost = WebIdParameter(webId).Length;

                // Той самий WebId удруге (два мапінги одного атрибута) місця в
                // URL не займає — він іде одним параметром.
                if (!webIdsInChunk.Contains(webId)
                    && webIdsInChunk.Count > 0
                    && (webIdsInChunk.Count >= MaxStreamsPerRequest || length + cost > MaxWebIdQueryLength))
                {
                    yield return chunk;
                    chunk = [];
                    webIdsInChunk.Clear();
                    length = 0;
                }

                if (webIdsInChunk.Add(webId))
                {
                    length += cost;
                }

                chunk.Add(entry);
            }

            yield return chunk;
        }
    }

    private static string WebIdParameter(string webId) => $"webId={Uri.EscapeDataString(webId)}&";

    /// <summary>Один <c>streamsets/recorded</c> на пакет; відмова запиту — відмова кожного його атрибута.</summary>
    private async Task ReadChunkAsync(
        DataSource source,
        IReadOnlyList<CollectionRequest> requests,
        List<(int Index, AttributeRef Attribute)> chunk,
        BatchReadItem?[] items,
        CancellationToken ct)
    {
        var first = requests[chunk[0].Index];
        var query = new StringBuilder("streamsets/recorded?");

        foreach (var webId in chunk.Select(c => c.Attribute.WebId!).Distinct(StringComparer.Ordinal))
        {
            query.Append(WebIdParameter(webId));
        }

        query.Append("startTime=").Append(Iso(first.FromUtc))
            .Append("&endTime=").Append(Iso(first.ToUtc))
            .Append("&maxCount=").Append(first.MaxPoints.ToString(CultureInfo.InvariantCulture));

        JsonDocument recorded;

        try
        {
            recorded = await GetAsync(source.Endpoint, query.ToString(), source.SecretName, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            foreach (var (index, _) in chunk)
            {
                items[index] = new BatchReadItem(null, ex);
            }

            return;
        }

        using (recorded)
        {
            var streams = Streams(recorded.RootElement);

            foreach (var (index, attribute) in chunk)
            {
                var request = requests[index];

                // ⚠ Потік без точок і потік, якого у відповіді НЕМАЄ або який
                // прийшов із помилкою, — різні речі. Перше — порожній період
                // (покривається), друге — відмова цього атрибута: увесь
                // інтервал у FailedIntervals, як і коли WebId не знайдено.
                // Сусіди з того самого запиту від цього не страждають.
                items[index] = streams.TryGetValue(attribute.WebId!, out var stream) && !Failed(stream)
                    ? new BatchReadItem(Batch(Points(stream, request.SourcePath, attribute.DefaultUnits), request), null)
                    : new BatchReadItem(Unresolved(request), null);
            }
        }
    }

    /// <summary>Потоки відповіді за WebId.</summary>
    /// <remarks>
    /// ⚠ Зіставлення за <c>WebId</c>, а не за позицією: порядок <c>Items</c>
    /// збігається з порядком запиту за звичаєм, а не за контрактом, і
    /// помилка зсуву записала б точки одного атрибута під іменем іншого.
    /// </remarks>
    private static Dictionary<string, JsonElement> Streams(JsonElement root)
    {
        var streams = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        if (root.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in items.EnumerateArray())
            {
                if (Text(stream, "WebId") is { } webId)
                {
                    streams.TryAdd(webId, stream);
                }
            }
        }

        return streams;
    }

    /// <summary>Чи прийшов потік із помилкою замість точок.</summary>
    /// <remarks>
    /// PI Web API описує часткову відмову елемента колекції як <c>Errors</c>
    /// (масив рядків) або <c>Exception.Errors</c>; перевіряються обидві форми.
    /// </remarks>
    private static bool Failed(JsonElement stream)
        => HasErrors(stream)
           || (stream.TryGetProperty("Exception", out var exception)
               && exception.ValueKind == JsonValueKind.Object
               && (HasErrors(exception) || !exception.TryGetProperty("Errors", out _)));

    private static bool HasErrors(JsonElement element)
        => element.TryGetProperty("Errors", out var errors)
           && errors.ValueKind == JsonValueKind.Array
           && errors.GetArrayLength() > 0;
}
