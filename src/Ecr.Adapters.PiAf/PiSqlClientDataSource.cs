using System.Data;
using System.Data.Common;
using System.Data.Odbc;
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Sources;
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
        SELECT e.Name AS ElementName, a.Name AS AttributeName, a.UnitOfMeasure AS Uom, a.ValueType AS DataType, e.ID AS ElementId
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
                     ?? throw Unavailable($"Джерело {dataSourceId} не існує або вимкнене.", dataSourceId);

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

        // ⚠ `ElementId` (GUID елемента, S1) — необов'язкова: запит каталогу
        // перевизначається налаштуванням, і текст без неї мусить працювати як досі.
        var hasElementId = Enumerable.Range(0, reader.FieldCount)
            .Any(i => string.Equals(reader.GetName(i), "ElementId", StringComparison.OrdinalIgnoreCase));

        while (result.Count < MaxCatalogRows && await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var element = reader["ElementName"] as string ?? string.Empty;
            var attribute = reader["AttributeName"] as string ?? string.Empty;

            // Порядок звернень — порядок колонок: під SequentialAccess назад не можна.
            var uom = reader["Uom"] as string;
            var dataType = reader["DataType"] as string;
            var elementId = hasElementId ? reader["ElementId"] : null;

            result.Add(new SourceEntityDescriptor(
                $"{element}|{attribute}",
                attribute,
                element,

                // ⚠ Одиниця йде в каталог обов'язково: побачити її треба вже
                // при налаштуванні, а не через місяць на звірці (ФВ-16.9).
                uom,
                dataType,
                elementId is null or DBNull ? null : Convert.ToString(elementId, CultureInfo.InvariantCulture)));
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<CollectionResult> ReadAsync(CollectionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // ⛔ Тип запиту перевіряється ДО джерела: без налаштованого тексту
        // інтерпольований запит не підміняється сирим (V-3, D-172).
        var queryText = request.Kind switch
        {
            SourceQueryKind.Raw => null,
            SourceQueryKind.Interpolated => ConfiguredOrRefuse(InterpolatedQueryKey, request.Kind.ToString()),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "Невідомий тип запиту."),
        };

        if (request.Kind == SourceQueryKind.Interpolated && !(request.Step > TimeSpan.Zero))
        {
            throw new ArgumentException("Інтерпольований запит потребує додатного кроку.", nameof(request));
        }

        var source = await store.FindDataSourceAsync(request.DataSourceId, ct).ConfigureAwait(false)
                     ?? throw Unavailable(
                         $"Джерело {request.DataSourceId} не існує або вимкнене.", request.DataSourceId);

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
        command.CommandText = queryText is null
            ? ValueQuery(template, attribute)
            : Fill(queryText, template, attribute);
        command.Parameters.Add(new OdbcParameter("element", OdbcType.NVarChar) { Value = element });
        command.Parameters.Add(new OdbcParameter("from", OdbcType.DateTime) { Value = request.FromUtc });
        command.Parameters.Add(new OdbcParameter("to", OdbcType.DateTime) { Value = request.ToUtc });

        if (request.Kind == SourceQueryKind.Interpolated)
        {
            // Крок — секундами: так його отримує текст запиту з налаштування
            // (наш контракт, як Ts/Val/Uom; форму RTQP задає сам текст).
            command.Parameters.Add(new OdbcParameter("step", OdbcType.Double)
            {
                Value = request.Step!.Value.TotalSeconds,
            });
        }

        // ⛔ Q-251: те саме читання, що йшло без жодного повтору — разовий
        // таймаут RTQP під навантаженням одразу провалював увесь інтервал.
        using var reader = await RetryAsync(
            () => command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct),
            IsTransientOdbcFailure,
            ct).ConfigureAwait(false);

        var points = await ReadPointsAsync(reader, request.SourcePath, request.MaxPoints, ct)
            .ConfigureAwait(false);

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

    /// <summary>
    /// Вікно: summary на сервері, якщо налаштовано <see cref="SummaryQueryKey"/>;
    /// інакше — типова локальна згортка сирих точок (<see cref="WindowFold"/>).
    /// </summary>
    /// <remarks>
    /// ⛔ Типового тексту summary-запиту немає і не буде (V-3, <c>D-172</c>): імена
    /// табличних функцій RTQP для summary в репозиторії не підтверджені, а
    /// вигаданий дефолт виглядав би робочим налаштуванням. Текст звіряється з
    /// AVEVA PI SQL DAS (RTQP Engine) Reference процедурою <c>Q-197</c>.
    /// <para>
    /// Контракт тексту: заповнювачі <c>{template}</c>, <c>{attribute}</c>,
    /// <c>{summary}</c> (<c>Total</c>/<c>Average</c>/<c>Minimum</c>/<c>Maximum</c>/<c>Count</c>)
    /// — літералами; параметри <c>?</c> — елемент, від, до. Один рядок: <c>Val</c>,
    /// необов'язкові <c>Uom</c>, <c>PercentGood</c>, <c>PointCount</c>.
    /// ⚠ <c>Val</c> для <c>Total</c> — в «одиниця × секунда», як у локальної згортки.
    /// PI Total за замовчуванням рахує «за добу»; поправка — справа тексту запиту,
    /// інакше число завищене в 86 400 разів і правдоподібне (HQ-16).
    /// </para>
    /// </remarks>
    /// <param name="request">Вікно.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<WindowResult> ReadWindowAsync(WindowRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var configured = settings?.Find(SummaryQueryKey);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return await WindowFold.FromRawAsync(this, request, ct).ConfigureAwait(false);
        }

        if (request.ToUtc <= request.FromUtc)
        {
            throw new ArgumentException("Вікно порожнє або перевернуте.", nameof(request));
        }

        var source = await store.FindDataSourceAsync(request.DataSourceId, ct).ConfigureAwait(false)
                     ?? throw Unavailable(
                         $"Джерело {request.DataSourceId} не існує або вимкнене.", request.DataSourceId);

        var (element, attribute) = Split(request.SourcePath);

        using var connection = await OpenAsync(source, ct).ConfigureAwait(false);

        var template = await TemplateAsync(connection, element, ct).ConfigureAwait(false);
        if (template is null)
        {
            return new WindowResult(
                null, null, 0, null, WindowComputedBy.Server,
                [new TimeInterval(request.FromUtc, request.ToUtc)], SourceUnavailable);
        }

        using var command = connection.CreateCommand();
        command.CommandText = SummaryQuery(configured, template, attribute, request.Summary);
        command.Parameters.Add(new OdbcParameter("element", OdbcType.NVarChar) { Value = element });
        command.Parameters.Add(new OdbcParameter("from", OdbcType.DateTime) { Value = request.FromUtc });
        command.Parameters.Add(new OdbcParameter("to", OdbcType.DateTime) { Value = request.ToUtc });

        using var reader = await RetryAsync(
            () => command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct),
            IsTransientOdbcFailure,
            ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return new WindowResult(null, null, 0, null, WindowComputedBy.Server, [], null);
        }

        var row = Row(reader);
        var (value, _) = Value(Column(row, "Val"));

        return new WindowResult(
            value,
            Column(row, "Uom") as string,
            Value(Column(row, "PointCount")).Numeric is { } count ? (int)count : 0,
            Value(Column(row, "PercentGood")).Numeric,
            WindowComputedBy.Server,
            [],
            null);
    }

    /// <summary>Ключ інтерпольованого запиту (HSE301 §4.3). Типового тексту немає.</summary>
    /// <remarks>
    /// Заповнювачі <c>{template}</c>, <c>{attribute}</c> — літералами, як у
    /// <see cref="ValueQueryKey"/>; параметри <c>?</c> — елемент, від, до, крок
    /// (секунди). Результат — ті самі колонки, що в сирого: <c>Ts</c>, <c>Val</c>,
    /// <c>Uom</c>, необов'язкова <c>Quality</c>.
    /// </remarks>
    public const string InterpolatedQueryKey = "PiSqlClient:InterpolatedQuery";

    /// <summary>Ключ summary-запиту вікна (HSE301 §4.3). Типового тексту немає.</summary>
    public const string SummaryQueryKey = "PiSqlClient:SummaryQuery";

    /// <summary>Тип запиту не налаштовано — той самий код, що й інші відмови конфігурації збору.</summary>
    private const string QueryNotConfigured = "ECR-INT-0422";

    /// <summary>Текст запиту з налаштування або відмова <c>ECR-INT-0422</c>.</summary>
    private string ConfiguredOrRefuse(string key, string kind)
    {
        var configured = settings?.Find(key);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        throw new BusinessRuleException(
            QueryNotConfigured,
            $"Запит типу {kind} для PI SQL Client не налаштовано: немає ключа {key}.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-INT-0422.queryKindNotConfigured",
                ["queryKind"] = kind,
                ["configKey"] = key,
            });
    }

    /// <summary>Ключ запиту поточного значення атрибута (S1, FEATURE-REGISTRY-SYNC). Типового тексту немає.</summary>
    /// <remarks>
    /// ⛔ Типового тексту немає з тієї ж причини, що в <see cref="InterpolatedQueryKey"/>
    /// (V-3): таблична функція RTQP для «snapshot» атрибута в репозиторії не
    /// підтверджена, а вигаданий дефолт виглядав би робочим налаштуванням.
    /// <para>
    /// Контракт тексту: заповнювачі <c>{template}</c>, <c>{attribute}</c> —
    /// літералами; один параметр <c>?</c> — ім'я елемента. Перший рядок
    /// результату: <c>Ts</c>, <c>Val</c>, необов'язкові <c>Uom</c>, <c>Quality</c>
    /// (як у <see cref="ReadPointsAsync"/>). Порожній результат — відмова шляху
    /// <c>ECR-INT-0503</c> <c>.currentValueUnreadable</c>, не «нуль».
    /// </para>
    /// </remarks>
    public const string CurrentValueQueryKey = "PiSqlClient:CurrentValueQuery";

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ Без <see cref="CurrentValueQueryKey"/> — відмова <c>ECR-INT-0422</c>
    /// <c>.queryKindNotConfigured</c> ДО з'єднання (як інтерпольований запит, F4).
    /// Елемента немає — відмова шляху <c>ECR-INT-0404</c>, решта читається далі.
    /// </remarks>
    public async Task<CurrentValuesResult> ReadCurrentAsync(
        int dataSourceId, IReadOnlyCollection<string> paths, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var queryText = ConfiguredOrRefuse(CurrentValueQueryKey, "CurrentValue");

        var source = await store.FindDataSourceAsync(dataSourceId, ct).ConfigureAwait(false)
                     ?? throw Unavailable($"Джерело {dataSourceId} не існує або вимкнене.", dataSourceId);

        using var connection = await OpenAsync(source, ct).ConfigureAwait(false);

        var values = new List<SourceDataPoint>();
        var failures = new List<CurrentValueFailure>();

        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal))
        {
            var (element, attribute) = Split(path);

            var template = await TemplateAsync(connection, element, ct).ConfigureAwait(false);
            if (template is null)
            {
                failures.Add(new CurrentValueFailure(path, "ECR-INT-0404", "err.ECR-INT-0404.sourcePathNotFound"));
                continue;
            }

            using var command = connection.CreateCommand();
            command.CommandText = Fill(queryText, template, attribute);
            command.Parameters.Add(new OdbcParameter("element", OdbcType.NVarChar) { Value = element });

            using var reader = await RetryAsync(
                () => command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct),
                IsTransientOdbcFailure,
                ct).ConfigureAwait(false);

            var point = (await ReadPointsAsync(reader, path, 1, ct).ConfigureAwait(false)).FirstOrDefault();
            if (point is null)
            {
                failures.Add(new CurrentValueFailure(path, SourceUnavailable, "err.ECR-INT-0503.currentValueUnreadable"));
            }
            else
            {
                values.Add(point);
            }
        }

        return new CurrentValuesResult(values, failures);
    }

    /// <summary>Текст summary-запиту з підставленими літералами.</summary>
    /// <param name="configured">Текст із <see cref="SummaryQueryKey"/>.</param>
    /// <param name="template">Шаблон елемента.</param>
    /// <param name="attribute">Атрибут.</param>
    /// <param name="summary">Спосіб згортки: ім'я PI summary type.</param>
    /// <remarks>Публічний для тестів: живого RTQP у контурі розробки немає.</remarks>
    public static string SummaryQuery(
        string configured, string template, string attribute, SourceSummaryKind summary)
    {
        ArgumentNullException.ThrowIfNull(configured);

        if (!Enum.IsDefined(summary))
        {
            throw new ArgumentOutOfRangeException(nameof(summary), summary, "Невідомий спосіб згортки вікна.");
        }

        return Fill(configured, template, attribute)
            .Replace("{summary}", summary.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Підставляє <c>{template}</c> і <c>{attribute}</c> літералами.</summary>
    private static string Fill(string query, string template, string attribute)
        => query
            .Replace("{template}", Literal(template), StringComparison.Ordinal)
            .Replace("{attribute}", Literal(attribute), StringComparison.Ordinal);

    /// <summary>
    /// Точки з результату запиту: <c>Ts</c>, <c>Val</c>, необов'язкові <c>Uom</c> і <c>Quality</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Якість (Д-4, §4.6) — з колонки <c>Quality</c>, якщо запит її повертає;
    /// немає колонки чи значення — <c>Good</c>, як у <c>SqlDataSource</c>.
    /// Типовий <see cref="DefaultValueQuery"/> колонки якості НЕ повертає: два
    /// <c>NULL</c> у заголовку таблиці значень, імовірно, — слоти стану/помилки,
    /// але їхня семантика в репозиторії не підтверджена (як <c>Q-197</c>).
    /// Увімкнути — текстом <see cref="ValueQueryKey"/> після звірки з AVEVA Reference.
    /// Системний стан PI текстом (<c>I/O Timeout</c>) і так лягає в
    /// <see cref="SourceDataPoint.ValueString"/> і згорткою не береться.
    /// <para>
    /// Рядок читається цілим (<c>GetValues</c>): під <c>SequentialAccess</c>
    /// колонки не можна читати не по порядку, а порядок належить тексту з
    /// налаштування. Публічний для тестів: <c>OdbcDataReader</c> ззовні не зробиш.
    /// </para>
    /// </remarks>
    /// <param name="reader">Відкритий результат запиту.</param>
    /// <param name="sourcePath">Шлях атрибута для кожної точки.</param>
    /// <param name="maxPoints">Стеля батча.</param>
    /// <param name="ct">Скасування.</param>
    public static async Task<List<SourceDataPoint>> ReadPointsAsync(
        DbDataReader reader, string sourcePath, int maxPoints, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var points = new List<SourceDataPoint>();

        while (points.Count < maxPoints && await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var row = Row(reader);
            if (Column(row, "Ts") is not DateTime timestamp)
            {
                continue;
            }

            var (numeric, text) = Value(Column(row, "Val"));
            var quality = Column(row, "Quality") as string;

            points.Add(new SourceDataPoint(
                sourcePath,
                DateTime.SpecifyKind(timestamp, DateTimeKind.Utc),
                numeric,
                text,
                Column(row, "Uom") as string,
                string.IsNullOrWhiteSpace(quality) ? WindowFold.GoodQuality : quality));
        }

        return points;
    }

    /// <summary>Поточний рядок: ім'я колонки → значення.</summary>
    private static Dictionary<string, object?> Row(DbDataReader reader)
    {
        var values = new object[reader.FieldCount];
        reader.GetValues(values);

        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < values.Length; i++)
        {
            row[reader.GetName(i)] = values[i] is DBNull ? null : values[i];
        }

        return row;
    }

    /// <summary>Значення колонки; немає колонки — <c>null</c>.</summary>
    private static object? Column(Dictionary<string, object?> row, string name)
        => row.TryGetValue(name, out var value) ? value : null;

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
                new Dictionary<string, object?>
                {
                    // Той самий ключ, що SqlDataSource.cs: той самий факт («не читається»).
                    ["messageKey"] = "err.ECR-INT-0503.connectionStringBroken",
                    ["dataSource"] = source.Code,
                });
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
                    new Dictionary<string, object?>
                    {
                        // Той самий ключ, що SqlDataSource.cs: той самий факт
                        // («службові облікові дані відмовлено»).
                        ["messageKey"] = "err.ECR-INT-0502.credentialsRefused",
                        ["dataSource"] = source.Code,
                    });
            }

            throw new BusinessRuleException(
                SourceUnavailable,
                $"PI SQL Client не з'єднується з {source.Code}: {ex.Message}",
                new Dictionary<string, object?>
                {
                    // Той самий ключ, що SqlDataSource.cs: той самий факт («не з'єднується»).
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
        => Fill(Query(ValueQueryKey, DefaultValueQuery), template, attribute);

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
                    "Ім'я об'єкта джерела містить керівний символ: запит не будується.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-INT-0503.controlCharacterInName",
                    });
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
    internal static (decimal? Numeric, string? Text) Value(object? raw)
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

    private static BusinessRuleException Unavailable(string message, int dataSourceId)
        => new(
            SourceUnavailable,
            message,
            new Dictionary<string, object?>
            {
                // Той самий ключ, що SqlDataSource.cs/CollectionRunner.cs/PiAfCatalogReader.cs.
                ["messageKey"] = "err.ECR-INT-0503.sourceMissing",
                ["dataSourceId"] = dataSourceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
}
