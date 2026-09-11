using System.Data;
using System.Data.Odbc;
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;

namespace Ecr.Adapters.PiAf;

/// <summary>
/// Читання через PI SQL Client (RTQP) — ODBC.
/// </summary>
/// <remarks>
/// Підтверджені факти: RTQP **у продуктиві** (63 land-процедури читають через
/// нього), він **тільки для читання**, **не потребує Kerberos-делегування** і
/// працює через ODBC, не OLE DB (`D-46`). Це основний транспорт для масового
/// читання історії й довідників.
/// </remarks>
public sealed class PiSqlClientDataSource(
    ICollectionStore store, ISecretProvider secrets, ISecretProvider? settings = null) : IExternalDataSource
{
    /// <summary>Стеля рядків каталогу за один обхід.</summary>
    /// <remarks>
    /// Каталог читає людина в конфігураторі. Двадцять тисяч атрибутів у
    /// випадному списку — це не повнота, а спосіб зробити список непридатним.
    /// </remarks>
    public const int MaxCatalogRows = 20_000;

    /// <summary>Скільки разів повторювати операцію, яка відмовила транзитивно.</summary>
    /// <remarks>
    /// ⛔ Q-251 (аудит): раніше цього поля не було зовсім — ні відкриття
    /// з'єднання, ні читання не повторювались жодного разу, на відміну від
    /// сусіднього <see cref="PiWebApiDataSource"/>, де саме це правило вже
    /// діяло (`MaxAttempts`). Розбіжність не пояснювалась жодним коментарем
    /// поряд, хоча решта файлу пояснює кожне архітектурне рішення — це був
    /// недогляд, а не свідомий вибір. Значення взято тим самим, що й там: три
    /// спроби, не «поки не вийде» — джерело, яке лежить, від наполегливості
    /// не піднімається.
    /// </remarks>
    public const int MaxAttempts = 3;

    /// <summary>Базова затримка між спробами; далі подвоюється (як у <see cref="PiWebApiDataSource"/>).</summary>
    public static TimeSpan RetryDelay => TimeSpan.FromSeconds(2);

    private const string SourceUnavailable = "ECR-INT-0503";

    /// <summary>Джерело відмовило в автентифікації — не те саме, що недоступність (<c>H-20</c>).</summary>
    private const string AuthenticationRefused = "ECR-INT-0502";

    /// <summary>
    /// Каталог елементів і атрибутів AF.
    /// </summary>
    /// <remarks>
    /// ⚠ Імена об'єктів звірені з експортом чинного рішення
    /// (<c>ECR_01_Air_PISqlClientExportedObjects.sql</c>): саме
    /// <c>[Master].[Element].[Element]</c> і <c>[Master].[Element].[Attribute]</c>,
    /// а не вигадані за аналогією. Колонки <c>UnitOfMeasure</c> і
    /// <c>ValueType</c> звірені додатково з офіційною AVEVA PI SQL DAS
    /// (RTQP Engine) Reference, Element schema
    /// (<c>docs.aveva.com/bundle/pi-sql-data-access-server-rtqp-engine/page/1016070.html</c>,
    /// станом на 2026-03-12): раніше тут стояли неіснуючі <c>UOM</c> і
    /// <c>Type</c> (`Q-197`).
    /// </remarks>
    public const string DefaultCatalogQuery = """
        SELECT e.Name AS ElementName, a.Name AS AttributeName, a.UnitOfMeasure AS Uom, a.ValueType AS DataType
        FROM [Master].[Element].[Attribute] a
        INNER JOIN [Master].[Element].[Element] e ON e.ID = a.ElementID
        ORDER BY e.Name, a.Name
        """;

    /// <summary>Ключ конфігурації, яким запит каталогу можна перевизначити.</summary>
    /// <remarks>
    /// ⚠ Запити винесені в **налаштування** (`P-11`). Імена об'єктів
    /// <c>[Master].[Element].[Attribute]</c> звірені як з експортом чинного
    /// рішення, так і з офіційною схемою (`Q-197`) — живого PI SQL Client у
    /// контурі розробки все ще немає, але саму назву колонки перевіряти вже
    /// не потрібно. Ключ конфігурації лишається на випадок, якщо в NCOC
    /// встановлена версія RTQP Engine, де база все ж розходиться.
    /// </remarks>
    public const string CatalogQueryKey = "PiSqlClient:CatalogQuery";

    /// <summary>Ключ конфігурації для запиту шаблона елемента.</summary>
    public const string TemplateQueryKey = "PiSqlClient:TemplateQuery";

    /// <summary>Ключ конфігурації для запиту значень.</summary>
    /// <remarks>
    /// ⚠ У шаблоні запиту доступні два заповнювачі: <c>{template}</c> і
    /// <c>{attribute}</c>. Обидва підставляються **літералами** — RTQP
    /// вимагає їх такими в заголовку таблиці значень, і параметр там не
    /// парситься. Часові межі й ім'я елемента лишаються звичайними
    /// параметрами <c>?</c>.
    /// </remarks>
    public const string ValueQueryKey = "PiSqlClient:ValueQuery";

    /// <summary>Шаблон елемента: потрібен, бо табличну функцію значень ним параметризують.</summary>
    public const string DefaultTemplateQuery = """
        SELECT e.Template AS Template
        FROM [Master].[Element].[Element] e
        WHERE e.Name = ?
        """;

    /// <inheritdoc />
    public ExternalTransport Transport => ExternalTransport.PiSqlClient;

    /// <inheritdoc />
    public async Task<IReadOnlyList<SourceEntityDescriptor>> DiscoverAsync(
        int dataSourceId, CancellationToken ct)
    {
        var source = await store.FindDataSourceAsync(dataSourceId, ct).ConfigureAwait(false)
                     ?? throw Unavailable($"Джерело {dataSourceId} не існує або вимкнене.");

        using var connection = await OpenAsync(source, ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = Query(CatalogQueryKey, DefaultCatalogQuery);

        var result = new List<SourceEntityDescriptor>();

        // ⚠ Потоково через reader, не ToList() над усім: каталог AF — це
        // десятки тисяч рядків, і матеріалізація його в пам'ять коштує більше,
        // ніж усе читання разом.
        using var reader = await command
            .ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct)
            .ConfigureAwait(false);

        while (result.Count < MaxCatalogRows && await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var element = reader["ElementName"] as string ?? string.Empty;
            var attribute = reader["AttributeName"] as string ?? string.Empty;

            result.Add(new SourceEntityDescriptor(
                $"{element}|{attribute}",
                attribute,
                element,

                // ⚠ Одиниця йде в каталог обов'язково: побачити її треба вже
                // при налаштуванні, а не через місяць на звірці (ФВ-16.9).
                reader["Uom"] as string,
                reader["DataType"] as string));
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<CollectionResult> ReadAsync(CollectionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var source = await store.FindDataSourceAsync(request.DataSourceId, ct).ConfigureAwait(false)
                     ?? throw Unavailable($"Джерело {request.DataSourceId} не існує або вимкнене.");

        var (element, attribute) = Split(request.SourcePath);

        using var connection = await OpenAsync(source, ct).ConfigureAwait(false);

        var template = await TemplateAsync(connection, element, ct).ConfigureAwait(false);

        if (template is null)
        {
            // Елемента за таким іменем немає. Порожній результат тут був би
            // гіршим за відмову: він записав би покриття за інтервал, у якому
            // даних не буде ніколи.
            return new CollectionResult(
                [], [new TimeInterval(request.FromUtc, request.ToUtc)], SourceUnavailable);
        }

        using var command = connection.CreateCommand();
        command.CommandText = ValueQuery(template, attribute);
        command.Parameters.Add(new OdbcParameter("element", OdbcType.NVarChar) { Value = element });
        command.Parameters.Add(new OdbcParameter("from", OdbcType.DateTime) { Value = request.FromUtc });
        command.Parameters.Add(new OdbcParameter("to", OdbcType.DateTime) { Value = request.ToUtc });

        var points = new List<SourceDataPoint>();

        // ⛔ Q-251: те саме читання, що йшло без жодного повтору — разовий
        // таймаут RTQP під навантаженням одразу провалював увесь інтервал.
        using var reader = await RetryAsync(
            () => command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct),
            IsTransientOdbcFailure,
            ct).ConfigureAwait(false);

        while (points.Count < request.MaxPoints && await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (reader["Ts"] is not DateTime timestamp)
            {
                continue;
            }

            var (numeric, text) = Value(reader["Val"]);

            points.Add(new SourceDataPoint(
                request.SourcePath,
                DateTime.SpecifyKind(timestamp, DateTimeKind.Utc),
                numeric,
                text,
                reader["Uom"] as string,
                "Good"));
        }

        // Повний батч означає, що хвіст діапазону лишився непрочитаним —
        // і покриття за нього писати не можна.
        // ⚠ Порожній батч НЕ вважається обрізаним: із MaxPoints = 0 умова
        // «набрали стелю» була б істинною завжди, і points[^1] упало б на
        // порожньому списку — на діапазоні, у якому просто немає даних.
        var truncated = points.Count > 0 && points.Count >= request.MaxPoints;

        return new CollectionResult(
            points,
            truncated ? [new TimeInterval(points[^1].Timestamp, request.ToUtc)] : [],
            null);
    }

    /// <summary>Відкриває з'єднання під службовим обліковим записом.</summary>
    /// <remarks>
    /// ⛔ Секрет береться <b>за іменем</b> із <c>SecretName</c> і ніколи з
    /// конфігурації (ФВ-6.11, D-11). Рядок з'єднання з паролем не логується:
    /// у повідомленні про відмову лишається лише адреса джерела.
    /// <para>
    /// ⛔ Адаптер не створює жодних артефактів у чужій БД — ні в'юх, ні
    /// таблиць. Вони не їдуть із застосунком і стають джерелом поломок при
    /// переїзді середовища (ER-I-01).
    /// </para>
    /// </remarks>
    private async Task<OdbcConnection> OpenAsync(
        Domain.Entities.External.DataSource source, CancellationToken ct)
    {
        OdbcConnectionStringBuilder builder;

        try
        {
            builder = new OdbcConnectionStringBuilder(source.Endpoint);
        }
        catch (ArgumentException ex)
        {
            // ⛔ Зіпсований рядок з'єднання — це недоступне джерело з погляду
            // збору, а не необроблений виняток десь у надрах. Інакше в
            // журналі прогону лежало б «ArgumentException» без натяку, що
            // правити треба поле Endpoint у конфігурації джерела.
            throw new BusinessRuleException(
                SourceUnavailable,
                $"Рядок з'єднання джерела {source.Code} не читається: {ex.Message}",
                new Dictionary<string, object?> { ["dataSource"] = source.Code });
        }

        if (secrets.Find(source.SecretName) is { } secret)
        {
            builder["PWD"] = secret;
        }

        var connection = new OdbcConnection(builder.ConnectionString);

        // ⛔ Q-222 (аудит): раніше диспозилось лише в `catch (OdbcException)`
        // — будь-яка ІНША помилка `OpenAsync` (скасування токена, таймаут,
        // щось неочікуване) лишала з'єднання відкритим назавжди. `finally`
        // із прапорцем ловить УСІ шляхи, не лише названий тип винятку.
        var opened = false;
        try
        {
            // ⛔ Q-251: відкриття тепер ретраїться (`RetryAsync`) — коротка
            // мережева негода ODBC чи разовий таймаут RTQP більше не
            // провалює весь інтервал з першої ж спроби. Відмова в
            // автентифікації (SQLSTATE 28000) лишається НЕ транзитивною:
            // `IsTransientOdbcFailure` навмисно повертає для неї `false`.
            await RetryAsync(
                async () => { await connection.OpenAsync(ct).ConfigureAwait(false); return true; },
                IsTransientOdbcFailure,
                ct).ConfigureAwait(false);
            opened = true;
            return connection;
        }
        catch (OdbcException ex)
        {
            // ⛔ Відмова в автентифікації відділяється від недоступності
            // (`H-20`): драйвер повідомляє її SQLSTATE 28000 («invalid
            // authorization specification»). Без цього розділення неправильний
            // пароль службового запису виглядав би як тимчасово недоступний
            // RTQP — тобто збір ішов би в наздоганяння і рапортував успіх.
            if (IsAuthenticationFailure(ex))
            {
                throw new SourceAuthenticationException(
                    AuthenticationRefused,
                    $"PI SQL Client не приймає облікові дані джерела {source.Code}: {ex.Message}",
                    new Dictionary<string, object?> { ["dataSource"] = source.Code });
            }

            throw new BusinessRuleException(
                SourceUnavailable,
                $"PI SQL Client не з'єднується з {source.Code}: {ex.Message}",
                new Dictionary<string, object?> { ["dataSource"] = source.Code });
        }
        finally
        {
            if (!opened)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>SQLSTATE відмови в автентифікації — «invalid authorization specification».</summary>
    /// <remarks>
    /// ⚠ Саме SQLSTATE, а не текст повідомлення: текст залежить від драйвера
    /// й мови ОС, і пошук у ньому підрядка розсипався б на першій же машині з
    /// іншою локаллю.
    /// </remarks>
    private const string AuthenticationSqlState = "28000";

    /// <summary>Чи це відмова саме в автентифікації.</summary>
    /// <param name="error">Виняток драйвера.</param>
    private static bool IsAuthenticationFailure(OdbcException error)
    {
        foreach (OdbcError item in error.Errors)
        {
            if (string.Equals(item.SQLState, AuthenticationSqlState, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Чи можна повторити цю відмову ODBC — усе, крім автентифікації.</summary>
    /// <remarks>
    /// ⛔ Q-251: відмова в автентифікації — не транзитивна (`H-20`-подібна
    /// логіка): той самий пароль дасть ту саму відповідь, а повтор лише
    /// подовжить збір на час усіх спроб замість негайного сигналу, що
    /// облікові дані джерела треба поправити.
    /// </remarks>
    private static bool IsTransientOdbcFailure(Exception ex)
        => ex is OdbcException odbc && !IsAuthenticationFailure(odbc);

    /// <summary>
    /// Повторює операцію з експоненційною затримкою — той самий цикл, що й
    /// <c>PiWebApiDataSource.GetAsync</c>, тут узагальнений: точок виклику
    /// дві (відкриття з'єднання й читання), а не одна.
    /// </summary>
    /// <remarks>
    /// ⚠ Публічний і узагальнений навмисно (`Q-251`): живого PI SQL Client
    /// у контурі розробки немає, а <see cref="OdbcException"/> ззовні збірки
    /// не сконструюєш — обидва конструктори в System.Data.Odbc внутрішні.
    /// Це єдиний спосіб перевірити сам цикл повторів напряму — підставним
    /// <paramref name="operation"/> і <paramref name="isTransient"/>, — так
    /// само, як <c>PiWebApiDataSource</c> не потребує живого PI Web API для
    /// перевірки свого ретраю, бо там підміняється транспорт HTTP.
    /// </remarks>
    /// <param name="operation">Операція, яку повторюємо.</param>
    /// <param name="isTransient">Чи варто повторювати саме цей виняток.</param>
    /// <param name="ct">Скасування: під час очікування між спробами теж діє.</param>
    public static async Task<T> RetryAsync<T>(
        Func<Task<T>> operation, Func<Exception, bool> isTransient, CancellationToken ct)
    {
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

    /// <summary>Запит із конфігурації або типовий.</summary>
    private string Query(string key, string fallback)
    {
        var configured = settings?.Find(key);

        return string.IsNullOrWhiteSpace(configured) ? fallback : configured;
    }

    /// <summary>Шаблон елемента; <c>null</c> — елемента немає.</summary>
    private async Task<string?> TemplateAsync(
        OdbcConnection connection, string element, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Query(TemplateQueryKey, DefaultTemplateQuery);
        command.Parameters.Add(new OdbcParameter("element", OdbcType.NVarChar) { Value = element });

        // ⛔ Q-251: цей виклик — частина шляху `ReadAsync` (єдиний викликач
        // нижче), тож повторюється тим самим правилом, а не лишається дірою
        // всередині щойно виправленого методу.
        using var reader = await RetryAsync(
            () => command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct),
            IsTransientOdbcFailure,
            ct).ConfigureAwait(false);

        return await reader.ReadAsync(ct).ConfigureAwait(false) ? reader["Template"] as string : null;
    }

    /// <summary>
    /// Запит значень атрибута через таблицю <c>[Master].[Element].[Value]</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Шаблон і шлях атрибута підставляються <b>текстом</b>, а не
    /// параметрами, і це не недогляд: RTQP вимагає їх літералами в заголовку
    /// таблиці значень — параметр там не парситься. Тому обидва проходять
    /// <see cref="Literal"/>: подвоєння лапки і заборона керівних символів.
    /// Часові межі й ім'я елемента лишаються звичайними параметрами.
    /// </remarks>
    private string ValueQuery(string template, string attribute)
        => Query(ValueQueryKey, DefaultValueQuery)
            .Replace("{template}", Literal(template), StringComparison.Ordinal)
            .Replace("{attribute}", Literal(attribute), StringComparison.Ordinal);

    /// <summary>Типовий запит значень; перевизначається через конфігурацію.</summary>
    public const string DefaultValueQuery = """
        SELECT v.[Ts] AS Ts, v.[Val] AS Val, v.[Uom] AS Uom
        FROM [Master].[Element].[Element] e
        INNER JOIN [Master].[Element].[Value]
        <
            N'{template}',
            {
                N'{attribute}',
                N'Ts',
                N'Val',
                N'Uom',
                NULL,
                NULL
            }
        > v ON e.ID = v.ElementID
        WHERE e.Name = ? AND v.[Ts] >= ? AND v.[Ts] < ?
        ORDER BY v.[Ts]
        """;

    /// <summary>Літерал для заголовка таблиці значень.</summary>
    private static string Literal(string value)
    {
        foreach (var symbol in value)
        {
            if (char.IsControl(symbol))
            {
                throw new BusinessRuleException(
                    SourceUnavailable,
                    "Ім'я об'єкта джерела містить керівний символ: запит не будується.");
            }
        }

        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    /// <summary>Значення точки: число або текст, ніколи обидва.</summary>
    /// <remarks>
    /// ⛔ <c>double</c> із джерела переводиться в <c>decimal</c> тут, на межі,
    /// і далі його немає (D-30). Звітні числа звіряються до копійки, а
    /// подвійна точність дає розбіжність, якої ніхто не може пояснити.
    /// </remarks>
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
            bool flag => (null, flag ? "true" : "false"),
            string text => (null, text),

            // Незнайомий тип не вгадується: текстове подання видно у звіті про
            // збір, підставлений нуль — ні.
            _ => (null, raw.ToString()),
        };

    /// <summary>Розбирає шлях на елемент і атрибут.</summary>
    private static (string Element, string Attribute) Split(string sourcePath)
    {
        var separator = sourcePath.LastIndexOf('|');

        return separator > 0
            ? (sourcePath[..separator], sourcePath[(separator + 1)..])
            : (sourcePath, sourcePath);
    }

    private static BusinessRuleException Unavailable(string message) => new(SourceUnavailable, message);
}
