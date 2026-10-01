// src/Ecr.Application/Sources/IntegrationConfigAudit.cs
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;

namespace Ecr.Application.Sources;

/// <summary>
/// Журнал змін налаштувань інтеграції: мапінги полів, сутності збору, розклади
/// (<c>ФВ-12.10</c>, <c>S-4</c>) — запис у <c>aud.StructureChange</c> зі станом ДО і ПІСЛЯ.
/// </summary>
/// <remarks>
/// ⚠ Той самий журнал і той самий прийом, що в <see cref="SetSourceEntityRegistryPolicyHandler"/>
/// (<c>TemplateVersionId = 0</c>: зміна не належить версії шаблону). Запис іде В ТІЙ САМІЙ
/// транзакції, що й зміна (<see cref="IUnitOfWork.ExecuteInTransactionAsync"/>): або є обидва,
/// або жодного — журнал не свідчить про зміну, якої не було, і не мовчить про ту, що була.
///
/// ⚠ Судження: <see cref="ChangeClass.Safe"/> для всіх операцій — це налаштування збору, а не
/// структура шаблону, яку класи Guarded/Breaking захищають від зміни під документами.
/// </remarks>
internal static class IntegrationConfigAudit
{
    /// <summary>Тип сутності: мапінг поля джерела.</summary>
    public const string FieldMapType = "ext.EntityFieldMap";

    /// <summary>Тип сутності: сутність збору.</summary>
    public const string SourceEntityType = "ext.SourceEntity";

    /// <summary>Тип сутності: розклад збору.</summary>
    public const string ScheduleType = "ext.CollectionSchedule";

    /// <summary>Тип сутності: мапінг подій джерела (HSE301 A6).</summary>
    public const string EventMapType = "ext.SourceEventMap";

    /// <summary>Тип сутності: прив'язка PI за вікном рядка (HSE301 A1).</summary>
    public const string RowWindowMapType = "ext.RowWindowMap";

    // Web = camelCase, як у відповідях API; перелічення — рядками, щоб журнал читався без довідника значень.
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Тип сутності: з'єднання з джерелом (адреса, транспорт) — SSRF-чутлива конфігурація.</summary>
    public const string DataSourceType = "ext.DataSource";

    /// <summary>
    /// Знімок з'єднання для журналу: код, транспорт, адреси, стеля паралелізму, активність.
    /// ⛔ Секретів немає за побудовою: ні значення, ні навіть ім'я секрету (<c>SecretName</c>).
    /// </summary>
    public static string Snapshot(DataSource source) => JsonSerializer.Serialize(
        new
        {
            source.Code,
            transport = source.Transport,
            endpoint = source.Endpoint,
            secondaryEndpoint = source.SecondaryEndpoint,
            maxParallel = source.MaxParallel,
            isActive = source.IsActive,
        },
        Options);

    /// <summary>Знімок мапінгу для журналу (той самий вигляд, що й у відповіді API).</summary>
    public static string Snapshot(EntityFieldMap map) => JsonSerializer.Serialize(EntityFieldMapLifecycle.Map(map), Options);

    /// <summary>Знімок мапінгу подій для журналу (той самий вигляд, що й у відповіді API).</summary>
    public static string Snapshot(SourceEventMapDto map) => JsonSerializer.Serialize(map, Options);

    /// <summary>Знімок прив'язки вікна рядка для журналу (той самий вигляд, що й у відповіді API).</summary>
    public static string Snapshot(RowWindowMapDto map) => JsonSerializer.Serialize(map, Options);

    /// <summary>Знімок сутності збору для журналу.</summary>
    public static string Snapshot(SourceEntity entity) => JsonSerializer.Serialize(SourceEntityDto.From(entity), Options);

    /// <summary>Знімок розкладу збору для журналу (лише налаштування, без стану запусків).</summary>
    public static string Snapshot(CollectionSchedule schedule) => JsonSerializer.Serialize(
        new
        {
            schedule.SourceEntityId,
            cron = schedule.CronExpression,
            schedule.IsEnabled,
            schedule.LookbackDays,
        },
        Options);

    /// <summary>
    /// Причина запису КОНВЕРТОМ <c>{"k":…,"p":{…}}</c> (той самий кодек, що <c>JobProgressMessageCodec</c>),
    /// а не готовою українською фразою: мова читача журналу в момент запису невідома, і
    /// англійський інтерфейс бачив українську (P3 живого проходу екрана джерел). Клієнт
    /// розгортає ключ <c>integrationAudit.*</c> мовою інтерфейсу (<c>structureChangeReason.ts</c>).
    /// </summary>
    public static string Reason(string key, params (string Name, object? Value)[] parameters)
        => JobProgressMessageCodec.Encode(new JobProgressMessageEnvelope(
            key,
            parameters.Length == 0
                ? null
                : parameters.ToDictionary(
                    p => p.Name,
                    p => Convert.ToString(p.Value, CultureInfo.InvariantCulture) ?? string.Empty,
                    StringComparer.Ordinal)));

    /// <summary>Пише запис журналу; <paramref name="oldJson"/> = <c>null</c> — створення, <paramref name="newJson"/> = <c>null</c> — видалення.</summary>
    public static Task WriteAsync(
        IAuditWriter audit, IClock clock, ICurrentUser currentUser, string entityType, int entityId,
        string operation, string? oldJson, string? newJson, string reason, CancellationToken ct)
        => audit.WriteStructureChangeAsync(
            new StructureChangeRecord(
                ChangedAt: clock.UtcNow,
                TemplateVersionId: 0,
                EntityType: entityType,
                EntityId: entityId,
                ChangeClass: ChangeClass.Safe,
                Operation: operation,
                OldJson: oldJson,
                NewJson: newJson,
                ChangeReason: reason,
                ChangedByUserId: EntityFieldMapLifecycle.RequireUserId(currentUser),
                CorrelationId: currentUser.CorrelationId),
            ct);
}
