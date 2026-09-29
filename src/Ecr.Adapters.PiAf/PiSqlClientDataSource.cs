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
        command.CommandText = Query(source.Code, CatalogQueryKey, DefaultCatalogQuery);

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

        if (request.Kind is not (SourceQueryKind.Raw or SourceQueryKind.Interpolated))
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "Невідомий тип запиту.");
        }

        if (request.Kind == SourceQueryKind.Interpolated && !(request.Step > TimeSpan.Zero))
        {
            throw new ArgumentException("Інтерпольований запит потребує додатного кроку.", nameof(request));
        }

        var source = await store.FindDataSourceAsync(request.DataSourceId, ct).ConfigureAwait(false)
                     ?? throw Unavailable(
                         $"Джерело {request.DataSourceId} не існує або вимкнене.", request.DataSourceId);

        // ⛔ Тип запиту перевіряється ДО з'єднання: без налаштованого тексту
        // інтерпольований запит не підміняється сирим (V-3, D-172). Запис джерела
        // читається раніше — ключ запиту буває власним у кожного джерела (<see cref="ScopedKey"/>).
        var queryText = request.Kind == SourceQueryKind.Interpolated
            ? ConfiguredOrRefuse(source.Code, InterpolatedQueryKey, request.Kind.ToString())
            : null;

        var (element, attribute) = Split(request.SourcePath);

        using var connection = await OpenAsync(source, ct).ConfigureAwait(false);

        var template = await TemplateAsync(connection, source.Code, element, ct).ConfigureAwait(false);

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
            ? ValueQuery(source.Code, template, attribute)
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

        var points = await ReadSourcePointsAsync(reader, source.Code, request.SourcePath, request.MaxPoints, ct)
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

        // Запис джерела — до вибору шляху: summary-запит теж буває власним у джерела.
        var source = await store.FindDataSourceAsync(request.DataSourceId, ct).ConfigureAwait(false)
                     ?? throw Unavailable(
                         $"Джерело {request.DataSourceId} не існує або вимкнене.", request.DataSourceId);

        var configured = ConfiguredQuery(settings, source.Code, SummaryQueryKey);
        if (configured is null)
        {
            return await WindowFold.FromRawAsync(this, request, ct).ConfigureAwait(false);
        }

        if (request.ToUtc <= request.FromUtc)
        {
            throw new ArgumentException("Вікно порожнє або перевернуте.", nameof(request));
        }

        var (element, attribute) = Split(request.SourcePath);

        using var connection = await OpenAsync(source, ct).ConfigureAwait(false);

        var template = await TemplateAsync(connection, source.Code, element, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Текст запиту з налаштування (спершу ключ джерела, далі спільний — <see cref="ConfiguredQuery"/>)
    /// або відмова <c>ECR-INT-0422</c> <c>.queryKindNotConfigured</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Рішення людини 2026-09-29 («ми можемо звертатися до різних баз на одному AF-сервері»):
    /// ключ джерела — для КОЖНОГО запиту, не лише подій. <c>configKey</c> тут — обидва ключі
    /// через « / »: наявний текст сіду <c>.queryKindNotConfigured</c> («set {configKey}») тоді
    /// радить те саме, що перевірялось, без зміни тексту ключа.
    /// </remarks>
    /// <param name="dataSourceCode">Код джерела.</param>
    /// <param name="key">Спільний ключ.</param>
    /// <param name="kind">Тип запиту — для відмови.</param>
    private string ConfiguredOrRefuse(string dataSourceCode, string key, string kind)
        => ConfiguredQuery(settings, dataSourceCode, key)
           ?? throw NotConfigured(
               dataSourceCode, key, kind, "err.ECR-INT-0422.queryKindNotConfigured",
               $"{ScopedKey(dataSourceCode, key)} / {key}");

    /// <summary>
    /// Відмова «запит не налаштовано» — одна форма параметрів для всіх запитів PI SQL Client:
    /// <c>queryKind</c>, <c>dataSource</c>, <c>sourceConfigKey</c>, <c>sharedConfigKey</c>, <c>configKey</c>.
    /// </summary>
    /// <remarks>
    /// <c>configKey</c> — заповнювач тексту сіду свого <c>messageKey</c>: для
    /// <c>.queryKindNotConfigured</c> («set {configKey}») — обидва ключі через « / »; для
    /// <c>.eventQueryNotConfigured</c> («set {sourceConfigKey} (or the shared {configKey})») — спільний.
    /// </remarks>
    private static BusinessRuleException NotConfigured(
        string dataSourceCode, string key, string kind, string messageKey, string configKey)
    {
        var own = ScopedKey(dataSourceCode, key);

        return new BusinessRuleException(
            IExternalDataSource.QueryRefusedCode,
            $"Запит типу {kind} для джерела {dataSourceCode} не налаштовано: немає ні {own}, ні {key}.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = messageKey,
                ["queryKind"] = kind,
                ["dataSource"] = dataSourceCode,
                ["sourceConfigKey"] = own,
                ["sharedConfigKey"] = key,
                ["configKey"] = configKey,
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

        var source = await store.FindDataSourceAsync(dataSourceId, ct).ConfigureAwait(false)
                     ?? throw Unavailable($"Джерело {dataSourceId} не існує або вимкнене.", dataSourceId);

        var queryText = ConfiguredOrRefuse(source.Code, CurrentValueQueryKey, "CurrentValue");

        using var connection = await OpenAsync(source, ct).ConfigureAwait(false);

        var values = new List<SourceDataPoint>();
        var failures = new List<CurrentValueFailure>();

        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal))
        {
            var (element, attribute) = Split(path);

            var template = await TemplateAsync(connection, source.Code, element, ct).ConfigureAwait(false);
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

            SourceDataPoint? point;
            try
            {
                point = (await ReadSourcePointsAsync(reader, source.Code, path, 1, ct).ConfigureAwait(false))
                    .FirstOrDefault();
            }
            catch (BusinessRuleException refused) when (IsTimestampRefusal(refused))
            {
                // Поточне значення з нечитабельною міткою — відмова ШЛЯХУ, як і
                // до F4e (тоді рядок мовчки пропускався і шлях так само ставав
                // `.currentValueUnreadable`), а не відмова всього знімка.
                point = null;
            }

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

    /// <summary>Ключ запиту подій (HSE301 §4.7.2). Типового тексту немає.</summary>
    /// <remarks>
    /// ⛔ Типового тексту немає (V-3): імена RTQP-об'єктів подій факела в
    /// репозиторії не підтверджені. Без ключа — <c>ECR-INT-0422</c>
    /// <c>.eventQueryNotConfigured</c>, не порожній список.
    /// <para>
    /// Контракт тексту: заповнювач <c>{template}</c> — літералом
    /// (<see cref="Literal"/>); параметри <c>?</c> — від і до, UTC (події, що
    /// перетинають <c>[від, до)</c>, — справа тексту). Результат — довга форма,
    /// рядок на атрибут: <c>EventId</c>, <c>StartTime</c> (обов'язкові),
    /// <c>EventName</c>, <c>Template</c>, <c>EndTime</c>, <c>Modified</c>,
    /// <c>PrimaryElement</c>, <c>ParentId</c>, <c>AttrScope</c> (<c>E</c>/<c>P</c>),
    /// <c>AttrName</c>, <c>AttrValue</c>, <c>AttrUom</c>. Подія без атрибутів —
    /// рядок з <c>AttrName = NULL</c>. Рядки однієї події — поспіль
    /// (<c>ORDER BY StartTime, EventId</c>): стеля рахує події (<see cref="SourceEventFolder"/>).
    /// </para>
    /// </remarks>
    public const string EventQueryKey = "PiSqlClient:EventQuery";

    /// <summary>Ключ запиту каталогу шаблонів подій. Типового тексту немає.</summary>
    /// <remarks>
    /// Результат: <c>Template</c>, необов'язкові <c>AttrScope</c>, <c>AttrName</c>,
    /// <c>AttrUom</c>, <c>AttrType</c>; шаблон без атрибутів — рядок з <c>AttrName = NULL</c>.
    /// </remarks>
    public const string EventTemplateQueryKey = "PiSqlClient:EventTemplateQuery";

    /// <inheritdoc />
    /// <remarks>
    /// Шаблону елемента не потребує (на відміну від <see cref="ReadAsync"/>):
    /// шаблон подій приходить у запиті. Без <see cref="EventQueryKey"/> — власного
    /// ключа джерела чи спільного — відмова <c>ECR-INT-0422</c> до з'єднання.
    /// </remarks>
    public async Task<SourceEventResult> ReadEventsAsync(SourceEventQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.Template, nameof(query));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(query.MaxEvents, nameof(query));

        var from = AsUtc(query.FromUtc);
        var to = AsUtc(query.ToUtc);
        if (to <= from)
        {
            throw new ArgumentException("Вікно подій порожнє або перевернуте.", nameof(query));
        }

        var source = await store.FindDataSourceAsync(query.DataSourceId, ct).ConfigureAwait(false)
                     ?? throw Unavailable($"Джерело {query.DataSourceId} не існує або вимкнене.", query.DataSourceId);

        var text = EventQueryOrRefuse(EventQueryKey, IExternalDataSource.EventQueryKind, source.Code);

        using var connection = await OpenAsync(source, ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = EventQuery(text, query.Template);
        command.Parameters.Add(new OdbcParameter("from", OdbcType.DateTime) { Value = from });
        command.Parameters.Add(new OdbcParameter("to", OdbcType.DateTime) { Value = to });

        using var reader = await RetryAsync(
            () => command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct),
            IsTransientOdbcFailure,
            ct).ConfigureAwait(false);

        return await ReadEventRowsAsync(reader, source.Code, query.Template, query.MaxEvents, query.Attributes, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SourceEventTemplate>> DiscoverEventTemplatesAsync(
        int dataSourceId, CancellationToken ct)
    {
        var source = await store.FindDataSourceAsync(dataSourceId, ct).ConfigureAwait(false)
                     ?? throw Unavailable($"Джерело {dataSourceId} не існує або вимкнене.", dataSourceId);

        var text = EventQueryOrRefuse(EventTemplateQueryKey, IExternalDataSource.EventTemplateQueryKind, source.Code);

        using var connection = await OpenAsync(source, ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = text;

        using var reader = await RetryAsync(
            () => command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct),
            IsTransientOdbcFailure,
            ct).ConfigureAwait(false);

        return await ReadEventTemplatesAsync(reader, ct).ConfigureAwait(false);
    }

    /// <summary>Події з результату запиту подій (контракт колонок — <see cref="EventQueryKey"/>).</summary>
    /// <remarks>
    /// Час — правилом <see cref="EventTime"/>: <c>StartTime</c> обов'язковий, <c>EndTime</c>
    /// і <c>Modified</c> можуть бути <c>NULL</c>; нечитабельний тип — відмова
    /// <c>.eventTimestampUnreadable</c>, рядок без <c>EventId</c> — <c>.eventIdMissing</c>
    /// (обидві <c>ECR-INT-0422</c>). Публічний для тестів: <c>OdbcDataReader</c> ззовні не зробиш.
    /// </remarks>
    /// <param name="reader">Відкритий результат запиту.</param>
    /// <param name="dataSource">Код джерела — для тексту відмови.</param>
    /// <param name="template">Шаблон запиту.</param>
    /// <param name="maxEvents">Стеля подій.</param>
    /// <param name="attributes">Які атрибути лишити; порожньо — усі.</param>
    /// <param name="ct">Скасування.</param>
    public static async Task<SourceEventResult> ReadEventRowsAsync(
        DbDataReader reader,
        string dataSource,
        string template,
        int maxEvents,
        IReadOnlyCollection<SourceEventAttributeRef>? attributes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var folder = new SourceEventFolder(template, maxEvents, attributes);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var row = Row(reader);
            var id = Text(Column(row, "EventId"));
            if (string.IsNullOrWhiteSpace(id))
            {
                // Без ідентифікатора подію не зв'язати з рядком документа ніколи:
                // це дефект тексту запиту, а не стан даних, — відмова вікна (422), не 500.
                throw new BusinessRuleException(
                    IExternalDataSource.QueryRefusedCode,
                    $"Запит подій джерела {dataSource} повернув рядок без EventId: ключа синхронізації немає.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-INT-0422.eventIdMissing",
                        ["dataSource"] = dataSource,
                    });
            }

            var added = folder.Add(new SourceEventRow(
                id,
                Text(Column(row, "EventName")),
                Text(Column(row, "Template")),
                (DateTime)EventTime(Column(row, "StartTime"), dataSource, id, "StartTime", required: true)!,
                EventTime(Column(row, "EndTime"), dataSource, id, "EndTime", required: false),
                EventTime(Column(row, "Modified"), dataSource, id, "Modified", required: false),
                Text(Column(row, "PrimaryElement")),
                Text(Column(row, "ParentId")),
                Text(Column(row, "AttrScope")),
                Text(Column(row, "AttrName")),
                Column(row, "AttrValue"),
                Text(Column(row, "AttrUom"))));

            if (!added)
            {
                break;
            }
        }

        return folder.ToResult();
    }

    /// <summary>Каталог шаблонів подій з результату запиту (<see cref="EventTemplateQueryKey"/>).</summary>
    /// <param name="reader">Відкритий результат запиту.</param>
    /// <param name="ct">Скасування.</param>
    public static async Task<IReadOnlyList<SourceEventTemplate>> ReadEventTemplatesAsync(
        DbDataReader reader, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var order = new List<string>();
        var byName = new Dictionary<string, List<SourceEventAttributeDescriptor>>(StringComparer.Ordinal);
        var rows = 0;

        while (rows < MaxCatalogRows && await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows++;
            var row = Row(reader);
            var name = Text(Column(row, "Template"));
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (!byName.TryGetValue(name, out var attributes))
            {
                attributes = [];
                byName.Add(name, attributes);
                order.Add(name);
            }

            var attribute = Text(Column(row, "AttrName"));
            if (!string.IsNullOrWhiteSpace(attribute))
            {
                attributes.Add(new SourceEventAttributeDescriptor(
                    attribute,
                    SourceEventFolder.Scope(Text(Column(row, "AttrScope"))),
                    Text(Column(row, "AttrUom")),
                    Text(Column(row, "AttrType"))));
            }
        }

        return [.. order.Select(name => new SourceEventTemplate(name, byName[name]))];
    }

    /// <summary>Час події в UTC — тим самим правилом, що <see cref="Utc"/>, але з власним ключем відмови.</summary>
    /// <remarks>
    /// ⚠ Ключ <c>.eventTimestampUnreadable</c>, а не точковий <c>.timestampUnreadable</c>:
    /// текст того говорить про <c>Ts</c> і запит значень, а людина тут читає про подію.
    /// <see cref="DateTime"/> без поясу вважається UTC — те саме явне припущення.
    /// </remarks>
    /// <param name="raw">Значення колонки.</param>
    /// <param name="dataSource">Код джерела.</param>
    /// <param name="eventId">Ідентифікатор події.</param>
    /// <param name="field">Колонка: <c>StartTime</c>, <c>EndTime</c>, <c>Modified</c>.</param>
    /// <param name="required"><c>NULL</c> — відмова, а не <c>null</c>.</param>
    private static DateTime? EventTime(object? raw, string dataSource, string eventId, string field, bool required)
        => raw switch
        {
            DateTimeOffset zoned => zoned.UtcDateTime,
            DateTime unzoned => DateTime.SpecifyKind(unzoned, DateTimeKind.Utc),
            null or DBNull when !required => null,
            _ => throw EventTimestampUnreadable(dataSource, eventId, field, raw),
        };

    /// <summary>Відмова «час події не прочитати».</summary>
    private static BusinessRuleException EventTimestampUnreadable(
        string dataSource, string eventId, string field, object? raw)
    {
        var valueType = raw is null or DBNull ? "NULL" : raw.GetType().Name;

        return new BusinessRuleException(
            IExternalDataSource.QueryRefusedCode,
            $"Запит подій джерела {dataSource} повернув {field} типу {valueType} для події «{eventId}»: "
            + "час прочитати не можна, події вікна не прочитано.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-INT-0422.eventTimestampUnreadable",
                ["dataSource"] = dataSource,
                ["eventId"] = eventId,
                ["field"] = field,
                ["valueType"] = valueType,
            });
    }

    /// <summary>Текст запиту подій або відмова <c>ECR-INT-0422</c> <c>.eventQueryNotConfigured</c>.</summary>
    /// <param name="key">Спільний ключ.</param>
    /// <param name="kind">Тип запиту — для відмови.</param>
    /// <param name="dataSourceCode">Код джерела: спершу його власний ключ (<see cref="ConfiguredQuery"/>).</param>
    private string EventQueryOrRefuse(string key, string kind, string dataSourceCode)
        => ConfiguredQuery(settings, dataSourceCode, key)
           ?? throw NotConfigured(dataSourceCode, key, kind, "err.ECR-INT-0422.eventQueryNotConfigured", key);

    /// <summary>Текст запиту подій із шаблоном, підставленим літералом.</summary>
    /// <remarks>
    /// ⚠ Шаблон — текстом, не параметром: RTQP вимагає його літералом (як
    /// <c>{template}</c> у <see cref="ValueQueryKey"/>), тож він проходить
    /// <see cref="Literal"/> — подвоєння лапки й заборона керівних символів.
    /// Публічний для тестів.
    /// </remarks>
    /// <param name="configured">Текст із <see cref="EventQueryKey"/>.</param>
    /// <param name="template">Шаблон подій.</param>
    public static string EventQuery(string configured, string template)
    {
        ArgumentNullException.ThrowIfNull(configured);
        ArgumentNullException.ThrowIfNull(template);

        return configured.Replace("{template}", Literal(template), StringComparison.Ordinal);
    }

    /// <summary>Межа вікна в UTC: місцевий час переводиться, без поясу — вважається UTC.</summary>
    private static DateTime AsUtc(DateTime value)
        => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary>Текстове подання значення колонки; <c>NULL</c> — <c>null</c>.</summary>
    private static string? Text(object? raw)
        => raw switch
        {
            null => null,
            string text => text,
            _ => Convert.ToString(raw, CultureInfo.InvariantCulture),
        };

    /// <summary>Ключ запиту переліку елементів під коренем (синк довідника, <c>D-212</c>). Типового тексту немає.</summary>
    /// <remarks>
    /// ⛔ Типового тексту немає (V-3, як <see cref="CurrentValueQueryKey"/>): як RTQP адресує
    /// «корінь» (шлях, шаблон, категорія) — рішення конкретної бази AF, і вигаданий дефолт
    /// виглядав би робочим налаштуванням.
    /// <para>
    /// Контракт тексту: один параметр <c>?</c> — корінь (<c>EntityPath</c> сутності, інакше її код).
    /// Колонки: <c>ElementId</c> (GUID), <c>ElementName</c>, необов'язкова <c>ElementPath</c>.
    /// Адреса читання — <c>ElementName</c>: саме за ним <see cref="ReadCurrentAsync"/> шукає
    /// елемент (<c>WHERE e.Name = ?</c>). Повний — менше ніж <see cref="MaxCatalogRows"/>
    /// рядків і кожен має <c>ElementId</c>.
    /// </para>
    /// </remarks>
    public const string ElementListQueryKey = "PiSqlClient:ElementListQuery";

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ Без <see cref="ElementListQueryKey"/> — відмова <c>ECR-INT-0422</c>
    /// <c>.queryKindNotConfigured</c> ДО з'єднання.
    /// </remarks>
    public async Task<SourceElementsResult> DiscoverElementsAsync(int dataSourceId, string root, CancellationToken ct)
    {
        var source = await store.FindDataSourceAsync(dataSourceId, ct).ConfigureAwait(false)
                     ?? throw Unavailable($"Джерело {dataSourceId} не існує або вимкнене.", dataSourceId);

        var queryText = ConfiguredOrRefuse(source.Code, ElementListQueryKey, IExternalDataSource.ElementListQueryKind);

        using var connection = await OpenAsync(source, ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = queryText;
        command.Parameters.Add(new OdbcParameter("root", OdbcType.NVarChar) { Value = root });

        using var reader = await RetryAsync(
            () => command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct),
            IsTransientOdbcFailure,
            ct).ConfigureAwait(false);

        return await ReadElementsAsync(reader, MaxCatalogRows, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Елементи з результату <see cref="ElementListQueryKey"/>: <c>ElementId</c>, <c>ElementName</c>,
    /// необов'язкова <c>ElementPath</c>; адреса читання — ім'я.
    /// </summary>
    /// <remarks>Публічний для тестів: <c>OdbcDataReader</c> ззовні не зробиш.</remarks>
    /// <param name="reader">Відкритий результат запиту.</param>
    /// <param name="ceiling">Стеля рядків; дійшли до неї — перелік неповний.</param>
    /// <param name="ct">Скасування.</param>
    public static async Task<SourceElementsResult> ReadElementsAsync(
        DbDataReader reader, int ceiling, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var elements = new List<SourceElement>();
        var rows = 0;
        var complete = true;

        while (rows < ceiling && await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows++;
            var row = Row(reader);
            var id = Column(row, "ElementId") is { } raw ? Convert.ToString(raw, CultureInfo.InvariantCulture) : null;
            var name = Column(row, "ElementName") as string;

            // Без GUID чи імені елемент не зіставити й не прочитати: перелік неповний, а не падіння.
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            {
                complete = false;
                continue;
            }

            elements.Add(new SourceElement(id, name, Column(row, "ElementPath") as string, name));
        }

        return new SourceElementsResult(elements, complete && rows < ceiling);
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
    public static Task<List<SourceDataPoint>> ReadPointsAsync(
        DbDataReader reader, string sourcePath, int maxPoints, CancellationToken ct)
        => ReadSourcePointsAsync(reader, nameof(ExternalTransport.PiSqlClient), sourcePath, maxPoints, ct);

    /// <summary>Точки з результату запиту джерела; час — за правилом <see cref="Utc"/>.</summary>
    /// <remarks>
    /// ⛔ Рядок, чию мітку <c>Ts</c> не прочитати (<c>NULL</c>, рядок, число),
    /// — відмова <c>ECR-INT-0422</c> <c>.timestampUnreadable</c>, а не пропуск
    /// (аудит A7, як <c>SqlDataSource</c>). До F4e такий рядок мовчки
    /// відкидався, і невдалий інтервал записувався в покриття як «зібраний повністю».
    /// <para>
    /// ⚠ Окреме ім'я, а не перевантаження <see cref="ReadPointsAsync"/>: перевантаження
    /// робить неоднозначним <c>cref</c> на старий метод (CS0419 — помилка збірки).
    /// </para>
    /// </remarks>
    /// <param name="reader">Відкритий результат запиту.</param>
    /// <param name="dataSource">Код джерела — для тексту відмови.</param>
    /// <param name="sourcePath">Шлях атрибута для кожної точки.</param>
    /// <param name="maxPoints">Стеля батча.</param>
    /// <param name="ct">Скасування.</param>
    public static async Task<List<SourceDataPoint>> ReadSourcePointsAsync(
        DbDataReader reader, string dataSource, string sourcePath, int maxPoints, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var points = new List<SourceDataPoint>();

        while (points.Count < maxPoints && await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var row = Row(reader);
            var timestamp = Utc(Column(row, "Ts"), dataSource, sourcePath);

            var (numeric, text) = Value(Column(row, "Val"));
            var quality = Column(row, "Quality") as string;

            points.Add(new SourceDataPoint(
                sourcePath,
                timestamp,
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

    /// <summary>Запит із конфігурації (спершу власний джерела, далі спільний) або типовий.</summary>
    private string Query(string dataSourceCode, string key, string fallback)
        => ConfiguredQuery(settings, dataSourceCode, key) ?? fallback;

    /// <summary>
    /// Ключ запиту, власний для джерела: <c>PiSqlClient:{код}:EventQuery</c> для
    /// <c>PiSqlClient:EventQuery</c> — так само, як <c>Sql:{код}:…</c> у <c>SqlDataSource</c>.
    /// </summary>
    /// <param name="dataSourceCode">Код джерела.</param>
    /// <param name="key">Спільний ключ <c>PiSqlClient:…</c>.</param>
    public static string ScopedKey(string dataSourceCode, string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return key.StartsWith(KeyPrefix, StringComparison.Ordinal)
            ? key.Insert(KeyPrefix.Length, $"{dataSourceCode}:")
            : $"{KeyPrefix}{dataSourceCode}:{key}";
    }

    /// <summary>
    /// Текст запиту з налаштування: спершу власний ключ джерела
    /// (<see cref="ScopedKey"/>), далі спільний; <c>null</c> — немає жодного.
    /// </summary>
    /// <remarks>
    /// ⚠ Запит — властивість <b>джерела</b>, а не транспорту: два PI SQL-джерела
    /// (різні бази AF, різні шаблони подій) не мусять ділити один текст. Без
    /// власного ключа поведінка та сама, що й до F4e, — спільний ключ або типовий текст.
    /// Рішення людини 2026-09-29: так — для КОЖНОГО запиту (<c>Catalog</c>, <c>Template</c>,
    /// <c>Value</c>, <c>Interpolated</c>, <c>Summary</c>, <c>CurrentValue</c>, <c>ElementList</c>,
    /// події). Порожній чи пробільний ключ джерела = відсутній (як <c>ConfigurationSecretProvider</c>
    /// і <c>SqlDataSource</c>): порожній текст запитом не буває.
    /// Публічний для тестів: живого RTQP у контурі розробки немає.
    /// </remarks>
    /// <param name="settings">Канал налаштувань.</param>
    /// <param name="dataSourceCode">Код джерела.</param>
    /// <param name="key">Спільний ключ.</param>
    public static string? ConfiguredQuery(ISecretProvider? settings, string dataSourceCode, string key)
    {
        if (settings is null)
        {
            return null;
        }

        var own = settings.Find(ScopedKey(dataSourceCode, key));
        if (!string.IsNullOrWhiteSpace(own))
        {
            return own;
        }

        var shared = settings.Find(key);
        return string.IsNullOrWhiteSpace(shared) ? null : shared;
    }

    private const string KeyPrefix = "PiSqlClient:";

    /// <summary>Мітка часу з результату запиту в UTC.</summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><see cref="DateTimeOffset"/> — <b>конвертується</b> в UTC: пояс записано в самому значенні.</item>
    /// <item><see cref="DateTime"/> без поясу — <b>вважається UTC</b> (переозначення, не конвертація).
    /// ⚠ Це ЯВНЕ ПРИПУЩЕННЯ, типове від людини (аудит A7, як <c>SqlDataSource</c>): RTQP
    /// віддає час PI як UTC, а межі <c>?</c> ідуть у запит теж UTC, тож фільтр і
    /// мітки живуть в одній шкалі. Якщо текст запиту поверне місцевий час, ряд
    /// зсунеться на зсув поясу — лікується в самому тексті запиту.</item>
    /// <item>усе інше, зокрема <c>NULL</c>, — відмова <c>ECR-INT-0422</c>
    /// <c>.timestampUnreadable</c>, а не пропуск рядка.</item>
    /// </list>
    /// </remarks>
    /// <param name="raw">Значення колонки.</param>
    /// <param name="dataSource">Код джерела — для тексту відмови.</param>
    /// <param name="sourcePath">Що читали — для тексту відмови.</param>
    internal static DateTime Utc(object? raw, string dataSource, string sourcePath)
        => UtcOrNull(raw, dataSource, sourcePath) ?? throw TimestampUnreadable(dataSource, sourcePath, raw);

    /// <summary>Як <see cref="Utc"/>, але <c>NULL</c> — це <c>null</c>, а не відмова.</summary>
    /// <param name="raw">Значення колонки.</param>
    /// <param name="dataSource">Код джерела — для тексту відмови.</param>
    /// <param name="sourcePath">Що читали — для тексту відмови.</param>
    internal static DateTime? UtcOrNull(object? raw, string dataSource, string sourcePath)
        => raw switch
        {
            null or DBNull => null,
            DateTimeOffset zoned => zoned.UtcDateTime,
            DateTime unzoned => DateTime.SpecifyKind(unzoned, DateTimeKind.Utc),
            _ => throw TimestampUnreadable(dataSource, sourcePath, raw),
        };

    private const string TimestampUnreadableKey = "err.ECR-INT-0422.timestampUnreadable";

    /// <summary>Відмова «мітку часу не прочитати» — той самий ключ, що <c>SqlDataSource</c>.</summary>
    private static BusinessRuleException TimestampUnreadable(string dataSource, string sourcePath, object? raw)
    {
        var valueType = raw is null or DBNull ? "NULL" : raw.GetType().Name;

        return new BusinessRuleException(
            IExternalDataSource.QueryRefusedCode,
            $"Запит джерела {dataSource} для «{sourcePath}» повернув час типу {valueType}: "
            + "мітку часу прочитати не можна, інтервал не зібрано.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-INT-0422.timestampUnreadable",
                ["dataSource"] = dataSource,
                ["sourcePath"] = sourcePath,
                ["valueType"] = valueType,
            });
    }

    /// <summary>Чи це відмова <see cref="TimestampUnreadable"/>.</summary>
    private static bool IsTimestampRefusal(BusinessRuleException error)
        => error.Details is { } details
           && details.TryGetValue("messageKey", out var key)
           && string.Equals(key as string, TimestampUnreadableKey, StringComparison.Ordinal);

    /// <summary>Шаблон елемента; <c>null</c> — елемента немає.</summary>
    private async Task<string?> TemplateAsync(
        OdbcConnection connection, string dataSourceCode, string element, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Query(dataSourceCode, TemplateQueryKey, DefaultTemplateQuery);
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
    private string ValueQuery(string dataSourceCode, string template, string attribute)
        => Fill(Query(dataSourceCode, ValueQueryKey, DefaultValueQuery), template, attribute);

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

            // ⚠ Дата — ISO 8601 ("O"), а не ToString() за культурою потоку: той самий
            // атрибут давав би різний текст на різних серверах (і вічну «розбіжність»).
            DateTime date => (null, date.ToString("O", CultureInfo.InvariantCulture)),
            DateTimeOffset date => (null, date.ToString("O", CultureInfo.InvariantCulture)),

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
