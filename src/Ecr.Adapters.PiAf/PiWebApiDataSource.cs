using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;

namespace Ecr.Adapters.PiAf;

/// <summary>
/// Читання через PI Web API (HTTP).
/// </summary>
/// <remarks>
/// Використовується для того, чого не вміє RTQP. **Методів запису тут немає
/// навмисно** (`D-44`): Web API їх підтримує, але система в AF не пише нічого.
/// </remarks>
public sealed class PiWebApiDataSource(
    HttpClient http, ICollectionStore store, ISecretProvider secrets) : IExternalDataSource
{
    /// <summary>Скільки разів повторювати запит, який відмовив через 5xx або таймаут.</summary>
    /// <remarks>
    /// ⚠ Три, а не «поки не вийде». Джерело, яке лежить, від наполегливості
    /// не піднімається — а нескінченні ретраї перетворюють одну відмову на
    /// навантаження, через яке воно не піднімається довше.
    /// </remarks>
    public const int MaxAttempts = 3;

    /// <summary>Базова затримка між спробами; далі подвоюється.</summary>
    public static TimeSpan RetryDelay => TimeSpan.FromSeconds(2);

    private const string SourceUnavailable = "ECR-INT-0503";

    /// <summary>Джерело відмовило в автентифікації — не те саме, що недоступність (<c>H-20</c>).</summary>
    private const string AuthenticationRefused = "ECR-INT-0502";

    /// <inheritdoc />
    public ExternalTransport Transport => ExternalTransport.PiWebApi;

    /// <inheritdoc />
    /// <remarks>
    /// Обхід дає **один рівень** ієрархії. Повний обхід бази AF у
    /// конфігураторі означав би хвилини очікування на дереві, у якому
    /// користувач відкриє два вузли.
    /// </remarks>
    public async Task<IReadOnlyList<SourceEntityDescriptor>> DiscoverAsync(
        int dataSourceId, CancellationToken ct)
    {
        var source = await SourceAsync(dataSourceId, ct).ConfigureAwait(false);

        // ⛔ Q-222 (аудит): `using`, а не голий `.Dispose()` наприкінці —
        // виняток із `EnumerateArray()`/`Text(...)` лишав би `JsonDocument`
        // (некерована пам'ять) недиспозженим, на відміну від `ReadAsync` у
        // цьому самому класі, де той самий патерн уже під `using`.
        using var elements = await GetAsync(
            source.Endpoint,
            $"assetdatabases/{Uri.EscapeDataString(source.Catalog ?? string.Empty)}/elements"
            + "?searchFullHierarchy=false",
            source.SecretName,
            ct).ConfigureAwait(false);

        var result = new List<SourceEntityDescriptor>();

        if (elements.RootElement.TryGetProperty("Items", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                result.Add(new SourceEntityDescriptor(
                    Text(item, "Name") ?? string.Empty,
                    Text(item, "Description"),
                    Text(item, "Path"),
                    SourceUnitSymbol: null,
                    DataType: "Element"));
            }
        }

        return result;
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Часткова відмова батча — це **не** загальний провал: успішні точки
    /// повертаються, невдалий інтервал іде у <see cref="CollectionResult.FailedIntervals"/>
    /// і звідти в наздоганяння. Інакше одна відмова наприкінці діапазону
    /// щоразу викидала б усе, що вже прочиталося.
    /// </remarks>
    public async Task<CollectionResult> ReadAsync(CollectionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var source = await SourceAsync(request.DataSourceId, ct).ConfigureAwait(false);

        JsonDocument? attribute = null;
        JsonDocument? recorded = null;

        try
        {
            attribute = await GetAsync(
                source.Endpoint,
                $"attributes?path={Uri.EscapeDataString(request.SourcePath)}",
                source.SecretName,
                ct).ConfigureAwait(false);

            var webId = Text(attribute.RootElement, "WebId");
            var defaultUnits = Text(attribute.RootElement, "DefaultUnitsName");

            if (string.IsNullOrWhiteSpace(webId))
            {
                // Атрибута за таким шляхом немає. Це не «нуль точок»: нуль
                // означав би, що джерело відповіло порожнім періодом, і
                // покриття за нього записалося б як повне.
                return new CollectionResult(
                    [], [new TimeInterval(request.FromUtc, request.ToUtc)], SourceUnavailable);
            }

            recorded = await GetAsync(
                source.Endpoint,
                $"streams/{Uri.EscapeDataString(webId)}/recorded"
                + $"?startTime={Iso(request.FromUtc)}&endTime={Iso(request.ToUtc)}"
                + $"&maxCount={request.MaxPoints.ToString(CultureInfo.InvariantCulture)}",
                source.SecretName,
                ct).ConfigureAwait(false);

            var points = Points(recorded.RootElement, request.SourcePath, defaultUnits);

            // ⚠ Повний батч означає, що джерело віддало рівно стелю — і хвіст
            // діапазону лишився непрочитаним. Мовчазне «зібрано» тут дало б
            // дірку, позначену як покриття.
            // ⚠ Порожній батч НЕ вважається обрізаним: із MaxPoints = 0 умова
        // «набрали стелю» була б істинною завжди, і points[^1] упало б на
        // порожньому списку — на діапазоні, у якому просто немає даних.
        var truncated = points.Count > 0 && points.Count >= request.MaxPoints;

            return new CollectionResult(
                points,
                truncated ? [new TimeInterval(points[^1].Timestamp, request.ToUtc)] : [],
                null);
        }
        finally
        {
            attribute?.Dispose();
            recorded?.Dispose();
        }
    }

    /// <summary>Джерело за ідентифікатором; запам'ятовується на час прогону.</summary>
    private async Task<Domain.Entities.External.DataSource> SourceAsync(int dataSourceId, CancellationToken ct)
    {
        if (cached is { } known && known.Id == dataSourceId)
        {
            return known;
        }

        cached = await store.FindDataSourceAsync(dataSourceId, ct).ConfigureAwait(false)
                 ?? throw new BusinessRuleException(
                     SourceUnavailable,
                     $"Джерело {dataSourceId} не існує або вимкнене.",
                     new Dictionary<string, object?> { ["dataSourceId"] = dataSourceId });

        return cached;
    }

    private Domain.Entities.External.DataSource? cached;

    /// <summary>GET із ретраями на 5xx і таймаутах.</summary>
    /// <remarks>
    /// ⛔ 4xx не повторюється: невірний шлях або відмова в доступі від
    /// повторення не виправляються, а лише подовжують збір на час усіх спроб.
    /// </remarks>
    private async Task<JsonDocument> GetAsync(
        string endpoint, string path, string secretName, CancellationToken ct)
    {
        var uri = new Uri($"{endpoint.TrimEnd('/')}/{path}");
        var delay = RetryDelay;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Get, uri);
                Authorize(message, secretName);

                using var response = await http.SendAsync(message, ct).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    // ⛔ 401/403 — ОКРЕМИЙ вид відмови, і він не повторюється
                    // жодного разу (`H-20`). Той самий заголовок дасть ту саму
                    // відповідь, а прогін від цього стане повільнішим, не
                    // успішнішим. Тип винятку тут — єдине, що не дає збирачеві
                    // проковтнути відмову й піти в наздоганяння.
                    if (Unauthorized(response.StatusCode))
                    {
                        throw new SourceAuthenticationException(
                            AuthenticationRefused,
                            $"PI Web API відповів {(int)response.StatusCode} на {path}: "
                            + "джерело не приймає облікові дані.",
                            new Dictionary<string, object?> { ["status"] = (int)response.StatusCode });
                    }

                    if (attempt >= MaxAttempts || !Retryable(response.StatusCode))
                    {
                        throw new BusinessRuleException(
                            SourceUnavailable,
                            $"PI Web API відповів {(int)response.StatusCode} на {path}.",
                            new Dictionary<string, object?> { ["status"] = (int)response.StatusCode });
                    }
                }
                else
                {
                    var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    return await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);
                }
            }
            catch (HttpRequestException) when (attempt < MaxAttempts)
            {
                // Мережева відмова — саме той випадок, заради якого ретрай
                // існує: джерело живе, між нами — комутатор.
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < MaxAttempts)
            {
                // Таймаут HttpClient приходить саме так; скасування ззовні —
                // ні, і його повторювати не можна.
            }

            await Task.Delay(delay, ct).ConfigureAwait(false);
            delay += delay;
        }
    }

    /// <summary>
    /// Схема автентифікації, названа в секреті джерела.
    /// </summary>
    /// <remarks>
    /// ⚠ Схема — це **налаштування**, а не гілка коду (`P-12`). Як саме
    /// автентифікується PI Web API в конкретному контурі, з коду не видно:
    /// Kerberos, Basic і Bearer однаково правдоподібні. Помилка тут дає збору
    /// постійний <c>401</c>.
    /// <para>
    /// ⛔ Раніше такий <c>401</c> потрапляв у наздоганяння і прогін
    /// <b>завершувався успішно</b> — рівно як задумано для тимчасово
    /// недоступного джерела. Це був наш дефект (<c>H-20</c>): помилка в
    /// налаштуванні не проявлялася як помилка. Тепер відмова в автентифікації
    /// кидає <see cref="SourceAuthenticationException"/>, прогін стає
    /// <c>Failed</c>, а алерт іде негайно.
    /// </para>
    /// <para>
    /// Тому значення секрету читається як <c>"схема значення"</c>:
    /// <c>Basic dXNlcjpwYXNz</c>, <c>Bearer eyJ…</c>. Секрет без пробілу —
    /// <c>Bearer</c> за замовчуванням; порожній секрет означає інтегровану
    /// автентифікацію службового облікового запису (D-34), яку виконує сам
    /// <c>HttpClient</c>.
    /// </para>
    /// <para>
    /// ⛔ Значення секрету не логується — ні саме, ні його довжина, ні факт
    /// збігу (ФВ-6.11).
    /// </para>
    /// </remarks>
    private void Authorize(HttpRequestMessage message, string secretName)
    {
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var secret = secrets.Find(secretName);

        if (string.IsNullOrEmpty(secret))
        {
            // Порожній заголовок кращий за вигаданий: підставлений `Basic` із
            // порожнім паролем отримав би 401 і виглядав би як недоступність
            // джерела.
            return;
        }

        var separator = secret.IndexOf(' ', StringComparison.Ordinal);

        message.Headers.Authorization = separator > 0
            ? new AuthenticationHeaderValue(secret[..separator], secret[(separator + 1)..])
            : new AuthenticationHeaderValue("Bearer", secret);
    }

    private static bool Retryable(HttpStatusCode status)
        => (int)status >= 500 || status == HttpStatusCode.RequestTimeout;

    /// <summary>Чи це відмова саме в автентифікації, а не в доступності.</summary>
    /// <remarks>
    /// ⚠ <c>403</c> сюди входить нарівні з <c>401</c>. Для PI Web API різниця
    /// між «не назвався» і «назвався не тим» — це різниця в налаштуванні
    /// службового запису, і обидва випадки лікує людина, а не повтор.
    /// </remarks>
    private static bool Unauthorized(HttpStatusCode status)
        => status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    /// <summary>Точки батча в одиниці ДЖЕРЕЛА (ФВ-16.10).</summary>
    private static List<SourceDataPoint> Points(JsonElement root, string sourcePath, string? defaultUnits)
    {
        var points = new List<SourceDataPoint>();

        if (!root.TryGetProperty("Items", out var items))
        {
            return points;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("Timestamp", out var stamp)
                || !stamp.TryGetDateTime(out var timestamp))
            {
                continue;
            }

            var (numeric, text) = Value(item);

            points.Add(new SourceDataPoint(
                sourcePath,
                timestamp.ToUniversalTime(),
                numeric,
                text,
                Text(item, "UnitsAbbreviation") ?? defaultUnits,
                Quality(item)));
        }

        return points;
    }

    /// <summary>Значення точки: число або текст, ніколи обидва.</summary>
    /// <remarks>
    /// ⚠ Цифровий стан AF (<c>{"Name":"Off","Value":0}</c>) зберігається
    /// текстом, а не своїм числовим кодом: нуль стану «Off» у сумі за період
    /// нічим не відрізняється від нуля вимірювання.
    /// </remarks>
    private static (decimal? Numeric, string? Text) Value(JsonElement item)
    {
        if (!item.TryGetProperty("Value", out var value))
        {
            return (null, null);
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDecimal(out var number)
                ? (number, null)

                // Число, що не вміщується в decimal, — це не число вимірювання,
                // а маркер джерела. Округлити його означало б записати вигадку.
                : (null, value.GetRawText()),
            JsonValueKind.String => (null, value.GetString()),
            JsonValueKind.True or JsonValueKind.False => (null, value.GetRawText()),
            JsonValueKind.Object => (null, Text(value, "Name") ?? value.GetRawText()),
            _ => (null, null),
        };
    }

    /// <summary>Якість у термінах джерела.</summary>
    private static string Quality(JsonElement item)
    {
        var good = !item.TryGetProperty("Good", out var g) || g.ValueKind != JsonValueKind.False;
        var questionable = item.TryGetProperty("Questionable", out var q) && q.ValueKind == JsonValueKind.True;

        return !good ? "Bad" : questionable ? "Questionable" : "Good";
    }

    private static string? Text(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Iso(DateTime moment)
        => Uri.EscapeDataString(
            moment.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
}
