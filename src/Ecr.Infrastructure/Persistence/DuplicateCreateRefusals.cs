using Ecr.Application.Errors;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Notifications;
using Ecr.Domain.Entities.Units;
using Ecr.Domain.Errors;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// B158 (лінія c): програш гонки двох однакових запитів створення, що зберігають через <see cref="UnitOfWork"/>, — та
/// сама відмова, що й послідовна перевірка обробника, а не сирий <see cref="DbUpdateException"/> (голий 500).
/// </summary>
/// <remarks>
/// Спільна форма всіх обробників тут — check-then-insert: «чи вже є?» читається без блокування, тож два одночасні
/// запити проходять перевірку обидва, і другого відбиває лише унікальний індекс. Кожен арм — лише СВІЙ індекс (за ім'ям)
/// і лише доданий/змінений рядок СВОГО типу; код, ключ і аргументи — ті самі, що в перевірки обробника.
/// ⚠ Де аргумент послідовної відмови — ключ ПЕРЕМОЖЦЯ (одиниця: <c>{id}</c>; розклад: <c>{scheduleId}</c>), база його не
/// каже: арм дає код і ключ, а обробник, спіймавши саме цю відмову, перечитує переможця й кидає повну
/// (<c>CreateUnitHandler</c>, <c>CreateCollectionScheduleHandler</c>).
/// </remarks>
internal static class DuplicateCreateRefusals
{
    /// <summary>Відмова для програшу гонки на одному з індексів лінії c; <c>null</c> — не наш випадок.</summary>
    public static EcrException? TryMap(DbUpdateException ex)
    {
        // c1. `POST /units` (`CreateUnitHandler`, `FindUnitByCodeAsync`) → `409 ECR-UOM-4091 unitCodeTaken {code}`;
        // `{id}` переможця додає обробник.
        if (SqlConflict.ViolatesIndex(ex, "UQ_Unit_Code")
            && ex.Entries.FirstOrDefault(e => e.State == EntityState.Added && e.Entity is Unit)
                is { Entity: Unit unit })
        {
            return new BusinessRuleException(
                ErrorCodes.UnitCodeTaken,
                $"Одиниця «{unit.Code}» уже існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-UOM-4091.unitCodeTaken",
                    ["code"] = unit.Code,
                });
        }

        // c2. `POST /notifications/channels` і перейменування `PUT …/channels/{id}` (`IsChannelNameTakenAsync`) →
        // `ECR-REQ-0422 notificationChannelNameTaken {name}`.
        if (SqlConflict.ViolatesIndex(ex, "UQ_NotificationChannel_Name")
            && ex.Entries.FirstOrDefault(e =>
                    e.State is EntityState.Added or EntityState.Modified && e.Entity is NotificationChannel)
                is { Entity: NotificationChannel channel })
        {
            return new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Канал «{channel.Name}» уже є.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.notificationChannelNameTaken",
                    ["name"] = channel.Name,
                });
        }

        // c3. `POST /data-sources` (`IsCodeTakenAsync`) → `ECR-REQ-0422 dataSourceCodeTaken {code}`.
        if (SqlConflict.ViolatesIndex(ex, "UQ_DataSource")
            && ex.Entries.FirstOrDefault(e => e.State == EntityState.Added && e.Entity is DataSource)
                is { Entity: DataSource dataSource })
        {
            return new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Джерело з кодом «{dataSource.Code}» уже є.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.dataSourceCodeTaken",
                    ["code"] = dataSource.Code,
                });
        }

        // c5. `POST /collection-schedules` (`entity.ScheduleId` з читання без блокування) і типовий розклад мапінгу подій
        // → `409 ECR-JOB-0409 collectionScheduleExists`; `{scheduleId}` переможця додає обробник.
        if (SqlConflict.ViolatesIndex(ex, "UQ_CollectionSchedule")
            && ex.Entries.FirstOrDefault(e => e.State == EntityState.Added && e.Entity is CollectionSchedule)
                is { Entity: CollectionSchedule schedule })
        {
            return new ConcurrencyConflictException(
                ErrorCodes.JobStateConflict,
                $"Сутність джерела {schedule.SourceEntityId} уже має розклад.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-JOB-0409.collectionScheduleExists",
                });
        }

        return null;
    }
}
