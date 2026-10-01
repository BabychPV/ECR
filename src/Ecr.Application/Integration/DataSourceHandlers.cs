// src/Ecr.Application/Integration/DataSourceHandlers.cs
using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Sources;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

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
/// <remarks>
/// ⛔ <c>Error</c> — НОРМАЛІЗОВАНА категорія відмови
/// (<see cref="TestDataSourceConnectionHandler.FailureCategories"/>), а не текст
/// винятку транспорту (<c>S3</c> аудиту безпеки). Сирий текст розрізняв
/// «connection refused», «timeout», «TLS» і тексти чужого сервера — тобто
/// перетворював кнопку «перевірити» на сканер внутрішньої мережі. Сирий текст
/// лишається лише в серверному журналі.
/// </remarks>
/// <param name="Ok">Чи джерело відповіло.</param>
/// <param name="Error">Категорія відмови; <c>null</c> — успіх.</param>
/// <param name="Entities">Розмір кореневого каталогу джерела.</param>
/// <param name="MessageKey">Ключ каталогу з причиною відмови.</param>
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
///
/// ⛔ Єдине поле, що стосується секрету, — <c>secretConfirmation</c> (S3): не
/// «задати секрет», а довести, що його знаєш, коли секрет, заданий у
/// середовищі, мав би поїхати на нову адресу. Значення лише звіряється і
/// ніде не зберігається.
/// </remarks>
public sealed partial class SaveDataSourceHandler(
    IDataSourceStore store,
    ISecretProvider secrets,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock,
    IEndpointNetwork? network = null,
    ILogger<SaveDataSourceHandler>? logger = null)
{
    /// <summary>Negotiate-джерело збережено без allowlist хостів: службові облікові дані підуть на будь-який дозволений блок-листом хост.</summary>
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Джерело {DataSource} з Windows-автентифікацією збережено без PiWebApi:AllowedHosts: адреса обмежена лише блок-листом.")]
    private static partial void LogNegotiateWithoutAllowlist(ILogger logger, string dataSource);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Джерело {DataSource} (PiSqlClient): адреса сервера поза PiWebApi:AllowedHosts; збережено, бо для SQL-транспорту це лише попередження.")]
    private static partial void LogSqlHostOutsideAllowlist(ILogger logger, string dataSource);

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

    /// <summary>Ключ відмови: адреса змінюється, а секрет не підтверджено.</summary>
    public const string SecretReentryRequiredKey = "err.ECR-REQ-0422.dataSourceSecretReentryRequired";

    /// <summary>Ключ відмови: адреса Negotiate-джерела змінюється без явного підтвердження.</summary>
    public const string EndpointChangeUnconfirmedKey = "err.ECR-REQ-0422.dataSourceEndpointChangeUnconfirmed";

    /// <summary>Заводить джерело; код має бути вільним.</summary>
    /// <remarks>
    /// <c>secretConfirmation</c> — значення секрету середовища під це джерело,
    /// лише коли він там заданий (див. <see cref="RequireSecretConfirmation"/>).
    /// Не зберігається.
    /// </remarks>
    public async Task<DataSourceView> CreateAsync(
        string code,
        IReadOnlyDictionary<string, string>? name,
        ExternalTransport transport,
        string? endpoint,
        string? secondaryEndpoint,
        string? catalog,
        int? maxParallel,
        string? secretConfirmation,
        CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var ecrCode = EcrCode.Create(code ?? string.Empty);
        var parsed = await ValidateAsync(
            ecrCode.Value, name, transport, endpoint, secondaryEndpoint, maxParallel, exceptId: null, ct)
            .ConfigureAwait(false);

        // ⛔ Створення — теж прив'язка секрету до адреси: ім'я секрету виводиться
        // з КОДУ, і секрет, що лишився в середовищі після видаленого джерела,
        // інакше поїхав би на будь-яку адресу нового джерела з тим самим кодом.
        RequireSecretConfirmation(SecretNamePrefix + ecrCode.Value, secretConfirmation, ecrCode.Value);

        var source = new DataSource(
            ecrCode, parsed.Name, transport, parsed.Endpoint, SecretNamePrefix + ecrCode.Value);
        source.Configure(parsed.SecondaryEndpoint, Trim(catalog), parsed.MaxParallel);

        store.Add(source);

        // D8: створення з'єднання (адреса — SSRF-чутлива) лишає слід у журналі структурних
        // змін В ТІЙ САМІЙ транзакції, що й збереження.
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);

            await IntegrationConfigAudit.WriteAsync(
                audit, clock, currentUser, IntegrationConfigAudit.DataSourceType, source.Id, "Create",
                oldJson: null, newJson: IntegrationConfigAudit.Snapshot(source),
                reason: $"З'єднання «{source.Code}» заведено.", innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        await AuditAsync(profile.UserId, new { id = source.Id, code = source.Code, created = true }, ct)
            .ConfigureAwait(false);

        return ListDataSourcesHandler.ToView(source, new DataSourceUsage(0, 0), secrets);
    }

    /// <summary>Змінює джерело; код і ім'я секрету лишаються.</summary>
    /// <remarks>
    /// <c>secretConfirmation</c> — значення секрету середовища; обов'язкове, коли
    /// змінюється адреса (<see cref="DataSourceAddress.SameTarget"/>) джерела,
    /// під яке секрет заданий. Не зберігається й не пишеться нікуди.
    /// </remarks>
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
        string? secretConfirmation,
        bool confirmEndpointChange,
        CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var source = await ListDataSourcesHandler.FindAsync(store, id, ct).ConfigureAwait(false);
        ListDataSourcesHandler.RequireCurrentVersion(source, ifMatch);

        var parsed = await ValidateAsync(
            source.Code, name, transport, endpoint, secondaryEndpoint, maxParallel, id, ct).ConfigureAwait(false);

        // ⛔ S3 аудиту безпеки: секрет середовища прив'язаний до ІМЕНІ джерела,
        // а не до адреси, і адаптери чіпляють його до будь-якої адреси, що
        // стоїть у рядку (`Authorization`, `Password`, `PWD`). Без цієї
        // перевірки право `Integration.Manage` означало «надішли службовий
        // секрет куди скажу»: PUT на чужий хост, потім «перевірити з'єднання».
        var addressChanged = !DataSourceAddress.SameTarget(
            source.Transport, source.Endpoint, source.SecondaryEndpoint,
            transport, parsed.Endpoint, parsed.SecondaryEndpoint);

        if (addressChanged)
        {
            // ⛔ Negotiate-джерело (секрету немає або він «Negotiate») несе
            // облікові дані СЛУЖБОВОГО акаунта процесу: повторно вводити нічого,
            // тож доказом наміру є явне `confirmEndpointChange` (SSRF-аудит).
            // Джерело із секретом-заголовком — як і раніше, повторне введення (S3).
            if (!IsNegotiate(source.SecretName))
            {
                RequireSecretConfirmation(source.SecretName, secretConfirmation, source.Code);
            }
            else if (!confirmEndpointChange)
            {
                throw new BusinessRuleException(
                    ErrorCodes.RequestInvalid,
                    $"Адреса джерела «{source.Code}» з Windows-автентифікацією змінюється: потрібне явне підтвердження.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = EndpointChangeUnconfirmedKey,
                        ["field"] = "confirmEndpointChange",
                        ["code"] = source.Code,
                    });
            }
        }

        // Старі значення — ДО зміни: після `Update` сутність їх уже не пам'ятає.
        var oldTransport = source.Transport;
        var oldEndpoint = source.Endpoint;
        var oldSecondary = source.SecondaryEndpoint;

        var before = IntegrationConfigAudit.Snapshot(source);

        source.Update(parsed.Name, transport, parsed.Endpoint, isActive);
        source.Configure(parsed.SecondaryEndpoint, Trim(catalog), parsed.MaxParallel);

        // D8: стан ДО і ПІСЛЯ — у журнал структурних змін, в одній транзакції зі збереженням.
        // Лише справжня зміна налаштувань з'єднання (ім'я/каталог без зміни знімка шуму не дають).
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);

            var after = IntegrationConfigAudit.Snapshot(source);
            if (string.Equals(before, after, StringComparison.Ordinal))
            {
                return;
            }

            await IntegrationConfigAudit.WriteAsync(
                audit, clock, currentUser, IntegrationConfigAudit.DataSourceType, source.Id, "Update",
                before, after,
                reason: $"З'єднання «{source.Code}» змінено.", innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        // ⛔ Стара й нова адреса — у журнал (S3/S20): без них журнал не
        // відповідав на питання «куди джерело дивилося вчора». Адреса не несе
        // секрету за побудовою — `RequireNoCredentials` відхиляє такі ДО запису.
        // Значення секрету (і підтвердження) сюди не потрапляє ніколи.
        await AuditAsync(
            profile.UserId,
            new
            {
                id,
                code = source.Code,
                isActive,
                oldTransport = oldTransport.ToString(),
                newTransport = transport.ToString(),
                oldEndpoint,
                newEndpoint = parsed.Endpoint,
                oldSecondaryEndpoint = oldSecondary,
                newSecondaryEndpoint = parsed.SecondaryEndpoint,
                addressChanged,
            },
            ct).ConfigureAwait(false);

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

        if (transport == ExternalTransport.PiWebApi)
        {
            var bound = secrets.Find(SecretNamePrefix + code);
            var negotiate = string.IsNullOrWhiteSpace(bound)
                            || string.Equals(bound.Trim(), "Negotiate", StringComparison.OrdinalIgnoreCase);

            await RequireAllowedEndpointAsync(address, "endpoint", negotiate, ct).ConfigureAwait(false);

            if (negotiate && logger is not null && network?.AllowedHosts is not { Count: > 0 })
            {
                LogNegotiateWithoutAllowlist(logger, code);
            }

            if (spare is { Length: > 0 })
            {
                await RequireAllowedEndpointAsync(spare, "secondaryEndpoint", negotiate, ct).ConfigureAwait(false);
            }
        }

        if (transport == ExternalTransport.PiSqlClient)
        {
            await RequireSqlServerAddressAsync(address, "endpoint", code, ct).ConfigureAwait(false);

            if (spare is { Length: > 0 })
            {
                await RequireSqlServerAddressAsync(spare, "secondaryEndpoint", code, ct).ConfigureAwait(false);
            }
        }

        if (await store.IsCodeTakenAsync(code, exceptId, ct).ConfigureAwait(false))
        {
            throw ListDataSourcesHandler.Invalid(
                "err.ECR-REQ-0422.dataSourceCodeTaken", $"Джерело з кодом «{code}» уже є.", code);
        }

        return new Parsed(new LocalizedText(named), address, spare, parallel);
    }

    /// <summary>
    /// Адреса PiSqlClient — ім'я сервера, не URL; link-local/metadata (літерал чи розв'язаний) відхиляється.
    /// Збій розв'язання не блокує (внутрішні імена). Хост поза <c>PiWebApi:AllowedHosts</c> — лише Warning.
    /// </summary>
    private async Task RequireSqlServerAddressAsync(string address, string field, string code, CancellationToken ct)
    {
        var verdict = DataSourceEndpointPolicy.CheckSqlServerAddress(address);

        // ⛔ ent4 P2-1: кожен сервер рядка з'єднання ODBC, а не весь рядок як «ім'я».
        var hosts = verdict == EndpointVerdict.Allowed
            ? DataSourceEndpointPolicy.SqlHostsOf(address) ?? []
            : [];

        foreach (var host in hosts)
        {
            if (verdict != EndpointVerdict.Allowed || network is null || IPAddress.TryParse(host, out _))
            {
                continue;
            }

            var resolved = await network.ResolveAsync(host, ct).ConfigureAwait(false);

            if (resolved.Any(DataSourceEndpointPolicy.IsLinkLocal))
            {
                verdict = EndpointVerdict.HostForbidden;
            }
        }

        if (verdict == EndpointVerdict.Allowed)
        {
            if (logger is not null && network?.AllowedHosts is { Count: > 0 } allowed
                && hosts.Any(host => !DataSourceEndpointPolicy.IsHostAllowed(host, allowed)))
            {
                LogSqlHostOutsideAllowlist(logger, code);
            }

            return;
        }

        var key = verdict switch
        {
            EndpointVerdict.Scheme => "err.ECR-REQ-0422.dataSourceEndpointSqlScheme",
            EndpointVerdict.HostForbidden => "err.ECR-REQ-0422.dataSourceEndpointSqlLinkLocal",
            _ => "err.ECR-REQ-0422.dataSourceEndpointMalformed",
        };

        throw new BusinessRuleException(
            ErrorCodes.RequestInvalid,
            $"Адресу джерела відхилено політикою ({verdict}).",
            new Dictionary<string, object?> { ["messageKey"] = key, ["field"] = field });
    }

    /// <summary>Адреса PI Web API проходить політику SSRF (<see cref="DataSourceEndpointPolicy"/>).</summary>
    private async Task RequireAllowedEndpointAsync(string address, string field, bool negotiate, CancellationToken ct)
    {
        var verdict = DataSourceEndpointPolicy.CheckAddress(address, negotiate, network?.AllowedHosts);

        if (verdict == EndpointVerdict.Allowed && network is not null
            && DataSourceEndpointPolicy.HostNeedingResolution(address) is { } name)
        {
            // Кожна A/AAAA-адреса; приватні за ім'ям дозволені (корпоративний AF).
            var resolved = await network.ResolveAsync(name, ct).ConfigureAwait(false);

            if (resolved.Any(DataSourceEndpointPolicy.IsBlocked))
            {
                verdict = EndpointVerdict.HostForbidden;
            }
        }

        var key = verdict switch
        {
            EndpointVerdict.Allowed => null,
            EndpointVerdict.Scheme => "err.ECR-REQ-0422.dataSourceEndpointScheme",
            EndpointVerdict.HostForbidden => "err.ECR-REQ-0422.dataSourceEndpointHostForbidden",
            EndpointVerdict.HostNotAllowed => "err.ECR-REQ-0422.dataSourceEndpointHostNotAllowed",
            _ => "err.ECR-REQ-0422.dataSourceEndpointMalformed",
        };

        if (key is not null)
        {
            // ⚠ Відмова називає ПОЛЕ, а не вміст адреси.
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Адресу джерела відхилено політикою ({verdict}).",
                new Dictionary<string, object?> { ["messageKey"] = key, ["field"] = field });
        }
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

    /// <summary>
    /// Прив'язати секрет середовища до (нової) адреси може лише той, хто цей
    /// секрет знає.
    /// </summary>
    /// <remarks>
    /// ⛔ Рішення (S3): <b>повторне введення в тому ж запиті</b>, а не
    /// «скидання» прив'язки. Скинути нічого: секрет живе в конфігурації
    /// процесу, а не в базі (<c>Q15-06</c>), <c>SecretName</c> — обов'язкова
    /// колонка, і відв'язка потребувала б міграції та окремої дії «прив'язати
    /// знову», якої в API немає. Повторне введення — це доказ знання: хто має
    /// лише <c>Integration.Manage</c>, не має секрету, тож і перенаправити його
    /// не може; хто секрет знає — переносить джерело одним запитом.
    ///
    /// ⚠ Секрету в середовищі немає (типовий стан, Windows-автентифікація) —
    /// перевірки немає: переносити нічого.
    ///
    /// ⚠ Одна відмова на «не ввели» і «ввели не те» — без оракула, який
    /// дозволив би підбирати значення, дивлячись на різницю відповідей.
    /// Порівняння — за сталий час. Значення не потрапляє ні в текст відмови,
    /// ні в журнал.
    /// </remarks>
    private void RequireSecretConfirmation(string secretName, string? confirmation, string code)
    {
        if (secrets.Find(secretName) is not { Length: > 0 } bound)
        {
            return;
        }

        var matches = confirmation is { Length: > 0 }
                      && CryptographicOperations.FixedTimeEquals(
                          Encoding.UTF8.GetBytes(confirmation), Encoding.UTF8.GetBytes(bound));

        if (!matches)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Адреса джерела «{code}» нова, тож його секрет треба ввести повторно: "
                + "секрет середовища йде лише на адресу, яку підтвердив той, хто його знає.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = SecretReentryRequiredKey,
                    ["field"] = "secretConfirmation",
                    ["code"] = code,
                });
        }
    }

    private bool IsNegotiate(string secretName)
    {
        var bound = secrets.Find(secretName);

        return string.IsNullOrWhiteSpace(bound)
               || string.Equals(bound.Trim(), "Negotiate", StringComparison.OrdinalIgnoreCase);
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
/// Що вважається «тією самою адресою» джерела — межа, за якою секрет треба
/// підтвердити повторно (S3).
/// </summary>
/// <remarks>
/// ⛔ Правило консервативне: сумнів — це зміна. Зайве повторне введення
/// коштує адміністраторові хвилину, пропущена зміна — службового секрету.
/// <list type="bullet">
/// <item>Інший транспорт — зміна: та сама стрічка читається іншим адаптером
/// і означає іншу ціль.</item>
/// <item>URL (<c>http</c>/<c>https</c>, транспорт PI Web API): порівнюються
/// схема, хост, порт, шлях і рядок запиту. Не зміна — лише регістр схеми й
/// хоста, явний типовий порт (<c>:443</c>) і кінцева <c>/</c>. Шлях — зміна:
/// на тому самому хості за іншим шляхом може стояти інший застосунок
/// (зворотний проксі). Фрагмент (<c>#…</c>) не йде в мережу й ігнорується.</item>
/// <item>Рядок з'єднання (<c>Sql</c>, <c>PiSqlClient</c>): порівнюються ВСІ
/// пари «ключ=значення»; не зміна — лише порядок ключів, пробіли й регістр
/// назв ключів. Білого списку «безпечних» ключів немає навмисно: секрет
/// перенаправляють не лише <c>Server</c>, а й <c>Failover Partner</c>,
/// <c>Network Library</c>, а <c>Encrypt</c>/<c>TrustServerCertificate</c>
/// роблять його видимим у мережі.</item>
/// <item>Запасна адреса — за тими самими правилами: адаптер, що колись почне
/// нею користуватися, понесе туди той самий секрет.</item>
/// </list>
/// Каталог (<c>Catalog</c>) не адреса: він обирає базу на ТОМУ САМОМУ сервері.
/// </remarks>
public static class DataSourceAddress
{
    /// <summary>Чи дві конфігурації ведуть секрет в одне й те саме місце.</summary>
    public static bool SameTarget(
        ExternalTransport oldTransport, string? oldEndpoint, string? oldSecondary,
        ExternalTransport newTransport, string? newEndpoint, string? newSecondary)
        => oldTransport == newTransport
           && string.Equals(Canonical(oldEndpoint), Canonical(newEndpoint), StringComparison.Ordinal)
           && string.Equals(Canonical(oldSecondary), Canonical(newSecondary), StringComparison.Ordinal);

    /// <summary>Канонічна форма адреси; <c>null</c> — адреси немає.</summary>
    public static string? Canonical(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        var text = address.Trim();

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{uri.Scheme}://{uri.IdnHost.ToUpperInvariant()}:{uri.Port}{uri.AbsolutePath.TrimEnd('/')}{uri.Query}");
        }

        try
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = text };

            if (builder.Count > 0)
            {
                return string.Join(
                    ";",
                    builder.Keys.Cast<string>()
                        .Select(key => (Key: key.ToUpperInvariant(), Value: Convert.ToString(builder[key], CultureInfo.InvariantCulture)))
                        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .Select(pair => $"{pair.Key}={pair.Value}"));
            }
        }
        catch (ArgumentException)
        {
            // Не розбирається як рядок з'єднання — порівнюється як є.
        }

        return text;
    }
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
public sealed partial class TestDataSourceConnectionHandler(
    IDataSourceStore store,
    IEnumerable<IExternalDataSource> adapters,
    SourceProbeGate gate,
    IAccessDecisionService access,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock,
    ILogger<TestDataSourceConnectionHandler> logger)
{
    /// <summary>Відмовило в автентифікації (<c>401</c>/<c>403</c>, «Login failed»).</summary>
    public const string FailureAuth = "auth";

    /// <summary>TCP-з'єднання не встановилося, ім'я не резолвиться, джерело відповіло помилкою.</summary>
    public const string FailureUnreachable = "unreachable";

    /// <summary>TLS-рукостискання не вдалося (сертифікат, протокол).</summary>
    public const string FailureTls = "tls";

    /// <summary>Джерело не відповіло вчасно.</summary>
    public const string FailureTimeout = "timeout";

    /// <summary>Будь-що інше.</summary>
    public const string FailureOther = "other";

    /// <summary>Транспорт джерела не має адаптера.</summary>
    public const string FailureAdapterNotRegistered = "adapterNotRegistered";

    /// <summary>Усі категорії відмови, які може віддати проба.</summary>
    public static readonly IReadOnlyList<string> FailureCategories =
    [
        FailureAuth, FailureUnreachable, FailureTls, FailureTimeout, FailureOther, FailureAdapterNotRegistered,
    ];

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
                false, FailureAdapterNotRegistered, 0, "integration.test.adapterNotRegistered");
        }

        try
        {
            var catalog = await adapter.DiscoverAsync(source.Id, ct).ConfigureAwait(false);

            return new DataSourceTestResult(true, null, catalog.Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Відмова джерела — це й є відповідь проби, а не аварія запиту.
        catch (Exception e)
#pragma warning restore CA1031
        {
            var category = Classify(e);

            // ⛔ Сирий текст — ЛИШЕ сюди (S3). Він потрібен тому, хто лагодить
            // з'єднання, і він же — відповідь сканера мережі, якщо віддати його
            // в тіло: «connection refused» проти «timeout» проти «TLS» проти
            // банера чужого сервера.
            LogProbeFailed(logger, e, source.Code, category);

            return new DataSourceTestResult(false, category, 0, "integration.test.failed." + category);
        }
    }

    /// <summary>Рядок серверного журналу з винятком адаптера — єдине місце його тексту.</summary>
    /// <param name="logger">Журнал.</param>
    /// <param name="error">Виняток адаптера.</param>
    /// <param name="dataSource">Код джерела.</param>
    /// <param name="category">Категорія, яку побачив клієнт.</param>
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Перевірка з'єднання з джерелом {DataSource} не пройшла: {Category}.")]
    private static partial void LogProbeFailed(ILogger logger, Exception error, string dataSource, string category);

    /// <summary>Виняток адаптера → одна з кількох категорій; текст винятку не читається.</summary>
    /// <remarks>
    /// ⚠ За ТИПОМ і кодом, а не за текстом: текст залежить від мови ОС і
    /// сервера. Порядок має значення: TLS-відмова приходить як
    /// <see cref="HttpRequestException"/> з <see cref="AuthenticationException"/>
    /// всередині, тож перевіряється раніше за «недоступність»; таймаут
    /// <c>HttpClient</c> — це <see cref="TaskCanceledException"/>, не
    /// скасування запиту (те відсіяне вище за <c>ct</c>).
    /// </remarks>
    /// <param name="error">Виняток адаптера.</param>
    public static string Classify(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var chain = new List<Exception>();

        for (var e = error; e is not null && chain.Count < 16; e = e.InnerException)
        {
            chain.Add(e);
        }

        if (chain.Exists(e => e is SourceAuthenticationException))
        {
            return FailureAuth;
        }

        if (chain.Exists(e => e is AuthenticationException
                              || e is HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError }))
        {
            return FailureTls;
        }

        if (chain.Exists(e => e is TimeoutException or TaskCanceledException
                              || (e is SocketException socket && socket.SocketErrorCode == SocketError.TimedOut)
                              || (e is EcrException ecr && ecr.Details?.GetValueOrDefault("reason") is "timeout")))
        {
            return FailureTimeout;
        }

        if (chain.Exists(e => e is HttpRequestException or SocketException or DbException or IOException
                              || (e is EcrException ecr && ecr.ErrorCode == ErrorCodes.SourceUnavailable)))
        {
            return FailureUnreachable;
        }

        return FailureOther;
    }
}
