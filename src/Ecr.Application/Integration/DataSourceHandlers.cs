// src/Ecr.Application/Integration/DataSourceHandlers.cs
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Integration;

/// <summary>
/// Джерело даних — рядок екрана конфігуратора (<c>BE-21</c>, ФВ-14.3).
/// </summary>
/// <remarks>
/// ⛔ Значення секрету тут немає: рішення людини на <c>Q15-06</c> — джерела
/// ходять під Windows-автентифікацією службового облікового запису, сховища
/// секретів у застосунку немає. <see cref="HasSecret"/> лишається ознакою
/// стану СЕРЕДОВИЩА: <c>true</c> означає, що секрет під це джерело все-таки
/// заданий у конфігурації процесу, і адміністратор має це бачити, не бачачи
/// значення.
/// </remarks>
public sealed record DataSourceView(
    int Id,
    string Code,
    IReadOnlyDictionary<string, string> NameL10n,
    ExternalTransport Transport,
    string Endpoint,
    string? SecondaryEndpoint,
    string? Catalog,
    int MaxParallel,
    bool IsActive,
    bool HasSecret,
    int SourceEntities,
    int CollectionSchedules,
    string RowVersion);

/// <summary>Наслідок перевірки з'єднання; <c>Entities</c> — розмір каталогу джерела.</summary>
public sealed record DataSourceTestResult(bool Ok, string? Error, int Entities, string? MessageKey = null);

/// <summary>
/// Перелік джерел. Право <c>Integration.View</c> або <c>Integration.Manage</c>.
/// </summary>
/// <remarks>
/// ⛔ <c>Integration.View</c> — одне з восьми прав, які сід видавав і які не
/// перевіряв ЖОДЕН обробник (<c>BE-28</c>). Це його перший викликач: право,
/// яке можна видати й яке нічого не відкриває, — брехня в матриці доступу.
/// </remarks>
public sealed class ListDataSourcesHandler(
    IDataSourceStore store, ISecretProvider secrets, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Право на перегляд конфігурації інтеграції (`02-contracts.md` §9).</summary>
    public const string Permission = "Integration.View";

    /// <summary>Віддає джерела разом із тим, що на них спирається.</summary>
    public async Task<IReadOnlyList<DataSourceView>> HandleAsync(CancellationToken ct)
    {
        // `Integration.Manage` включає `Integration.View`: хто заводить і тестує
        // з'єднання, мусить бачити їхній перелік (інакше екран «New connection» над 403).
        await PermissionCheck.RequireAnyAsync(
            access, currentUser, [Permission, SaveDataSourceHandler.Permission], ct).ConfigureAwait(false);

        var rows = await store.ListAsync(ct).ConfigureAwait(false);

        return [.. rows.Select(row => ToView(row.Source, row.Usage, secrets))];
    }

    /// <summary>Рядок екрана: ознака секрету замість секрету.</summary>
    internal static DataSourceView ToView(DataSource source, DataSourceUsage usage, ISecretProvider secrets)
        => new(
            source.Id, source.Code, source.NameL10n.Values, source.Transport, source.Endpoint,
            source.SecondaryEndpoint, source.Catalog, source.MaxParallel, source.IsActive,

            // ⛔ Саме `is not null`, а не значення: запис віддається клієнтові як
            // є, і будь-яке поле, у яке потрапить `Find(...)`, виїде в перший же
            // `GET` (ФВ-6.11).
            secrets.Find(source.SecretName) is not null,
            usage.SourceEntities,
            usage.CollectionSchedules,
            Convert.ToBase64String(source.RowVersion));

    /// <summary>
    /// Звіряє <c>If-Match</c> із версією рядка — той самий контракт, що в розкладах
    /// (<see cref="ListCollectionSchedulesHandler.RequireCurrentVersion"/>).
    /// </summary>
    /// <remarks>
    /// ⛔ Власна перевірка, а не токен EF: той ловить лише збіг у мілісекунди між
    /// читанням і записом ЦЬОГО запиту, а правку втрачають між двома відкритими
    /// шухлядами. Заголовка немає — <c>422</c>; версія чужа — <c>409</c>.
    /// </remarks>
    internal static void RequireCurrentVersion(DataSource source, string? ifMatch)
    {
        var expected = ListCollectionSchedulesHandler.NormalizeETag(ifMatch)
                       ?? throw Invalid(
                           "err.ECR-REQ-0422.dataSourceIfMatch",
                           "Запит на зміну з'єднання має нести заголовок If-Match зі значенням rowVersion.");

        var actual = Convert.ToBase64String(source.RowVersion);

        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new ConcurrencyConflictException(
                ErrorCodes.JobStateConflict,
                $"З'єднання «{source.Code}» змінили після того, як його прочитали.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-JOB-0409.dataSourceChanged",
                    ["rowVersion"] = actual,
                });
        }
    }

    internal static async Task<DataSource> FindAsync(IDataSourceStore store, int id, CancellationToken ct)
        => await store.FindAsync(id, ct).ConfigureAwait(false)
           ?? throw new NotFoundException(
               ErrorCodes.SourceEntityNotFound, $"Джерела даних {id} не існує.",
               new Dictionary<string, object?>
               {
                   ["messageKey"] = "err.ECR-INT-0404.dataSource",
                   ["id"] = id.ToString(CultureInfo.InvariantCulture),
               });

    internal static BusinessRuleException Invalid(string messageKey, string message, string? code = null)
        => new(
            ErrorCodes.RequestInvalid, message,
            new Dictionary<string, object?> { ["messageKey"] = messageKey, ["code"] = code });
}

/// <summary>
/// Створення і зміна джерела. Право <c>Integration.Manage</c>.
/// </summary>
/// <remarks>
/// ⛔ Поля «секрет» у запиті немає (<c>Q15-06</c>). Ім'я секрету виводиться з
/// коду (<c>DataSource.&lt;CODE&gt;</c>) і лишається точкою розширення: якщо
/// секрет колись знадобиться, його задають змінною середовища
/// <c>ECR_Secrets__DataSource.&lt;CODE&gt;</c>, і жоден рядок API не міняється.
/// </remarks>
public sealed class SaveDataSourceHandler(
    IDataSourceStore store,
    ISecretProvider secrets,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право на керування інтеграцією (`02-contracts.md` §9).</summary>
    public const string Permission = "Integration.Manage";

    /// <summary>Префікс імені секрету джерела в конфігурації процесу.</summary>
    public const string SecretNamePrefix = "DataSource.";

    /// <summary>
    /// Стеля паралельних звернень до одного джерела; типове значення — 4.
    /// </summary>
    /// <remarks>
    /// ⚠ Судження, не цифра з довідника: PI AF на надмірний паралелізм
    /// відповідає деградацією ВСІМ клієнтам, зокрема тим, що не наші.
    /// </remarks>
    public const int MaxParallelCeiling = 32;

    /// <summary>Тип події в журналі безпеки.</summary>
    public const string EventType = "DataSourceSaved";

    /// <summary>Заводить джерело; код має бути вільним.</summary>
    public async Task<DataSourceView> CreateAsync(
        string code,
        IReadOnlyDictionary<string, string>? name,
        ExternalTransport transport,
        string? endpoint,
        string? secondaryEndpoint,
        string? catalog,
        int? maxParallel,
        CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var ecrCode = EcrCode.Create(code ?? string.Empty);
        var parsed = await ValidateAsync(
            ecrCode.Value, name, transport, endpoint, secondaryEndpoint, maxParallel, exceptId: null, ct)
            .ConfigureAwait(false);

        var source = new DataSource(
            ecrCode, parsed.Name, transport, parsed.Endpoint, SecretNamePrefix + ecrCode.Value);
        source.Configure(parsed.SecondaryEndpoint, Trim(catalog), parsed.MaxParallel);

        store.Add(source);
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        await AuditAsync(profile.UserId, new { id = source.Id, code = source.Code, created = true }, ct)
            .ConfigureAwait(false);

        return ListDataSourcesHandler.ToView(source, new DataSourceUsage(0, 0), secrets);
    }

    /// <summary>Змінює джерело; код і ім'я секрету лишаються.</summary>
    public async Task<DataSourceView> UpdateAsync(
        int id,
        IReadOnlyDictionary<string, string>? name,
        ExternalTransport transport,
        string? endpoint,
        string? secondaryEndpoint,
        string? catalog,
        int? maxParallel,
        bool isActive,
        string? ifMatch,
        CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var source = await ListDataSourcesHandler.FindAsync(store, id, ct).ConfigureAwait(false);
        ListDataSourcesHandler.RequireCurrentVersion(source, ifMatch);

        var parsed = await ValidateAsync(
            source.Code, name, transport, endpoint, secondaryEndpoint, maxParallel, id, ct).ConfigureAwait(false);

        source.Update(parsed.Name, transport, parsed.Endpoint, isActive);
        source.Configure(parsed.SecondaryEndpoint, Trim(catalog), parsed.MaxParallel);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
        await AuditAsync(profile.UserId, new { id, code = source.Code, isActive }, ct).ConfigureAwait(false);

        var usage = await store.CountUsageAsync(id, ct).ConfigureAwait(false);

        return ListDataSourcesHandler.ToView(source, usage, secrets);
    }

    private sealed record Parsed(LocalizedText Name, string Endpoint, string? SecondaryEndpoint, int MaxParallel);

    private async Task<Parsed> ValidateAsync(
        string code,
        IReadOnlyDictionary<string, string>? name,
        ExternalTransport transport,
        string? endpoint,
        string? secondaryEndpoint,
        int? maxParallel,
        int? exceptId,
        CancellationToken ct)
    {
        var address = Trim(endpoint);
        var spare = Trim(secondaryEndpoint);
        var parallel = maxParallel ?? 4;

        var named = name?.Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value.Trim(), StringComparer.OrdinalIgnoreCase);

        if (named is not { Count: > 0 }
            || !Enum.IsDefined(transport)
            || address is not { Length: > 0 and <= 400 }
            || spare is { Length: > 400 }
            || parallel is < 1 or > MaxParallelCeiling)
        {
            throw ListDataSourcesHandler.Invalid(
                "err.ECR-REQ-0422.dataSourceInvalid",
                "Джерело потребує назви хоча б однією мовою, відомого транспорту, адреси до 400 символів "
                + $"і стелі паралельних звернень 1..{MaxParallelCeiling}.",
                code);
        }

        // ⛔ Облікові дані в адресі ловляться ДО перевірки унікальності коду:
        // інакше перший же дублікат ховав би причину, через яку відмовляти
        // треба в будь-якому разі.
        RequireNoCredentials(address, "endpoint");
        RequireNoCredentials(spare, "secondaryEndpoint");

        if (await store.IsCodeTakenAsync(code, exceptId, ct).ConfigureAwait(false))
        {
            throw ListDataSourcesHandler.Invalid(
                "err.ECR-REQ-0422.dataSourceCodeTaken", $"Джерело з кодом «{code}» уже є.", code);
        }

        return new Parsed(new LocalizedText(named), address, spare, parallel);
    }

    /// <summary>
    /// Слова, поява яких в адресі означає, що в неї вписали обліковий секрет.
    /// </summary>
    /// <remarks>
    /// ⚠ Саме ці, бо саме так їх пишуть: <c>Password=</c>/<c>Pwd=</c> — рядок
    /// з'єднання SQL Server, <c>sig=</c> — підпис URL Azure, решта — ключі API
    /// параметром запиту.
    /// </remarks>
    private static readonly string[] CredentialMarkers =
    [
        "password=", "pwd=", "secret=", "token=", "apikey=", "api-key=", "accesskey=", "sig=",
    ];

    /// <summary>
    /// Адреса не має нести облікових даних.
    /// </summary>
    /// <remarks>
    /// ⛔ Це і є «write-only» у тому вигляді, який лишився після <c>Q15-06</c>.
    /// Сховища секретів немає, отже єдиний спосіб покласти пароль у базу —
    /// вписати його в НЕСЕКРЕТНЕ поле; для транспорту <c>Sql</c> адреса і є
    /// рядком з'єднання, тож <c>Server=…;Password=…</c> тут виглядає природно.
    /// Пароль пішов би в <c>ext.DataSource</c>, у резервну копію — і назад у
    /// КОЖНУ відповідь <c>GET</c>.
    ///
    /// ⚠ Відмова не повторює введеного: інакше пароль, який ми щойно
    /// відмовилися зберігати, поїхав би в журнал разом із її текстом. Тому
    /// відмова називає ПОЛЕ (<c>field</c>: <c>endpoint</c> або
    /// <c>secondaryEndpoint</c>), а не вміст. Несуть обидві — називається
    /// <c>endpoint</c>: перевірка йде в порядку полів форми і зупиняється на першому.
    /// </remarks>
    internal static void RequireNoCredentials(string? address, string field)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return;
        }

        // Пробіли прибираються: `Password = x` у рядку з'єднання законне.
        var packed = address.Replace(" ", string.Empty, StringComparison.Ordinal);

        var carries = CredentialMarkers.Any(
            marker => packed.Contains(marker, StringComparison.OrdinalIgnoreCase))
            || HasUserInfo(address);

        if (carries)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                "Адреса джерела не має нести облікових даних: джерела ходять під службовим обліковим записом.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.dataSourceEndpointCarriesSecret",
                    ["field"] = field,
                });
        }
    }

    /// <summary>Чи адреса має частину <c>scheme://user:pass@host</c>.</summary>
    private static bool HasUserInfo(string address)
    {
        var scheme = address.IndexOf("://", StringComparison.Ordinal);

        if (scheme < 0)
        {
            return false;
        }

        var rest = address[(scheme + 3)..];
        var at = rest.IndexOf('@', StringComparison.Ordinal);
        var slash = rest.IndexOf('/', StringComparison.Ordinal);

        return at > 0 && (slash < 0 || at < slash) && rest[..at].Contains(':', StringComparison.Ordinal);
    }

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private Task AuditAsync(int byUserId, object details, CancellationToken ct)
        => audit.WriteSecurityEventAsync(
            new SecurityEventRecord(
                clock.UtcNow, EventType, TargetUserId: null, TargetRoleId: null,
                JsonSerializer.Serialize(details), byUserId, currentUser.CorrelationId),
            ct);
}

/// <summary>
/// Видалення джерела. Право <c>Integration.Manage</c>.
/// </summary>
/// <remarks>
/// ⛔ <b>Заборона, а не каскад.</b> Каскад потягнув би сутності збору, їхні
/// мапінги, розклади і ВСІ зібрані точки <c>ext.RawDataPoint</c> разом із
/// журналом покриття: одна кнопка стирала б історію вимірювань, за якою вже
/// пораховані й підписані документи. Джерело, з якого більше не збирають,
/// вимикається (<c>isActive = false</c>) — це оборотно; видаляється лише те,
/// на що ніщо не спирається.
/// </remarks>
public sealed class DeleteDataSourceHandler(
    IDataSourceStore store,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>
    /// Код відмови: на джерело ще спирається конфігурація збору.
    /// </summary>
    /// <remarks>
    /// ⚠ Значення те саме, що в <see cref="RestartJobHandler.NotFailedErrorCode"/>,
    /// НАВМИСНО: одна природа відмови («стан не дозволяє дію»), один
    /// HTTP-статус, і арм у <c>ExceptionHandlingMiddleware</c> мапить саме код.
    /// </remarks>
    public const string InUseErrorCode = ErrorCodes.JobStateConflict;

    /// <summary>Тип події в журналі безпеки.</summary>
    public const string EventType = "DataSourceDeleted";

    /// <summary>Прибирає джерело, на яке ніщо не спирається.</summary>
    public async Task HandleAsync(int id, string? ifMatch, CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireAsync(access, currentUser, SaveDataSourceHandler.Permission, ct).ConfigureAwait(false);

        var source = await ListDataSourcesHandler.FindAsync(store, id, ct).ConfigureAwait(false);
        ListDataSourcesHandler.RequireCurrentVersion(source, ifMatch);

        var usage = await store.CountUsageAsync(id, ct).ConfigureAwait(false);

        if (usage.SourceEntities > 0 || usage.CollectionSchedules > 0)
        {
            throw new BusinessRuleException(
                InUseErrorCode,
                $"На джерело «{source.Code}» спирається {usage.SourceEntities} сутностей збору "
                + $"і {usage.CollectionSchedules} розкладів: вимкніть його замість видалення.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-JOB-0409.dataSourceInUse",
                    ["sourceEntities"] = usage.SourceEntities.ToString(CultureInfo.InvariantCulture),
                    ["collectionSchedules"] = usage.CollectionSchedules.ToString(CultureInfo.InvariantCulture),
                });
        }

        store.Remove(source);
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        await audit.WriteSecurityEventAsync(
            new SecurityEventRecord(
                clock.UtcNow, EventType, TargetUserId: null, TargetRoleId: null,
                JsonSerializer.Serialize(new { id, code = source.Code }), profile.UserId,
                currentUser.CorrelationId),
            ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Які джерела зараз опитують перевіркою з'єднання.
/// </summary>
/// <remarks>
/// ⛔ Синглтон процесу, і цього досить: планувальник теж у пам'яті процесу
/// (<c>D-66</c>). Розподілений замок коштував би таблиці й прибирання
/// застряглих записів заради дії, яка триває секунди.
/// </remarks>
public sealed class SourceProbeGate
{
    private readonly ConcurrentDictionary<int, byte> inFlight = new();

    /// <summary>Займає джерело; <c>false</c> — проба вже йде.</summary>
    public bool TryBegin(int dataSourceId) => inFlight.TryAdd(dataSourceId, 0);

    /// <summary>Звільняє джерело.</summary>
    public void End(int dataSourceId) => inFlight.TryRemove(dataSourceId, out _);
}

/// <summary>
/// Перевірка з'єднання з джерелом. Право <c>Integration.Manage</c>.
/// </summary>
/// <remarks>
/// ⚠ Проба йде ТИМ САМИМ адаптером, яким потім збиратимуть
/// (<see cref="IExternalDataSource.DiscoverAsync"/>). Окремий «ping» ходив би
/// іншою дорогою й зеленів би саме тоді, коли збір не працює: доступ до кореня
/// Web API і доступ до бази AF — різні права службового запису.
///
/// ⚠ Причина ОБОВ'ЯЗКОВА і йде в журнал безпеки — той самий вибір, що в
/// <see cref="Consistency.RunConsistencyCheckHandler"/>.
/// </remarks>
public sealed class TestDataSourceConnectionHandler(
    IDataSourceStore store,
    IEnumerable<IExternalDataSource> adapters,
    SourceProbeGate gate,
    IAccessDecisionService access,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Код відмови: перевірка цього джерела вже виконується.</summary>
    public const string AlreadyRunningErrorCode = ErrorCodes.JobStateConflict;

    /// <summary>Тип події в журналі безпеки.</summary>
    public const string EventType = "DataSourceConnectionTested";

    /// <summary>Стеля довжини причини — та сама, що в прогоні перевірки узгодженості.</summary>
    public const int MaxReasonLength = 400;

    /// <summary>Опитує джерело; відмова джерела — відповідь, а не виняток.</summary>
    public async Task<DataSourceTestResult> HandleAsync(int id, string? reason, CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireAsync(access, currentUser, SaveDataSourceHandler.Permission, ct).ConfigureAwait(false);

        var trimmed = (reason ?? string.Empty).Trim();

        if (trimmed.Length is 0 or > MaxReasonLength)
        {
            throw ListDataSourcesHandler.Invalid(
                "err.ECR-REQ-0422.dataSourceTestReason",
                $"Перевірка з'єднання потребує причини до {MaxReasonLength} символів: вона йде в журнал безпеки.");
        }

        var source = await ListDataSourcesHandler.FindAsync(store, id, ct).ConfigureAwait(false);

        // ⛔ Ворота ПЕРЕД пробою. Без них кожен зайвий натиск кнопки відкриває
        // ще одне з'єднання до чужої системи, а відповіді приходять у довільному
        // порядку: екран показує результат тієї проби, яка повернулася останньою,
        // а не останньої запущеної.
        if (!gate.TryBegin(id))
        {
            throw new BusinessRuleException(
                AlreadyRunningErrorCode,
                $"Перевірка з'єднання з джерелом «{source.Code}» уже виконується.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-JOB-0409.dataSourceTestRunning",
                    ["code"] = source.Code,
                });
        }

        DataSourceTestResult result;

        try
        {
            result = await ProbeAsync(source, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.End(id);
        }

        // ⛔ У деталях — факт і підсумок, без тексту відмови транспорту: його
        // складає не наш код, і адреса джерела поїхала б у журнал разом із ним.
        await audit.WriteSecurityEventAsync(
            new SecurityEventRecord(
                clock.UtcNow, EventType, TargetUserId: null, TargetRoleId: null,
                JsonSerializer.Serialize(new { id, code = source.Code, reason = trimmed, ok = result.Ok }),
                profile.UserId, currentUser.CorrelationId),
            ct).ConfigureAwait(false);

        return result;
    }

    /// <summary>Сама проба: адаптер обирається транспортом джерела (ФВ-11.2).</summary>
    private async Task<DataSourceTestResult> ProbeAsync(DataSource source, CancellationToken ct)
    {
        var adapter = adapters.FirstOrDefault(a => a.Transport == source.Transport);

        if (adapter is null)
        {
            return new DataSourceTestResult(
                false, $"No adapter is registered for the {source.Transport} transport.", 0,
                "integration.test.adapterNotRegistered");
        }

        try
        {
            var catalog = await adapter.DiscoverAsync(source.Id, ct).ConfigureAwait(false);

            return new DataSourceTestResult(true, null, catalog.Count);
        }
#pragma warning disable CA1031 // Відмова джерела — це й є відповідь проби, а не аварія запиту.
        catch (Exception e) when (e is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return new DataSourceTestResult(false, e.Message, 0);
        }
    }
}
