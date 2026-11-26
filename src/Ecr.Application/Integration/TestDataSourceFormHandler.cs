// src/Ecr.Application/Integration/TestDataSourceFormHandler.cs
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
using Microsoft.Extensions.Logging;

namespace Ecr.Application.Integration;

/// <summary>Незбережені значення форми для перевірки з'єднання (P-1126-SRCCRED §13.1).</summary>
/// <param name="SourceId">Джерело, чий збережений пароль можна підставити при НЕЗМІНЕНІЙ цілі; <c>null</c> — нове.</param>
/// <param name="Transport">Транспорт (лише PiSqlClient і Sql).</param>
/// <param name="Endpoint">Рядок з'єднання.</param>
/// <param name="SecondaryEndpoint">Запасна адреса.</param>
/// <param name="Catalog">Каталог.</param>
/// <param name="AuthMode">Режим входу.</param>
/// <param name="UserName">Логін.</param>
/// <param name="Password">Пароль з форми.</param>
/// <param name="Reason">Причина для журналу безпеки.</param>
public sealed record DataSourceFormTest(
    int? SourceId, ExternalTransport Transport, string? Endpoint, string? SecondaryEndpoint, string? Catalog,
    SourceAuthMode AuthMode, string? UserName, SecretString? Password, string? Reason);

/// <summary>
/// Пауза перевірок цілі після відмов входу поспіль (Q-15: поріг блокування облікового запису AF ~20): після
/// <see cref="MaxConsecutiveFailures"/> відмов автентифікації поспіль ціль не перевіряється <see cref="Pause"/>.
/// </summary>
/// <remarks>⚠ Синглтон процесу, як <see cref="SourceProbeGate"/>: перевірка — дія адміністратора, не збір.</remarks>
public sealed class SourceTargetLockout
{
    /// <summary>Скільки відмов входу поспіль ставлять ціль на паузу.</summary>
    public const int MaxConsecutiveFailures = 3;

    /// <summary>Тривалість паузи.</summary>
    public static readonly TimeSpan Pause = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, (int Failures, DateTime? Until)> targets = new(StringComparer.Ordinal);

    /// <summary>Чи ціль зараз на паузі.</summary>
    public bool IsPaused(string target, DateTime utcNow)
        => targets.TryGetValue(target, out var state) && state.Until is { } until && until > utcNow;

    /// <summary>Ще одна відмова входу; третя поспіль ставить паузу.</summary>
    public void RecordFailure(string target, DateTime utcNow)
        => targets.AddOrUpdate(
            target,
            _ => (1, null),
            (_, state) => state.Failures + 1 >= MaxConsecutiveFailures ? (0, utcNow + Pause) : (state.Failures + 1, state.Until));

    /// <summary>Успішний вхід обнуляє лічильник.</summary>
    public void RecordSuccess(string target) => targets.TryRemove(target, out _);
}

/// <summary>
/// Перевірка з'єднання з НЕЗБЕРЕЖЕНИХ значень форми (P-1126-SRCCRED §13.1, AC-11a..e). Право <c>Integration.Manage</c>.
/// Нічого не зберігає.
/// </summary>
/// <remarks>
/// ⛔ Захисти: (a) та сама валідація адреси, що при збереженні; (b) пароль — з тіла, збережений — лише за
/// <c>sourceId</c> при незмінній цілі й логіні; (c) <c>Integration.Manage</c>; (d) межа частоти — на рівні API;
/// (e) ворота за користувачем і ціллю, а не за id; (f) журнал без адреси й пароля; (g) відповідь — лише категорія;
/// (h) відмова входу з тіла не ставить мітку джерелу, але рахується в паузі цілі (Q-15).
/// </remarks>
public sealed partial class TestDataSourceFormHandler(
    IDataSourceStore store,
    IEnumerable<IExternalConnectionProbe> probes,
    SaveDataSourceHandler validator,
    SourceProbeGate gate,
    SourceTargetLockout lockout,
    IAccessDecisionService access,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock,
    ILogger<TestDataSourceFormHandler> logger,
    IDataSourceCredentialResolver? credentials = null)
{
    /// <summary>Ключ: з тіла перевіряються лише PiSqlClient і Sql.</summary>
    public const string TransportKey = "err.ECR-REQ-0422.dataSourceTestTransport";

    /// <summary>Ключ: ціль на паузі після відмов входу поспіль (429).</summary>
    public const string TargetPausedKey = "err.ECR-REQ-0429.sourceTestTargetPaused";

    /// <summary>Перевіряє з'єднання; відмова джерела — відповідь, а не виняток.</summary>
    public async Task<DataSourceTestResult> HandleAsync(DataSourceFormTest form, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(form);

        var profile = await PermissionCheck
            .RequireAsync(access, currentUser, SaveDataSourceHandler.Permission, ct).ConfigureAwait(false);

        var reason = (form.Reason ?? string.Empty).Trim();
        if (reason.Length is 0 or > TestDataSourceConnectionHandler.MaxReasonLength)
        {
            throw ListDataSourcesHandler.Invalid(
                "err.ECR-REQ-0422.dataSourceTestReason", "Перевірка з'єднання потребує причини: вона йде в журнал безпеки.");
        }

        if (!DataSource.SupportsUserPassword(form.Transport))
        {
            throw ListDataSourcesHandler.Invalid(TransportKey, "З тіла форми перевіряються лише PiSqlClient і Sql.");
        }

        var stored = form.SourceId is { } id ? await ListDataSourcesHandler.FindAsync(store, id, ct).ConfigureAwait(false) : null;
        var code = stored?.Code ?? "FORM_TEST";

        var (endpoint, spare) = await validator
            .ValidateConnectionAsync(code, form.Transport, form.Endpoint, form.SecondaryEndpoint, form.Catalog, ct)
            .ConfigureAwait(false);

        var (resolvedCredentials, userName, rejected) = Credentials(form, stored, endpoint, spare);

        var target = Fingerprint(form.Transport, endpoint, spare, userName);
        var gateKey = profile.UserId.ToString(CultureInfo.InvariantCulture) + "|" + target;

        if (lockout.IsPaused(target, clock.UtcNow))
        {
            throw new BusinessRuleException(
                ErrorCodes.TooManyRequests,
                "Ціль кілька разів поспіль відмовила у вході: перевірки на паузі.",
                new Dictionary<string, object?> { ["messageKey"] = TargetPausedKey });
        }

        if (!gate.TryBegin(gateKey))
        {
            throw new BusinessRuleException(
                TestDataSourceConnectionHandler.AlreadyRunningErrorCode,
                "Перевірка цієї цілі вже виконується.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-JOB-0409.dataSourceTestRunning", ["code"] = code });
        }

        DataSourceTestResult result;
        try
        {
            result = rejected
                ? TestDataSourceConnectionHandler.Failed(TestDataSourceConnectionHandler.FailureCredentialsRejected)
                : await ProbeAsync(form, code, endpoint, spare, resolvedCredentials, target, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.End(gateKey);
        }

        // ⛔ (f): факт, причина й підсумок — без адреси, логіна й пароля.
        await audit.WriteSecurityEventAsync(
            new SecurityEventRecord(
                clock.UtcNow, TestDataSourceConnectionHandler.EventType, TargetUserId: null, TargetRoleId: null,
                JsonSerializer.Serialize(new { id = form.SourceId, unsaved = true, reason, ok = result.Ok }),
                profile.UserId, currentUser.CorrelationId),
            ct).ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// Облікові дані перевірки: з тіла; збережені — лише того самого джерела при незмінних цілі й логіні (b).
    /// </summary>
    private (ResolvedCredentials? Credentials, string? UserName, bool Rejected) Credentials(
        DataSourceFormTest form, DataSource? stored, string endpoint, string? spare)
    {
        if (form.AuthMode != SourceAuthMode.UserPassword)
        {
            return (null, null, false);
        }

        DataSourceCredentialRules.RequireNoWindowsSignIn(form.Transport, endpoint, "endpoint");
        DataSourceCredentialRules.RequireNoWindowsSignIn(form.Transport, spare, "secondaryEndpoint");

        var userName = form.UserName?.Trim();
        if (!DataSourceCredentialRules.IsValidUserName(userName))
        {
            throw Invalid(DataSourceCredentialRules.CredentialsInvalidKey, "userName");
        }

        if (form.Password is { } password)
        {
            if (!DataSourceCredentialRules.IsValidPassword(password))
            {
                throw Invalid(DataSourceCredentialRules.CredentialsInvalidKey, "password");
            }

            return (new ResolvedCredentials(userName!, password), userName, false);
        }

        var sameTarget = stored is { AuthMode: SourceAuthMode.UserPassword, HasPassword: true }
                         && string.Equals(stored.UserName, userName, StringComparison.Ordinal)
                         && DataSourceAddress.SameTarget(
                             stored.Transport, stored.Endpoint, stored.SecondaryEndpoint, form.Transport, endpoint, spare);

        if (!sameTarget)
        {
            throw Invalid(DataSourceCredentialRules.PasswordReentryRequiredKey, "password");
        }

        // ⛔ R-03: збережений пароль уже відхилено — до драйвера не йдемо й не розшифровуємо.
        if (stored!.CredentialsRejectedAt is not null)
        {
            return (null, userName, true);
        }

        return ((credentials ?? throw new CredentialsUnreadableException(stored.Code)).Resolve(stored), userName, false);
    }

    private async Task<DataSourceTestResult> ProbeAsync(
        DataSourceFormTest form, string code, string endpoint, string? spare, ResolvedCredentials? resolved,
        string target, CancellationToken ct)
    {
        var probe = probes.FirstOrDefault(p => p.Transport == form.Transport);
        if (probe is null)
        {
            return TestDataSourceConnectionHandler.Failed(TestDataSourceConnectionHandler.FailureAdapterNotRegistered);
        }

        // Ефемерна сутність: не додається в контекст і не зберігається.
        var spec = new DataSource(
            EcrCode.Create(code), new LocalizedText(new Dictionary<string, string> { ["en"] = code }),
            form.Transport, endpoint, SaveDataSourceHandler.SecretNamePrefix + code);
        spec.Configure(spare, string.IsNullOrWhiteSpace(form.Catalog) ? null : form.Catalog.Trim(), 1);

        try
        {
            await probe.ProbeAsync(spec, resolved, ct).ConfigureAwait(false);
            lockout.RecordSuccess(target);

            return new DataSourceTestResult(true, null, 0);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Відмова джерела — це й є відповідь перевірки.
        catch (Exception e)
#pragma warning restore CA1031
        {
            var category = TestDataSourceConnectionHandler.Classify(e);

            if (category == TestDataSourceConnectionHandler.FailureAuth)
            {
                lockout.RecordFailure(target, clock.UtcNow);
            }

            TestDataSourceConnectionHandler.LogProbeFailed(
                logger, code, category, SafeLogException.Of(e, null, resolved?.Password.Reveal(), resolved?.UserName));

            return TestDataSourceConnectionHandler.Failed(category);
        }
    }

    /// <summary>Відбиток цілі (транспорт, канонічні адреси, логін) — без самих значень у пам'яті воріт.</summary>
    private static string Fingerprint(ExternalTransport transport, string endpoint, string? spare, string? userName)
    {
        var text = string.Join(
            "\u001f", transport.ToString(), DataSourceAddress.Canonical(transport, endpoint),
            DataSourceAddress.Canonical(transport, spare), userName);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static BusinessRuleException Invalid(string messageKey, string field)
        => new(
            ErrorCodes.RequestInvalid, "Облікові дані перевірки недопустимі або пароль треба ввести заново.",
            new Dictionary<string, object?> { ["messageKey"] = messageKey, ["field"] = field });
}
