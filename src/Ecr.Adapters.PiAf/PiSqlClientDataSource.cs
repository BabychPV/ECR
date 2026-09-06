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

    private const string SourceUnavailable = "ECR-INT-0503";

    /// <summary>
    /// Каталог елементів і атрибутів AF.
    /// </summary>
    /// <remarks>
    /// ⚠ Імена об'єктів звірені з експортом чинного рішення
    /// (<c>ECR_01_Air_PISqlClientExportedObjects.sql</c>): саме
    /// <c>[Master].[Element].[Element]</c> і <c>[Master].[Element].[Attribute]</c>,
    /// а не вигадані за аналогією.
    /// </remarks>
    public const string DefaultCatalogQuery = """
        SELECT e.Name AS ElementName, a.Name AS AttributeName, a.UOM AS Uom, a.Type AS DataType
        FROM [Master].[Element].[Attribute] a
        INNER JOIN [Master].[Element].[Element] e ON e.ID = a.ElementID
        ORDER BY e.Name, a.Name
        """;

    /// <summary>Ключ конфігурації, яким запит каталогу можна перевизначити.</summary>
    /// <remarks>
    /// ⚠ Запити винесені в **налаштування** (`P-11`). Імена об'єктів звірені з
    /// експортом чинного рішення, але точні колонки
    /// <c>[Master].[Element].[Attribute]</c> перевірити ніде: живого PI SQL
    /// Client у контурі розробки немає. Помилка в назві колонки — це не
    /// архітектурна проблема, а один рядок; вимагати заради нього перезбирання
    /// і релізу означало б, що зупинений збір чекає доби замість хвилини.
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

        using var reader = await command
            .ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct)
            .ConfigureAwait(false);

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

        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            return connection;
        }
        catch (OdbcException ex)
        {
            await connection.DisposeAsync().ConfigureAwait(false);

            // ⛔ Відмова в автентифікації відділяється від недоступності
            // (`H-20`): драйвер повідомляє її SQLSTATE 28000 («invalid
            // authorization specification»). Без цього розділення неправильний
            // пароль службового запису виглядав би як тимчасово недоступний
            // RTQP — тобто збір ішов би в наздоганяння і рапортував успіх.
            if (IsAuthenticationFailure(ex))
            {
                throw new SourceAuthenticationException(
                    SourceUnavailable,
                    $"PI SQL Client не приймає облікові дані джерела {source.Code}: {ex.Message}",
                    new Dictionary<string, object?> { ["dataSource"] = source.Code });
            }

            throw new BusinessRuleException(
                SourceUnavailable,
                $"PI SQL Client не з'єднується з {source.Code}: {ex.Message}",
                new Dictionary<string, object?> { ["dataSource"] = source.Code });
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

        using var reader = await command
            .ExecuteReaderAsync(CommandBehavior.SingleRow, ct)
            .ConfigureAwait(false);

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
