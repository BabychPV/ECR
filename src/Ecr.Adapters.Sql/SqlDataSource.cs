using System.Data;
using System.Net;
using System.Net.Sockets;
using Ecr.Application.Errors;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Microsoft.Data.SqlClient;

namespace Ecr.Adapters.Sql;

/// <summary>
/// Читання з довільної SQL-бази — джерела, яке до PI не має стосунку (ФВ-11.8).
/// </summary>
/// <remarks>
/// Іменований випадок — <b>FLERT</b> (`B14` §7): зовнішня БД із часовими рядами
/// об'ємів газу й рідин. Вона **не зникає після переходу** — на відміну від
/// шаблонів PI AF, які після cutover розділу вимикаються, FLERT лишається
/// єдиним постачальником реальних вимірів.
/// <para>
/// ⛔ Ні синонімів, ні linked server: сьогодні доступ до FLERT зроблений
/// синонімами (<c>Utility_RebindFlertSynonyms</c>), і саме від цього
/// відмовляємось (`B18` §14.5). Синонім прив'язує НАШУ схему до чужого сервера:
/// він переживає розгортання, не видний у міграціях і ламається мовчки при
/// переїзді середовища. Тут джерело — рядок з'єднання в
/// <c>ext.DataSource.Endpoint</c>, тобто конфігурація, яку видно.
/// </para>
/// <para>
/// ⛔ Запити НЕ мають типового значення, і це не недогляд. Імена таблиць і
/// колонок FLERT — факт про світ замовника, якого немає в цьому репозиторії;
/// вигаданий дефолт виглядав би як робоче налаштування і мовчки збирав би
/// порожнечу. Натомість <b>наш</b> бік контракту заданий жорстко: імена
/// колонок результату і набір параметрів (див. <see cref="ValueQueryKey"/>).
/// </para>
/// <para>
/// ⛔ <see cref="IExternalDataSource.ReadCurrentAsync"/> навмисно НЕ
/// перевизначено: типова реалізація відмовляє <c>ECR-INT-0422</c>
/// (<c>.currentValueNotSupported</c>). Довідники з SQL-джерела не
/// синхронізуються — master там ECR (D-50, D-199), а FLERT дає часові ряди, не
/// атрибути елементів.
/// </para>
/// </remarks>
/// <param name="store">Читання конфігурації джерела.</param>
/// <param name="secrets">Значення секрету за іменем (ФВ-6.11).</param>
/// <param name="settings">Канал налаштувань: тексти запитів.</param>
public sealed class SqlDataSource(
    ICollectionStore store, ISecretProvider secrets, ISecretProvider? settings = null)
    : IExternalDataSource
{
    /// <summary>Стеля рядків каталогу за один обхід.</summary>
    /// <remarks>Та сама причина, що й у сусіднього транспорту: каталог читає
    /// людина, і двадцять тисяч рядків у випадному списку — не повнота.</remarks>
    public const int MaxCatalogRows = 20_000;

    /// <summary>Скільки разів повторювати операцію, яка відмовила транзитивно.</summary>
    public const int MaxAttempts = 3;

    /// <summary>Стеля довжини шляху сутності — та сама, що в <c>ext.SourceEntity.EntityPath</c>.</summary>
    public const int MaxSourcePath = 400;

    /// <summary>Базова затримка між спробами; далі подвоюється.</summary>
    public static TimeSpan RetryDelay => TimeSpan.FromSeconds(2);

    private const string SourceUnavailable = "ECR-INT-0503";

    /// <summary>Джерело відмовило в автентифікації — не те саме, що недоступність (<c>H-20</c>).</summary>
    private const string AuthenticationRefused = "ECR-INT-0502";

    /// <summary>
    /// Результат запиту значень не відповідає контракту — мітка не читається або
    /// йде не по черзі (аудит A7). Той самий код, що «тип запиту не виконується»:
    /// обидва — дефект налаштування, який повторенням не минає, тож не <c>0503</c>.
    /// </summary>
    private const string ResultRefused = IExternalDataSource.QueryRefusedCode;

    /// <summary>Ключ запиту каталогу.</summary>
    /// <remarks>
    /// Результат мусить мати колонки <c>Code</c>, <c>DisplayName</c>,
    /// <c>EntityPath</c>, <c>Uom</c>, <c>DataType</c>. Обов'язкова з них лише
    /// <c>Code</c> — решта може бути <c>NULL</c> або бути відсутньою.
    /// </remarks>
    public const string CatalogQueryKey = "Sql:CatalogQuery";

    /// <summary>Ключ запиту значень.</summary>
    /// <remarks>
    /// Параметри — <c>@path</c>, <c>@from</c>, <c>@to</c>; межі напіввідкриті
    /// (<c>@from</c> включно, <c>@to</c> виключно). Колонки результату —
    /// <c>Ts</c>, <c>Val</c> і, за наявності, <c>Uom</c> і <c>Quality</c>.
    /// <para>
    /// ⛔ Рядки — <c>ORDER BY</c> за міткою (неспадно): на цьому стоїть хвіст
    /// обрізаного батча, і адаптер порушення відхиляє (аудит A7). <c>Ts</c> —
    /// <c>datetimeoffset</c> (конвертується в UTC) або дата-час без поясу
    /// (вважається UTC — припущення, див. <c>Timestamp</c>).
    /// </para>
    /// <para>
    /// ⛔ Параметри — справжні <see cref="SqlParameter"/>, а не підстановка в
    /// текст. Сусідній <c>PiSqlClientDataSource</c> змушений підставляти
    /// шаблон і атрибут літералами, бо RTQP не парсить параметр у заголовку
    /// таблиці значень; тут такого обмеження немає, отже й конкатенації немає.
    /// </para>
    /// </remarks>
    public const string ValueQueryKey = "Sql:ValueQuery";

    /// <inheritdoc />
    public ExternalTransport Transport => ExternalTransport.Sql;

    /// <inheritdoc />
    public async Task<IReadOnlyList<SourceEntityDescriptor>> DiscoverAsync(
        int dataSourceId, CancellationToken ct)
    {
        var source = await store.FindDataSourceAsync(dataSourceId, ct).ConfigureAwait(false)
                     ?? throw Unavailable(dataSourceId);

        var query = Query(source.Code, CatalogQueryKey);

        await using var connection = await OpenAsync(source, ct).ConfigureAwait(false);
        await using var command = new SqlCommand(query, connection);

        var result = new List<SourceEntityDescriptor>();

        // ⛔ Без SequentialAccess, на відміну від сусіднього транспорту. Там
        // запит свій і порядок колонок відомий; тут запит — конфігурація, тож
        // порядок колонок задає замовник. У послідовному режимі звернення до
        // колонки «назад» кидає виняток, і джерело з колонками в іншому
        // порядку падало б із помилки, що не має нічого спільного з причиною.
        await using var reader = await RetryAsync(
            () => command.ExecuteReaderAsync(ct),
            IsTransientSqlFailure,
            ct).ConfigureAwait(false);

        var columns = Columns(reader);

        while (result.Count < MaxCatalogRows && await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var code = Text(reader, columns, "Code");

            if (string.IsNullOrWhiteSpace(code))
            {
                // Рядок без коду створити сутність не може: `ext.SourceEntity`
                // унікальний за `(DataSourceId, Code)`. Пропустити тихо краще,
                // ніж завести сутність із порожнім ключем.
                continue;
            }

            result.Add(new SourceEntityDescriptor(
                code,
                Text(reader, columns, "DisplayName"),
                Text(reader, columns, "EntityPath"),

                // ⚠ Одиниця джерела йде в каталог обов'язково: побачити її
                // треба при налаштуванні, а не через місяць на звірці (ФВ-16.9).
                Text(reader, columns, "Uom"),
                Text(reader, columns, "DataType")));
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<CollectionResult> ReadAsync(CollectionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // ⛔ Лише сирі точки (HSE301 F4, D-172): контракт запиту значень
        // (`ValueQueryKey`) кроку не має, і інтерпольований запит мовчки
        // отримав би сирі рядки. Відмова — ДО джерела й до пошуку запиту.
        if (request.Kind != SourceQueryKind.Raw)
        {
            throw IExternalDataSource.QueryKindNotSupported(request.Kind, Transport);
        }

        var source = await store.FindDataSourceAsync(request.DataSourceId, ct).ConfigureAwait(false)
                     ?? throw Unavailable(request.DataSourceId);

        var query = Query(source.Code, ValueQueryKey);

        await using var connection = await OpenAsync(source, ct).ConfigureAwait(false);
        await using var command = new SqlCommand(query, connection);

        // ⛔ Саме параметри. `SourcePath` приходить із конфігурації мапінгу,
        // тобто з бази, і вставлений у текст запиту він був би ін'єкцією з
        // власного сховища — найгіршим її різновидом, бо джерело виглядає
        // довіреним.
        //
        // ⚠ Розмір оголошений СТАЛИЙ (`MaxSourcePath` — стеля
        // `ext.SourceEntity.EntityPath`), а не виведений зі значення: інакше
        // кожна довжина шляху дає серверу джерела власний план запиту.
        // Довший шлях ВІДХИЛЯЄТЬСЯ, а не обрізається: обрізаний шлях — це не
        // помилка, а тиша, бо джерело чесно відповість «такого немає».
        if (request.SourcePath.Length > MaxSourcePath)
        {
            throw new BusinessRuleException(
                SourceUnavailable,
                $"Шлях сутності довший за {MaxSourcePath} символів: збирати за ним не можна.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0503.sourcePathTooLong",
                    ["dataSource"] = source.Code,
                    ["maxLength"] = MaxSourcePath.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        command.Parameters.Add(new SqlParameter("@path", SqlDbType.NVarChar, MaxSourcePath)
        {
            Value = request.SourcePath,
        });
        command.Parameters.Add(new SqlParameter("@from", SqlDbType.DateTime2) { Value = request.FromUtc });
        command.Parameters.Add(new SqlParameter("@to", SqlDbType.DateTime2) { Value = request.ToUtc });

        var points = new List<SourceDataPoint>();

        // ⛔ Без SequentialAccess — причина та сама, що й у DiscoverAsync:
        // порядок колонок тут належить запитові з конфігурації, а не нам.
        await using var reader = await RetryAsync(
            () => command.ExecuteReaderAsync(ct),
            IsTransientSqlFailure,
            ct).ConfigureAwait(false);

        var columns = Columns(reader);
        DateTime? previous = null;

        while (points.Count < request.MaxPoints && await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            // ⛔ Рядок із міткою, яку не прочитано, НЕ пропускається (аудит A7).
            // Доти `continue` мовчки відкидав, наприклад, усі рядки з колонкою
            // `datetimeoffset`: батч виходив порожнім і необрізаним, і прогін
            // записував покриття за весь інтервал — дірку, яку наздоганяння
            // вже ніколи не знайде.
            var timestamp = Timestamp(reader, columns, source.Code, request.SourcePath);
            EnsureOrdered(previous, timestamp, source.Code, request.SourcePath);
            previous = timestamp;

            var (numeric, text) = Value(Raw(reader, columns, "Val"));

            points.Add(new SourceDataPoint(
                request.SourcePath,
                timestamp,
                numeric,
                text,
                Text(reader, columns, "Uom"),
                Text(reader, columns, "Quality") ?? "Good"));
        }

        // Повний батч, за яким є ще рядок, означає, що хвіст діапазону лишився
        // непрочитаним — і покриття за нього писати не можна (ER-I-03).
        // ⚠ Порожній батч обрізаним НЕ вважається: із MaxPoints = 0 умова була
        // б істинною завжди, а points[^1] упало б на порожньому списку.
        // ⚠ Про обрізання питаємо НАСТУПНИЙ рядок, а не лічильник: рівно
        // MaxPoints рядків — це прочитаний до кінця інтервал, а не хвіст.
        var truncated = points.Count > 0
                        && points.Count >= request.MaxPoints
                        && await reader.ReadAsync(ct).ConfigureAwait(false);

        if (truncated)
        {
            // ⛔ Хвіст `[остання мітка, ToUtc)` правдивий лише тоді, коли
            // непрочитане лежить ПІЗНІШЕ за прочитане. Порядок префікса
            // перевірено вище; перший непрочитаний рядок перевіряється тут —
            // інакше запит, що впорядкований «майже», оголосив би хвостом
            // інтервал, у якому пропущеної точки немає.
            EnsureOrdered(
                previous, Timestamp(reader, columns, source.Code, request.SourcePath), source.Code, request.SourcePath);
        }

        return new CollectionResult(
            points,
            truncated ? [new TimeInterval(points[^1].Timestamp, request.ToUtc)] : [],
            null);
    }

    /// <summary>Текст запиту: спершу власний запит джерела, далі спільний.</summary>
    /// <param name="code">Код джерела — щоб FLERT і сусідня SQL-база не ділили один запит.</param>
    /// <param name="key">Ключ запиту.</param>
    /// <remarks>
    /// ⚠ Канал налаштувань тут той самий <see cref="ISecretProvider"/>, що й у
    /// <c>PiSqlClientDataSource</c>. Запит не секрет, і читати його через порт
    /// секретів — не краса; але друга дорога до конфігурації в сусідньому
    /// адаптері коштувала б дорожче за цю незграбність.
    /// </remarks>
    private string Query(string code, string key)
    {
        var scoped = key.Insert("Sql:".Length, $"{code}:");

        var configured = settings?.Find(scoped) is { Length: > 0 } own
            ? own
            : settings?.Find(key);

        if (string.IsNullOrWhiteSpace(configured))
        {
            // ⛔ Не вигадуємо запит. Джерело без нього не «порожнє», а
            // ненаштоване, і сказати це треба ключем, який бракує: інакше
            // налаштування шукають у коді, якого не буде.
            throw new BusinessRuleException(
                SourceUnavailable,
                $"Джерело {code} не має запиту {scoped} (або спільного {key}): збирати нічим.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0503.queryNotConfigured",
                    ["dataSource"] = code,
                    ["settingKey"] = scoped,
                });
        }

        return configured;
    }

    /// <summary>
    /// L3-05 (<c>D-279</c>): та сама політика адреси, що й при збереженні джерела, — ще раз перед
    /// з'єднанням.
    /// </summary>
    /// <remarks>
    /// ⛔ Збереження перевіряє лише НОВІ адреси (<c>D-245</c>: наявні джерела ретроспективно не
    /// перевіряються), а ім'я між збереженням і з'єднанням може розв'язатися інакше. Рядок —
    /// нормалізований <see cref="SqlConnectionStringBuilder"/> (синоніми ключів зведені), тож політика
    /// бачить саме те, куди піде клієнт. Пароль ще не підставлено — секрет політику не проходить.
    /// </remarks>
    private static async Task RequireAllowedAddressAsync(string code, string connectionString, CancellationToken ct)
    {
        var verdict = DataSourceEndpointPolicy.CheckSqlClientConnectionString(connectionString);

        if (verdict == EndpointVerdict.Allowed)
        {
            foreach (var host in DataSourceEndpointPolicy.SqlClientHostsOf(connectionString) ?? [])
            {
                if (host.Length == 0 || IPAddress.TryParse(host, out _))
                {
                    continue;
                }

                IPAddress[] resolved;

                try
                {
                    resolved = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    // Ім'я не розв'язується — з'єднання впаде саме, своєю зрозумілою помилкою.
                    continue;
                }

                if (resolved.Any(DataSourceEndpointPolicy.IsLinkLocal))
                {
                    verdict = EndpointVerdict.HostForbidden;
                    break;
                }
            }
        }

        if (verdict == EndpointVerdict.Allowed)
        {
            return;
        }

        // ⚠ Відмова називає джерело й вердикт, а не вміст рядка з'єднання.
        throw new BusinessRuleException(
            SourceUnavailable,
            $"Адресу джерела {code} відхилено політикою адреси ({verdict}): збір не з'єднується.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-INT-0503.endpointForbidden",
                ["dataSource"] = code,
            });
    }

    /// <summary>Відкриває з'єднання під службовим обліковим записом.</summary>
    /// <remarks>
    /// ⛔ Секрет береться <b>за іменем</b> і ніколи з конфігурації (ФВ-6.11,
    /// D-11). Рядок з'єднання з паролем не логується.
    /// </remarks>
    /// <param name="source">Джерело з <c>ext.DataSource</c>.</param>
    /// <param name="ct">Скасування.</param>
    private async Task<SqlConnection> OpenAsync(
        Domain.Entities.External.DataSource source, CancellationToken ct)
    {
        SqlConnectionStringBuilder builder;

        try
        {
            builder = new SqlConnectionStringBuilder(source.Endpoint);
        }
        catch (ArgumentException ex)
        {
            // Зіпсований рядок з'єднання — недоступне джерело з погляду збору,
            // а не необроблений виняток: інакше в журналі прогону лежало б
            // «ArgumentException» без натяку, що правити треба поле Endpoint.
            throw new BusinessRuleException(
                SourceUnavailable,
                $"Рядок з'єднання джерела {source.Code} не читається: {ex.Message}",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0503.connectionStringBroken",
                    ["dataSource"] = source.Code,
                });
        }

        await RequireAllowedAddressAsync(source.Code, builder.ConnectionString, ct).ConfigureAwait(false);

        // ⚠ Каталог із конфігурації джерела перекриває той, що в рядку
        // з'єднання: `ext.DataSource.Catalog` — це поле, яке видно в
        // конфігураторі, а `Initial Catalog` усередині рядка — ні.
        if (!string.IsNullOrWhiteSpace(source.Catalog))
        {
            builder.InitialCatalog = source.Catalog;
        }

        if (secrets.Find(source.SecretName) is { Length: > 0 } secret)
        {
            builder.Password = secret;
        }

        var connection = new SqlConnection(builder.ConnectionString);

        // Прапорець, а не `catch`: будь-який шлях виходу, що не повернув
        // з'єднання, мусить його закрити — зокрема скасування токена.
        var opened = false;
        try
        {
            await RetryAsync(
                async () => { await connection.OpenAsync(ct).ConfigureAwait(false); return true; },
                IsTransientSqlFailure,
                ct).ConfigureAwait(false);
            opened = true;
            return connection;
        }
        catch (SqlException ex)
        {
            // ⛔ Відмова в автентифікації відділяється від недоступності
            // (`H-20`). Без цього неправильний пароль службового запису
            // виглядав би як тимчасово недоступна база — тобто збір ішов би в
            // наздоганяння і рапортував успіх.
            if (IsAuthenticationFailure(ex))
            {
                throw new SourceAuthenticationException(
                    AuthenticationRefused,
                    $"SQL-джерело {source.Code} не приймає облікові дані: {ex.Message}",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-INT-0502.credentialsRefused",
                        ["dataSource"] = source.Code,
                    });
            }

            throw new BusinessRuleException(
                SourceUnavailable,
                $"SQL-джерело {source.Code} не з'єднується: {ex.Message}",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0503.connectFailed",
                    ["dataSource"] = source.Code,
                });
        }
        finally
        {
            if (!opened)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>«Login failed for user» і «not associated with a trusted connection».</summary>
    /// <remarks>
    /// ⚠ Номер помилки, а не текст: текст залежить від мови сервера, і пошук
    /// підрядка розсипався б на першій же машині з іншою локаллю.
    /// </remarks>
    private static readonly int[] AuthenticationErrors = [18456, 18452];

    /// <summary>Чи це відмова саме в автентифікації.</summary>
    /// <param name="error">Виняток клієнта.</param>
    private static bool IsAuthenticationFailure(SqlException error)
    {
        foreach (SqlError item in error.Errors)
        {
            if (Array.IndexOf(AuthenticationErrors, item.Number) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Чи можна повторити цю відмову — транзитивна і не про пароль.</summary>
    /// <remarks>
    /// ⛔ Відмова в автентифікації не транзитивна: той самий пароль дасть ту
    /// саму відповідь, а повтор лише подовжить збір на час усіх спроб замість
    /// негайного сигналу, що облікові дані джерела треба поправити.
    /// </remarks>
    /// <param name="ex">Виняток спроби.</param>
    public static bool IsTransientSqlFailure(Exception ex)
        => ex is SqlException sql && sql.IsTransient && !IsAuthenticationFailure(sql);

    /// <summary>
    /// Повторює операцію з експоненційною затримкою.
    /// </summary>
    /// <remarks>
    /// ⚠ Цикл повторюється по суті за сусіднім <c>PiSqlClientDataSource</c>, і
    /// це свідомо. Спільний помічник мусив би жити в одному з двох адаптерів —
    /// тобто цей проєкт посилався б на <c>Ecr.Adapters.PiAf</c>, а це рівно та
    /// залежність, заради усунення якої SQL-джерело й винесене окремо.
    /// <para>
    /// Публічний навмисно: <see cref="SqlException"/> ззовні збірки не
    /// сконструюєш — усі конструктори внутрішні, — тож перевірити сам цикл
    /// можна лише підставним <paramref name="operation"/>.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">Результат операції.</typeparam>
    /// <param name="operation">Операція, яку повторюємо.</param>
    /// <param name="isTransient">Чи варто повторювати саме цей виняток.</param>
    /// <param name="ct">Скасування: під час очікування між спробами теж діє.</param>
    public static async Task<T> RetryAsync<T>(
        Func<Task<T>> operation, Func<Exception, bool> isTransient, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(isTransient);

        var delay = RetryDelay;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < MaxAttempts && !ct.IsCancellationRequested && isTransient(ex))
            {
                // Транзитивна відмова — впаде в затримку й повтор нижче.
            }

            await Task.Delay(delay, ct).ConfigureAwait(false);
            delay += delay;
        }
    }

    /// <summary>Імена колонок результату.</summary>
    /// <remarks>
    /// ⚠ Набір читається ОДИН раз на reader, а не питається на кожному рядку:
    /// <c>Uom</c> і <c>Quality</c> необов'язкові, і звернення до відсутньої
    /// колонки — виняток, а не <c>null</c>.
    /// </remarks>
    /// <param name="reader">Відкритий reader.</param>
    private static Dictionary<string, int> Columns(SqlDataReader reader)
    {
        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < reader.FieldCount; i++)
        {
            columns[reader.GetName(i)] = i;
        }

        return columns;
    }

    /// <summary>Значення колонки; <c>null</c> — колонки немає або вона порожня.</summary>
    /// <param name="reader">Відкритий reader.</param>
    /// <param name="columns">Набір колонок.</param>
    /// <param name="name">Ім'я колонки.</param>
    private static object? Raw(SqlDataReader reader, Dictionary<string, int> columns, string name)
    {
        if (!columns.TryGetValue(name, out var ordinal) || reader.IsDBNull(ordinal))
        {
            return null;
        }

        return reader.GetValue(ordinal);
    }

    /// <summary>Текстове значення колонки.</summary>
    /// <param name="reader">Відкритий reader.</param>
    /// <param name="columns">Набір колонок.</param>
    /// <param name="name">Ім'я колонки.</param>
    private static string? Text(SqlDataReader reader, Dictionary<string, int> columns, string name)
        => Raw(reader, columns, name) switch
        {
            null => null,
            string text => text,
            var other => other.ToString(),
        };

    /// <summary>Значення точки: число або текст, ніколи обидва.</summary>
    /// <remarks>
    /// ⛔ <c>double</c> із джерела переводиться в <c>decimal</c> тут, на межі, і
    /// далі його немає (D-30). Звітні числа звіряються до копійки, а подвійна
    /// точність дає розбіжність, якої ніхто не може пояснити.
    /// </remarks>
    /// <param name="raw">Значення з reader.</param>
    private static (decimal? Numeric, string? Text) Value(object? raw)
        => raw switch
        {
            null or DBNull => (null, null),
            decimal number => (number, null),
            double number when number is >= (double)decimal.MinValue and <= (double)decimal.MaxValue
                => ((decimal)number, null),
            float number => ((decimal)number, null),
            long number => (number, null),
            int number => (number, null),
            short number => (number, null),
            byte number => (number, null),
            bool flag => (null, flag ? "true" : "false"),
            string text => (null, text),

            // Незнайомий тип не вгадується: текстове подання видно у звіті про
            // збір, підставлений нуль — ні.
            _ => (null, raw.ToString()),
        };

    /// <summary>Мітка часу точки в UTC.</summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>datetimeoffset</c> — <b>конвертується</b> в UTC через
    /// <see cref="DateTimeOffset.UtcDateTime"/>: пояс записано в самому
    /// значенні, і вгадувати нічого не треба.</item>
    /// <item><c>datetime2</c>, <c>datetime</c>, <c>smalldatetime</c>, <c>date</c>
    /// (приходять як <see cref="DateTime"/> без поясу) — <b>вважаються UTC</b>
    /// (<see cref="DateTime.SpecifyKind"/>, переозначення, а не конвертація).
    /// ⚠ Це ЯВНЕ ПРИПУЩЕННЯ, а не знання (аудит A7). Поля поясу в
    /// <c>ext.DataSource</c> немає, і ні `06-integration.md` §6.5a, ні `B20` не
    /// кажуть, у якому поясі FLERT пише <c>datetime</c>. Воно принаймні
    /// узгоджене: межі <c>@from</c>/<c>@to</c> ідуть у запит теж як UTC
    /// <c>datetime2</c>, тож фільтр і мітки точок живуть в одній шкалі. Якщо
    /// джерело пише місцевий час, увесь ряд буде зсунутий на зсув поясу —
    /// лікується полем поясу джерела або <c>AT TIME ZONE</c> у самому запиті
    /// (тоді колонка стає <c>datetimeoffset</c> і йде першою гілкою).</item>
    /// <item>усе інше, зокрема <c>NULL</c>, рядок, <c>time</c>, — <b>відмова</b>
    /// <c>ECR-INT-0422</c>, а не пропуск рядка: пропущений рядок перетворював
    /// невдалий інтервал на «зібраний повністю».</item>
    /// </list>
    /// </remarks>
    /// <param name="reader">Reader на поточному рядку.</param>
    /// <param name="columns">Набір колонок.</param>
    /// <param name="dataSource">Код джерела — для тексту відмови.</param>
    /// <param name="sourcePath">Шлях сутності — для тексту відмови.</param>
    private static DateTime Timestamp(
        SqlDataReader reader, Dictionary<string, int> columns, string dataSource, string sourcePath)
        => Raw(reader, columns, "Ts") switch
        {
            DateTimeOffset zoned => zoned.UtcDateTime,
            DateTime unzoned => DateTime.SpecifyKind(unzoned, DateTimeKind.Utc),
            var other => throw new BusinessRuleException(
                ResultRefused,
                $"Запит значень джерела {dataSource} для «{sourcePath}» повернув Ts типу "
                + $"{other?.GetType().Name ?? "NULL"}: мітку часу прочитати не можна, інтервал не зібрано.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0422.timestampUnreadable",
                    ["dataSource"] = dataSource,
                    ["sourcePath"] = sourcePath,
                    ["valueType"] = other?.GetType().Name ?? "NULL",
                }),
        };

    /// <summary>Мітки йдуть неспадно — інакше відмова.</summary>
    /// <remarks>
    /// ⛔ Вибрано відмову, а не сортування в пам'яті (аудит A7). Сортування
    /// лагодить лише те, що вже прочитано: при обрізаному батчі непрочитані
    /// рядки невідомі, і хвіст <c>[остання мітка, ToUtc)</c> за невпорядкованого
    /// запиту пропускає точки раніше за цю мітку. А відмова лише на великих
    /// батчах зробила б дефект конфігурації випадковим. Тому контракт один:
    /// запит значень мусить мати <c>ORDER BY</c> за міткою, і порушення видно
    /// на першому ж прогоні з двома рядками не по черзі. Рівні мітки дозволені.
    /// <para>
    /// ⚠ Межа перевірки чесно: вона бачить прочитане плюс ОДИН наступний рядок.
    /// Запит без <c>ORDER BY</c>, що випадково віддав упорядкований префікс,
    /// пройде; гарантію дає лише <c>ORDER BY</c> у самому запиті.
    /// </para>
    /// </remarks>
    /// <param name="previous">Попередня мітка; <c>null</c> — рядок перший.</param>
    /// <param name="current">Поточна мітка.</param>
    /// <param name="dataSource">Код джерела — для тексту відмови.</param>
    /// <param name="sourcePath">Шлях сутності — для тексту відмови.</param>
    private static void EnsureOrdered(DateTime? previous, DateTime current, string dataSource, string sourcePath)
        => SourceRowOrder.EnsureOrdered(previous, current, dataSource, sourcePath);

    /// <summary>Джерела немає або воно вимкнене.</summary>
    /// <param name="dataSourceId">Ідентифікатор із запиту.</param>
    /// <remarks>
    /// ⚠ Ключ несе й цей кидок, хоча кидається результат методу, а не
    /// літеральне <c>throw new …Exception(</c>. Доти храповик локалізації таких
    /// місць не бачив; з 2026-09-23 <c>MessageKeyRatchetTests</c> рахує і
    /// фабрики (<c>BusinessRuleException X(…) =&gt; new(…)</c>), тож ключ тут —
    /// не лише чесність, а й вимога сторожа.
    /// </remarks>
    private static BusinessRuleException Unavailable(int dataSourceId)
        => new(
            SourceUnavailable,
            $"Джерело {dataSourceId} не існує або вимкнене.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-INT-0503.sourceMissing",
                ["dataSourceId"] = dataSourceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
}
