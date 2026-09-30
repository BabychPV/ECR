// src/Ecr.Api/Observability/EventLogSource.cs

using Microsoft.Extensions.Logging.EventLog;

namespace Ecr.Api.Observability;

/// <summary>
/// Джерело журналу подій Windows, під яким пише застосунок (<c>R-03</c>, <c>U15</c>).
/// </summary>
/// <remarks>
/// ⛔ Ім'я — те, яке реєструє MSI (<c>installer/Ecr.Installer/Service.wxs</c>:
/// <c>&lt;util:EventSource Log="Application" Name="ECR"&gt;</c>) і куди посилають
/// <c>tools/deploy-ecr.ps1</c> та runbook. Без явного <see cref="EventLogSettings.SourceName"/>
/// постачальник, якого додає <c>UseWindowsService()</c>, брав ім'я застосунку —
/// <c>Ecr.Api</c>: записи лягали не туди, куди дивиться адміністратор, а обліковий
/// запис служби без прав адміністратора не міг створити нове джерело взагалі —
/// тобто саме збої старту не записувалися нікуди.
///
/// ⚠ Джерело застосунок НЕ створює: це робить MSI під правами адміністратора.
/// Тому ім'я задається тут і звіряється з інсталятором тестом
/// (<c>EventLogSourceTests</c>), а не вгадується.
/// </remarks>
public static class EventLogSource
{
    /// <summary>Ім'я джерела в журналі <c>Application</c>.</summary>
    public const string Name = "ECR";

    /// <summary>Прив'язує постачальника журналу подій до джерела <see cref="Name"/>.</summary>
    /// <param name="services">Контейнер застосунку.</param>
    /// <remarks>
    /// ⚠ Порядок відносно <c>UseWindowsService()</c> не важить: той ставить ім'я
    /// застосунку лише тоді, коли <see cref="EventLogSettings.SourceName"/> ще порожнє.
    ///
    /// ⚠ Лише на Windows: журналу подій поза нею немає, і <see cref="EventLogSettings"/>
    /// там недоступний (CA1416). Поза Windows виклик нічого не реєструє.
    /// </remarks>
    public static IServiceCollection AddEcrEventLogSource(this IServiceCollection services)
    {
        if (OperatingSystem.IsWindows())
        {
            services.Configure<EventLogSettings>(Apply);
        }

        return services;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void Apply(EventLogSettings settings) => settings.SourceName = Name;
}
