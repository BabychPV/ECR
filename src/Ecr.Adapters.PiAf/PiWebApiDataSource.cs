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
public sealed partial class PiWebApiDataSource(
    HttpClient http, ICollectionStore store, ISecretProvider secrets)
    : IExternalDataSource, IHierarchicalCatalogSource, IBatchCollectionSource
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
    /// Лише кореневий рівень. Глибше — ліниво, на запитаний вузол
    /// (<see cref="BrowseAsync"/>, <see cref="AttributesAsync"/>): повний обхід
    /// бази AF означав би хвилини очікування на дереві, у якому користувач
    /// відкриє два вузли.
    /// </remarks>
    public async Task<IReadOnlyList<SourceEntityDescriptor>> DiscoverAsync(
        int dataSourceId, CancellationToken ct)
        => await BrowseAsync(dataSourceId, null, ct).ConfigureAwait(false);

    /// <summary>Скільки позицій одного рівня читати з джерела, йдучи за <c>Links.Next</c>.</summary>
    public const int MaxItemsPerLevel = 1_000;

    /// <inheritdoc />
    /// <remarks>
    /// Корінь — <c>assetdatabases/{webId}/elements</c>; вузол — спершу WebId
    /// елемента за шляхом (<c>elements?path=</c>), потім
    /// <c>elements/{webId}/elements</c>. Лише прямі діти: <c>searchFullHierarchy=false</c>.
    /// </remarks>
    public async Task<IReadOnlyList<SourceEntityDescriptor>> BrowseAsync(
        int dataSourceId, string? parentPath, CancellationToken ct)
    {
        var source = await SourceAsync(dataSourceId, ct).ConfigureAwait(false);

        var collection = string.IsNullOrWhiteSpace(parentPath)
            ? $"assetdatabases/{Uri.EscapeDataString(source.Catalog ?? string.Empty)}/elements"
            : $"elements/{Uri.EscapeDataString(await ElementWebIdAsync(source, parentPath, ct).ConfigureAwait(false))}/elements";

        return await ItemsAsync(
            source,
            collection + "?searchFullHierarchy=false",
            item => new SourceEntityDescriptor(
                Text(item, "Name") ?? string.Empty,
                Text(item, "Description"),
                Text(item, "Path"),
                SourceUnitSymbol: null,
                DataType: "Element",

                // GUID елемента, а не WebId: WebId кодує ще й сервер і формат
                // (WebID 2.0), тож змінюється разом із ними; GUID — ні (S1).
                ExternalId: Text(item, "Id")),
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Прямі діти <paramref name="root"/> через <see cref="BrowseAsync"/>; адреса читання — шлях
    /// (<c>attributes?path=шлях|атрибут</c>). Повний — лише якщо не дійшли до
    /// <see cref="MaxItemsPerLevel"/> і кожен елемент має <c>Id</c>.
    /// </remarks>
    public async Task<SourceElementsResult> DiscoverElementsAsync(int dataSourceId, string root, CancellationToken ct)
    {
        var children = await BrowseAsync(dataSourceId, root, ct).ConfigureAwait(false);

        var complete = children.Count < MaxItemsPerLevel;
        var elements = new List<SourceElement>(children.Count);

        foreach (var child in children)
        {
            if (string.IsNullOrWhiteSpace(child.ExternalId) || string.IsNullOrWhiteSpace(child.EntityPath))
            {
                complete = false;
                continue;
            }

            elements.Add(new SourceElement(child.ExternalId, child.Code, child.EntityPath, child.EntityPath));
        }

        return new SourceElementsResult(elements, complete);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>elements/{webId}/attributes</c>: <c>EntityPath</c> — повний шлях
    /// атрибута (саме його потім читає <see cref="ReadAsync"/>), одиниця —
    /// <c>DefaultUnitsName</c>, тип — <c>Type</c>.
    /// </remarks>
    public async Task<IReadOnlyList<SourceEntityDescriptor>> AttributesAsync(
        int dataSourceId, string elementPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(elementPath);

        var source = await SourceAsync(dataSourceId, ct).ConfigureAwait(false);
        var (webId, elementId) = await ElementAsync(source, elementPath, ct).ConfigureAwait(false);
        return await ItemsAsync(
            source,
            $"elements/{Uri.EscapeDataString(webId)}/attributes?searchFullHierarchy=false",
            item =>
            {
                var units = Text(item, "DefaultUnitsName");
                return new SourceEntityDescriptor(
                    Text(item, "Name") ?? string.Empty,
                    Text(item, "Description"),
                    Text(item, "Path"),
                    string.IsNullOrWhiteSpace(units) ? null : units,
                    Text(item, "Type"),

                    // Атрибут належить елементу: зовнішній ключ запису довідника —
                    // GUID саме елемента (FEATURE-REGISTRY-SYNC §2.1).
                    elementId);
            },
            ct).ConfigureAwait(false);
    }

    private async Task<string> ElementWebIdAsync(
        Domain.Entities.External.DataSource source, string path, CancellationToken ct)
        => (await ElementAsync(source, path, ct).ConfigureAwait(false)).WebId;

    /// <summary>WebId і GUID елемента за шляхом.</summary>
    private async Task<(string WebId, string? Id)> ElementAsync(
        Domain.Entities.External.DataSource source, string path, CancellationToken ct)
    {
        using var element = await GetAsync(
            source.Endpoint, $"elements?path={Uri.EscapeDataString(path)}", source.SecretName, ct)
            .ConfigureAwait(false);

        var id = Text(element.RootElement, "Id");

        return (Text(element.RootElement, "WebId")
               ?? throw new BusinessRuleException(
                   SourceUnavailable,
                   $"PI Web API не знайшов елемент {path}.",
                   new Dictionary<string, object?>
                   {
                       ["messageKey"] = "err.ECR-INT-0503.catalogUnavailable",
                       ["code"] = source.Code.ToString(),
                       ["path"] = path,
                   }), id);
    }

    /// <summary><c>Items</c> колекції з переходом за <c>Links.Next</c> до стелі.</summary>
    /// <remarks>
    /// ⛔ <c>Links.Next</c> іде лише на той самий хост, що й джерело: інакше
    /// заголовок автентифікації пішов би за адресою, яку назвала відповідь.
    /// </remarks>
    private async Task<List<SourceEntityDescriptor>> ItemsAsync(
        Domain.Entities.External.DataSource source,
        string path,
        Func<JsonElement, SourceEntityDescriptor> map,
        CancellationToken ct)
    {
        var result = new List<SourceEntityDescriptor>();
        var origin = new Uri($"{source.Endpoint.TrimEnd('/')}/");
        Uri? next = new(origin, path);

        while (next is not null && result.Count < MaxItemsPerLevel)
        {
            // Q-222: `using`, щоб виняток розбору не лишав JsonDocument недиспозженим.
            using var page = await GetAsync(next, path, source.SecretName, ct).ConfigureAwait(false);
            next = null;

            if (page.RootElement.TryGetProperty("Items", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    result.Add(map(item));
                }
            }

            if (page.RootElement.TryGetProperty("Links", out var links)
                && Text(links, "Next") is { } link
                && Uri.TryCreate(link, UriKind.Absolute, out var candidate)
                && Uri.Compare(candidate, origin, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0)
            {
                next = candidate;
            }
        }

        return result.Count > MaxItemsPerLevel ? result[..MaxItemsPerLevel] : result;
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

        // ⛔ Лише сирі точки (HSE301 F4, D-172). Інтерпольований запит тут не
        // реалізовано — і він не підміняється `recorded`: сирі точки під
        // іменем інтерпольованих дали б правдоподібні, але інші числа.
        // Перевірка ДО джерела: відмова конфігурації не чіпає PI.
        if (request.Kind != SourceQueryKind.Raw)
        {
            throw IExternalDataSource.QueryKindNotSupported(request.Kind, Transport);
        }

        var source = await SourceAsync(request.DataSourceId, ct).ConfigureAwait(false);

        // P7: WebId — із кешу екземпляра; повторний пошук того самого шляху
        // на кожен інтервал був половиною всіх запитів наздоганяння.
        var attribute = await AttributeAsync(source, request.SourcePath, ct).ConfigureAwait(false);

        if (attribute.NotFound is { } missing)
        {
            throw missing;
        }

        if (string.IsNullOrWhiteSpace(attribute.WebId))
        {
            return Unresolved(request);
        }

        using var recorded = await GetAsync(
            source.Endpoint,
            $"streams/{Uri.EscapeDataString(attribute.WebId)}/recorded"
            + $"?startTime={Iso(request.FromUtc)}&endTime={Iso(request.ToUtc)}"
            + $"&maxCount={request.MaxPoints.ToString(CultureInfo.InvariantCulture)}",
            source.SecretName,
            ct).ConfigureAwait(false);

        return Batch(Points(recorded.RootElement, request.SourcePath, attribute.DefaultUnits), request);
    }

    /// <summary>Атрибута за шляхом немає — увесь інтервал у відмову.</summary>
    /// <remarks>
    /// Це не «нуль точок»: нуль означав би, що джерело відповіло порожнім
    /// періодом, і покриття за нього записалося б як повне.
    /// </remarks>
    private static CollectionResult Unresolved(CollectionRequest request)
        => new([], [new TimeInterval(request.FromUtc, request.ToUtc)], SourceUnavailable);

    /// <summary>Точки одного атрибута як результат запиту — з хвостом, якщо батч повний.</summary>
    /// <remarks>
    /// ⚠ Повний батч означає, що джерело віддало рівно стелю — і хвіст
    /// діапазону лишився непрочитаним. Мовчазне «зібрано» тут дало б
    /// дірку, позначену як покриття.
    /// ⚠ Порожній батч НЕ вважається обрізаним: із MaxPoints = 0 умова
    /// «набрали стелю» була б істинною завжди, і points[^1] упало б на
    /// порожньому списку — на діапазоні, у якому просто немає даних.
    /// </remarks>
    private static CollectionResult Batch(List<SourceDataPoint> points, CollectionRequest request)
    {
        var truncated = points.Count > 0 && points.Count >= request.MaxPoints;

        return new CollectionResult(
            points,
            truncated ? [new TimeInterval(points[^1].Timestamp, request.ToUtc)] : [],
            null);
    }

    /// <inheritdoc />
    /// <remarks>
    /// На кожен шлях — WebId атрибута (<c>attributes?path=</c>, як у
    /// <see cref="ReadAsync"/>), далі <c>streams/{webId}/value</c>. Для
    /// статичного атрибута (без PI Point) PI Web API віддає його значення тим
    /// самим викликом.
    /// <para>
    /// ⚠ Запит на шлях, а не пакетний <c>streamsets/value</c>: синк довідника
    /// читає десятки атрибутів раз на прогін, а пакет потребував би окремої
    /// обробки часткових відмов у відповіді. Судження S1; переглянути, якщо
    /// кількість атрибутів виросте на порядки.
    /// </para>
    /// <para>
    /// ⛔ Атрибута немає (<c>404</c> або відповідь без <c>WebId</c>) — відмова
    /// цього шляху <c>ECR-INT-0404</c>, решта читається далі. Будь-яка інша
    /// відмова (5xx, автентифікація) летить винятком: знімок неповний.
    /// </para>
    /// </remarks>
    public async Task<CurrentValuesResult> ReadCurrentAsync(
        int dataSourceId, IReadOnlyCollection<string> paths, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var source = await SourceAsync(dataSourceId, ct).ConfigureAwait(false);
        var values = new List<SourceDataPoint>();
        var failures = new List<CurrentValueFailure>();

        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal))
        {
            string? webId;
            string? defaultUnits;

            try
            {
                using var attribute = await GetAsync(
                    source.Endpoint, $"attributes?path={Uri.EscapeDataString(path)}", source.SecretName, ct)
                    .ConfigureAwait(false);
                webId = Text(attribute.RootElement, "WebId");
                defaultUnits = Text(attribute.RootElement, "DefaultUnitsName");
            }
            catch (BusinessRuleException ex) when (IsNotFound(ex))
            {
                webId = null;
                defaultUnits = null;
            }

            if (string.IsNullOrWhiteSpace(webId))
            {
                failures.Add(new CurrentValueFailure(path, PathNotFound, "err.ECR-INT-0404.sourcePathNotFound"));
                continue;
            }

            using var value = await GetAsync(
                source.Endpoint, $"streams/{Uri.EscapeDataString(webId)}/value", source.SecretName, ct)
                .ConfigureAwait(false);

            if (CurrentPoint(value.RootElement, path, defaultUnits) is { } point)
            {
                values.Add(point);
            }
            else
            {
                failures.Add(new CurrentValueFailure(
                    path, SourceUnavailable, "err.ECR-INT-0503.currentValueUnreadable"));
            }
        }

        return new CurrentValuesResult(values, failures);
    }

    /// <summary>Атрибута чи елемента немає.</summary>
    private const string PathNotFound = "ECR-INT-0404";

    /// <summary>Чи відмова <see cref="GetAsync(string, string, string, CancellationToken)"/> — це «такого шляху немає».</summary>
    private static bool IsNotFound(BusinessRuleException error)
        => error.Details is { } details
           && details.TryGetValue("status", out var status)
           && status is "404";

    /// <summary>Одне значення відповіді <c>streams/{webId}/value</c>; без мітки часу — <c>null</c>.</summary>
    private static SourceDataPoint? CurrentPoint(JsonElement item, string sourcePath, string? defaultUnits)
    {
        if (!item.TryGetProperty("Timestamp", out var stamp) || !stamp.TryGetDateTime(out var timestamp))
        {
            return null;
        }

        var (numeric, text) = Value(item);
        var units = Text(item, "UnitsAbbreviation");

        return new SourceDataPoint(
            sourcePath,
            timestamp.ToUniversalTime(),
            numeric,
            text,
            string.IsNullOrWhiteSpace(units) ? defaultUnits : units,
            Quality(item));
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
                     new Dictionary<string, object?>
                     {
                         // Той самий ключ, що SqlDataSource.cs/CollectionRunner.cs/
                         // PiAfCatalogReader.cs/PiSqlClientDataSource.cs.
                         ["messageKey"] = "err.ECR-INT-0503.sourceMissing",
                         ["dataSourceId"] = dataSourceId.ToString(CultureInfo.InvariantCulture),
                     });

        return cached;
    }

    private Domain.Entities.External.DataSource? cached;

    /// <summary>GET із ретраями на 5xx і таймаутах.</summary>
    /// <remarks>
    /// ⛔ 4xx не повторюється: невірний шлях або відмова в доступі від
    /// повторення не виправляються, а лише подовжують збір на час усіх спроб.
    /// </remarks>
    private Task<JsonDocument> GetAsync(
        string endpoint, string path, string secretName, CancellationToken ct)
        => GetAsync(new Uri($"{endpoint.TrimEnd('/')}/{path}"), path, secretName, ct);

    private async Task<JsonDocument> GetAsync(
        Uri uri, string path, string secretName, CancellationToken ct)
    {
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
                            new Dictionary<string, object?>
                            {
                                ["messageKey"] = "err.ECR-INT-0502.piWebApiUnauthorized",
                                ["status"] = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture),
                                ["path"] = path,
                            });
                    }

                    if (attempt >= MaxAttempts || !Retryable(response.StatusCode))
                    {
                        throw new BusinessRuleException(
                            SourceUnavailable,
                            $"PI Web API відповів {(int)response.StatusCode} на {path}.",
                            new Dictionary<string, object?>
                            {
                                ["messageKey"] = "err.ECR-INT-0503.piWebApiErrorStatus",
                                ["status"] = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture),
                                ["path"] = path,
                            });
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
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                // ⛔ Останню спробу теж перехоплюємо — і перетворюємо на
                // `BusinessRuleException`, а не пускаємо `TaskCanceledException`
                // далі (аудит 2026-09-16, §7.1). Сирий виняток минав і
                // `CollectionRunner.ReadAsync`'s `catch
                // (OperationCanceledException) { throw; }`, і watchdog-перевірку
                // в `RunAsync` (та дивиться лише на
                // `watchdog.IsCancellationRequested`, який тут `false` —
                // скасування прийшло від ВНУТРІШНЬОГО таймера HttpClient, не від
                // watchdog). `RunAsync` кидав необробленим, `FinishRunAsync` і
                // `WriteCoverageAsync` не викликалися НІКОЛИ — і рядок
                // `CollectionRun` навічно лишався «Running» замість
                // запланованого «Degraded».
                //
                // ⚠ Умова `!ct.IsCancellationRequested` обов'язкова і тут: якщо
                // скасування прийшло ЗЗОВНІ (watchdog, зупинка сервісу), воно
                // мусить летіти як скасування — саме так збирач і відрізняє
                // «нас зупинили» від «джерело не відповіло».
                throw new BusinessRuleException(
                    SourceUnavailable,
                    $"PI Web API не відповів на {path} за {MaxAttempts} спроб: тайм-аут запиту.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-INT-0503.piWebApiTimeout",
                        ["path"] = path,
                        ["attempts"] = MaxAttempts.ToString(CultureInfo.InvariantCulture),
                        ["reason"] = "timeout",
                    });
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
