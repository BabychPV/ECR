// src/Ecr.Application/Health/SystemHealthHandlers.cs

using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.PublicApi;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Health;

/// <summary>
/// Факти про піднятий процес для <c>/admin/health</c> (<c>BE-18</c>). Право
/// <c>System.ViewHealth</c>.
/// </summary>
/// <remarks>
/// ⛔ Тут лише те, що процес ЗНАЄ про себе. Жодного «OK»: транспорт сповіщень
/// описується як «налаштований / ні» — рівно тим, що каже
/// <see cref="INotificationSender.IsConfigured"/>, — а не як «працює». Чи
/// доходять листи, звідси не видно, і поле, яке це обіцяло б, брехало б.
///
/// ⚠ <c>LogDirectory</c> — тека, в яку файловий приймач СПРАВДІ пише
/// (<c>D14-09</c>), а не значення з конфігурації: якщо в теку писати не
/// вдалося, поле <c>null</c>, і це правда, яку адміністратор має побачити.
/// </remarks>
public sealed class GetSystemFactsHandler(
    INotificationSender notifications,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    ISystemHealthStore healthStore,
    IUiStringCatalog catalog,
    IClock clock)
{
    /// <summary>Право, без якого факти не віддаються.</summary>
    public const string Permission = "System.ViewHealth";

    /// <summary>Єдиний транспорт, який уміє ця збірка.</summary>
    /// <remarks>
    /// ⚠ Реєстрація в <c>Ecr.Infrastructure.DependencyInjection</c> знає рівно
    /// дві реалізації порту: «не налаштовано» і SMTP. Другий транспорт
    /// (Teams, <c>P-13</c>) мусить принести сюди власну назву — константа
    /// тоді стане неправдою, і це місце, де її шукати.
    /// </remarks>
    public const string SmtpKind = "Smtp";

    /// <summary>Складає відповідь.</summary>
    /// <param name="host">Те, що знає про себе хост.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<SystemFactsResponse> HandleAsync(SystemHostFacts host, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);

        await ListTemplatesHandler
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        // ⚠ Те саме джерело й те саме обрізання, що в анонімному
        // `/public/bootstrap` (`BE-07`): дві «версії продукту» на двох екранах
        // рано чи пізно показали б різне.
        // ⚠ Агрегати БД — кілька запитів у сховищі, без N+1; усе, чого СУБД не
        // віддала, там уже `null`. Мови — той самий порт, що й у `/languages`.
        var snapshot = await healthStore.ReadAsync(clock.UtcNow, ct).ConfigureAwait(false);
        var languages = await catalog.ListLanguagesAsync(ct).ConfigureAwait(false);
        var version = GetPublicBootstrapHandler.MarketingVersion(host.InformationalVersion);

        return new SystemFactsResponse(
            version,
            host.StartedAtUtc,
            host.EnvironmentName,
            TransportOf(notifications.IsConfigured),
            host.LogDirectory,
            new SystemFactsJobsDto(snapshot.Jobs.Running, snapshot.Jobs.Queued, snapshot.Jobs.Failed24h),
            new SystemFactsSourcesDto(snapshot.Sources.Active, snapshot.Sources.Failed, snapshot.Sources.Gaps),
            new SystemFactsDbDto(
                snapshot.FreeSpaceDataDiskGb,
                snapshot.LastBackupAt,
                MaintenanceWindow: null),
            new SystemFactsAboutDto(
                version,
                host.SignInMode,
                [.. languages.Select(l => l.Code)],
                snapshot.LastErrorAt));
    }

    /// <summary>Стан транспорту за відповіддю порту.</summary>
    /// <param name="isConfigured">Що сказав <see cref="INotificationSender.IsConfigured"/>.</param>
    public static NotificationTransportDto TransportOf(bool isConfigured)
        => new(isConfigured, isConfigured ? SmtpKind : null);
}

/// <summary>
/// Текст команди для DBA, яка рухає межі партицій уперед (<c>D15-12</c>).
/// Право <c>System.ViewHealth</c>.
/// </summary>
/// <remarks>
/// ⛔ Застосунок НЕ виконує DDL (<c>D-66</c>) — він лише називає команду. І
/// команда не вигадана: це дослівно крок завдання SQL Agent із
/// <c>14-agent-jobs.sql</c>, тобто виклик <c>arc.usp_EnsurePartitions</c>
/// (<c>04-partition-maintenance.sql</c>). Процедура сама пропускає межі, які
/// вже є, тому текст не залежить від стану бази і його безпечно виконати
/// повторно. Сторож <c>Команда_збігається_з_кроком_завдання_SQL_Agent</c>
/// тримає збіг із розгортанням.
/// </remarks>
public sealed class GetPartitionScriptHandler(IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Команда — та сама, що в кроці завдання SQL Agent.</summary>
    public const string Command = "EXEC arc.usp_EnsurePartitions @MonthsAhead = 6;";

    /// <summary>Повертає готовий текст.</summary>
    /// <param name="ct">Токен скасування.</param>
    public async Task<string> HandleAsync(CancellationToken ct)
    {
        await ListTemplatesHandler
            .RequireAsync(access, currentUser, GetSystemFactsHandler.Permission, ct)
            .ConfigureAwait(false);

        return string.Join(
            '\n',
            "-- ECR: extend the pf_ByPeriodKey partition boundaries ahead of the current period.",
            "-- Run in the ECR database as a principal with ALTER rights on it (D-66: the application never runs DDL).",
            "-- Safe to re-run: boundaries that already exist are skipped.",
            Command,
            string.Empty);
    }
}

/// <summary>Те, що про себе знає ХОСТ (див. <see cref="PublicBootstrapRequest"/>).</summary>
/// <param name="InformationalVersion">Сирий <c>AssemblyInformationalVersion</c> збірки API.</param>
/// <param name="StartedAtUtc">Коли стартував процес, UTC.</param>
/// <param name="EnvironmentName">Ім'я середовища хосту.</param>
/// <param name="LogDirectory">Тека файлового журналу; <c>null</c> — приймач не активний.</param>
/// <param name="SignInMode">
/// Режим входу за зареєстрованими схемами автентифікації: <c>Windows</c>, <c>Local</c>,
/// <c>WindowsAndLocal</c> або <c>None</c> (лише мітка, без деталей схем).
/// </param>
public sealed record SystemHostFacts(
    string? InformationalVersion,
    DateTime StartedAtUtc,
    string EnvironmentName,
    string? LogDirectory,
    string SignInMode = SystemHostFacts.SignInNone)
{
    /// <summary>Жодної схеми входу не зареєстровано.</summary>
    public const string SignInNone = "None";

    /// <summary>Мітка режиму входу за наявністю схем Negotiate і cookie.</summary>
    /// <param name="windows">Чи зареєстрована схема Negotiate.</param>
    /// <param name="local">Чи зареєстрована схема cookie.</param>
    public static string SignInModeOf(bool windows, bool local)
        => (windows, local) switch
        {
            (true, true) => "WindowsAndLocal",
            (true, false) => "Windows",
            (false, true) => "Local",
            _ => SignInNone,
        };
}

/// <summary>Факти про піднятий процес.</summary>
/// <param name="ProductVersion">Версія продукту без метаданих збірки.</param>
/// <param name="StartedAt">Коли стартував процес, UTC.</param>
/// <param name="Environment">Ім'я середовища хосту (<c>Production</c>, <c>Development</c>).</param>
/// <param name="NotificationTransport">Стан транспорту сповіщень.</param>
/// <param name="LogDirectory">Тека файлового журналу; <c>null</c>, коли файл не пишеться.</param>
/// <param name="Jobs">Лічильники черги фонових задач.</param>
/// <param name="Sources">Лічильники джерел збору (лише числа).</param>
/// <param name="Db">Стан БД (число й мітки часу, без шляхів).</param>
/// <param name="About">Блок «Про систему».</param>
public sealed record SystemFactsResponse(
    string ProductVersion,
    DateTime StartedAt,
    string Environment,
    NotificationTransportDto NotificationTransport,
    string? LogDirectory,
    SystemFactsJobsDto Jobs,
    SystemFactsSourcesDto Sources,
    SystemFactsDbDto Db,
    SystemFactsAboutDto About);

/// <summary>Лічильники черги фонових задач.</summary>
/// <param name="Running">Виконуються зараз.</param>
/// <param name="Queued">Стоять у черзі.</param>
/// <param name="Failed24h">Завершились провалом за останню добу.</param>
public sealed record SystemFactsJobsDto(int Running, int Queued, int Failed24h);

/// <summary>Лічильники джерел збору: лише числа, без назв і адрес.</summary>
/// <param name="Active">Налаштованих активних джерел.</param>
/// <param name="Failed">Активних джерел, чий останній запуск збору провалився.</param>
/// <param name="Gaps">Подій покриття за тиждень, де дані за інтервал не перенесено.</param>
public sealed record SystemFactsSourcesDto(int Active, int Failed, int Gaps);

/// <summary>Стан БД: лише число й мітки часу, без шляхів чи імен.</summary>
/// <param name="FreeSpaceDataDiskGb">Вільне місце диска даних, ГБ; <c>null</c> — СУБД не віддала.</param>
/// <param name="LastBackupAt">Остання копія БД, UTC; <c>null</c> — немає копій або права.</param>
/// <param name="MaintenanceWindow">
/// Вікно обслуговування; <c>null</c> — у моделі налаштувань його немає (не вигадується).
/// </param>
public sealed record SystemFactsDbDto(
    long? FreeSpaceDataDiskGb, DateTime? LastBackupAt, string? MaintenanceWindow);

/// <summary>Блок «Про систему».</summary>
/// <param name="Build">Версія збірки без метаданих (як <c>productVersion</c>).</param>
/// <param name="SignInMode">Режим входу: <c>Windows</c>, <c>Local</c>, <c>WindowsAndLocal</c>, <c>None</c>.</param>
/// <param name="Languages">Коди увімкнених мов у порядку показу.</param>
/// <param name="LastErrorAt">Момент останнього провалу задачі чи збору, UTC; <c>null</c> — не було.</param>
public sealed record SystemFactsAboutDto(
    string Build, string SignInMode, IReadOnlyList<string> Languages, DateTime? LastErrorAt);

/// <summary>Транспорт сповіщень: що відомо з конфігурації, не більше.</summary>
/// <param name="IsConfigured">Чи налаштований транспорт.</param>
/// <param name="Kind">Який саме; <c>null</c>, коли не налаштований.</param>
public sealed record NotificationTransportDto(bool IsConfigured, string? Kind);
