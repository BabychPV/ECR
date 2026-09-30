// src/Ecr.Application/Sources/IntegrationConfigAudit.cs
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

    // Web = camelCase, як у відповідях API; перелічення — рядками, щоб журнал читався без довідника значень.
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Знімок мапінгу для журналу (той самий вигляд, що й у відповіді API).</summary>
    public static string Snapshot(EntityFieldMap map) => JsonSerializer.Serialize(EntityFieldMapLifecycle.Map(map), Options);

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
