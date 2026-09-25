using System.Data;
using Ecr.Application.Errors;
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

        while (points.Count < request.MaxPoints && await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (Raw(reader, columns, "Ts") is not DateTime timestamp)
            {
                continue;
            }

            var (numeric, text) = Value(Raw(reader, columns, "Val"));

            points.Add(new SourceDataPoint(
                request.SourcePath,
                DateTime.SpecifyKind(timestamp, DateTimeKind.Utc),
                numeric,
                text,
                Text(reader, columns, "Uom"),
                Text(reader, columns, "Quality") ?? "Good"));
        }

        // Повний батч означає, що хвіст діапазону лишився непрочитаним — і
        // покриття за нього писати не можна (ER-I-03).
        // ⚠ Порожній батч обрізаним НЕ вважається: із MaxPoints = 0 умова була
        // б істинною завжди, а points[^1] упало б на порожньому списку.
        var truncated = points.Count > 0 && points.Count >= request.MaxPoints;

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
