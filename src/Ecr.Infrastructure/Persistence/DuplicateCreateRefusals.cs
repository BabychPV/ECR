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
/// каже, а шаблон без нього показав би фігурні дужки (сторож <c>MessageKeyRatchetTests</c>). Тому арм дає загальний
/// <c>409 ECR-CELL-0409 concurrentChange</c>, а обробник на цей конфлікт перечитує переможця тією самою перевіркою й кидає
/// повну послідовну відмову (<c>CreateUnitHandler</c>, <c>CreateCollectionScheduleHandler</c>); шлях без такого обробника
/// (типовий розклад мапінгу подій) лишається чесним загальним 409.
/// </remarks>
internal static class DuplicateCreateRefusals
{
    /// <summary>Відмова для програшу гонки на одному з індексів лінії c; <c>null</c> — не наш випадок.</summary>
    public static EcrException? TryMap(DbUpdateException ex)
    {
        // c1. `POST /units` (`CreateUnitHandler`, `FindUnitByCodeAsync`): загальний 409; обробник перечитує переможця і
        // відповідає `409 ECR-UOM-4091 unitCodeTaken {code, id}`.
        if (SqlConflict.ViolatesIndex(ex, "UQ_Unit_Code")
            && ex.Entries.Any(e => e.State == EntityState.Added && e.Entity is Unit))
        {
            return Concurrent("Одиницю з цим кодом щойно завів інший запит.");
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

        // c5. `POST /collection-schedules` (`entity.ScheduleId` з читання без блокування) і типовий розклад мапінгу подій:
        // загальний 409; обробник створення перечитує переможця і відповідає `409 ECR-JOB-0409 collectionScheduleExists
        // {scheduleId}`.
        if (SqlConflict.ViolatesIndex(ex, "UQ_CollectionSchedule")
            && ex.Entries.Any(e => e.State == EntityState.Added && e.Entity is CollectionSchedule))
        {
            return Concurrent("Розклад цієї сутності джерела щойно завів інший запит.");
        }

        return null;
    }

    /// <summary>Загальний конфлікт стану — той самий код і ключ, що й <c>DbUpdateConcurrencyException</c> в одиниці роботи.</summary>
    private static ConcurrencyConflictException Concurrent(string message)
        => new(
            "ECR-CELL-0409",
            message,
            new Dictionary<string, object?> { ["messageKey"] = "err.ECR-CELL-0409.concurrentChange" });
}
