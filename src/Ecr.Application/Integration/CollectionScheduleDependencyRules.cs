// src/Ecr.Application/Integration/CollectionScheduleDependencyRules.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Errors;

namespace Ecr.Application.Integration;

/// <summary>Зміна залежності розкладу у запиті (ФВ-13.15).</summary>
/// <param name="DependsOnScheduleId">Нова залежність; <c>null</c> разом із <paramref name="Clear"/> = <c>false</c> — лишити наявну.</param>
/// <param name="Clear"><c>true</c> — зняти залежність.</param>
public sealed record ScheduleDependencyChange(int? DependsOnScheduleId, bool Clear = false)
{
    /// <summary>Залежність не чіпати.</summary>
    public static ScheduleDependencyChange Unchanged { get; } = new(DependsOnScheduleId: null);
}

/// <summary>Перевірки залежності розкладу від розкладу (ФВ-13.15) — ДО бази.</summary>
internal static class CollectionScheduleDependencyRules
{
    /// <summary>Глибина ланцюга, далі якої обхід вважається циклом: захист від зламаних даних.</summary>
    internal const int MaxChainDepth = 50;

    /// <summary>Перевіряє, що залежність існує, з того ж з'єднання і не замикає цикл.</summary>
    /// <param name="store">Розклади.</param>
    /// <param name="selfId">Розклад, що правиться; <c>null</c> — створюваний (цикл неможливий).</param>
    /// <param name="dataSourceId">З'єднання розкладу, що правиться.</param>
    /// <param name="dependsOnId">Бажана залежність.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="BusinessRuleException">Немає / інше з'єднання / цикл — 422 <c>ECR-REQ-0422</c>.</exception>
    internal static async Task RequireValidAsync(
        ICollectionScheduleStore store, int? selfId, int dataSourceId, int dependsOnId, CancellationToken ct)
    {
        if (selfId == dependsOnId)
        {
            throw Cycle(dependsOnId);
        }

        var target = await store.FindAsync(dependsOnId, ct).ConfigureAwait(false)
                     ?? throw Invalid(
                         $"Розкладу-залежності {dependsOnId} не існує.",
                         "err.ECR-REQ-0422.collectionScheduleDependencyNotFound",
                         dependsOnId);

        if (target.DataSourceId != dataSourceId)
        {
            throw Invalid(
                $"Розклад {dependsOnId} належить іншому джерелу: залежність можлива лише в межах одного.",
                "err.ECR-REQ-0422.collectionScheduleDependencyOtherSource",
                dependsOnId);
        }

        // Йдемо вгору ланцюгом залежностей цілі: дійшли до розкладу, що правиться, — цикл.
        // ⚠ Ланцюг читається СВІЖИМ запитом без відстеження (S-D1): відстежувані сутності, уже завантажені
        // раніше в цьому запиті, не бачать залежності, яку паралельний запит встиг закомітити.
        var visited = 0;
        var next = await store.ReadDependsOnAsync(dependsOnId, ct).ConfigureAwait(false);

        while (next is { } id)
        {
            if (id == selfId || ++visited > MaxChainDepth)
            {
                throw Cycle(dependsOnId);
            }

            next = await store.ReadDependsOnAsync(id, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Конфлікт змін залежностей (409): замок джерела не взято вчасно або зовнішній ключ зачепила паралельна
    /// зміна. Той самий ключ, що й «розклад змінили після читання», — клієнт перечитує й повторює.
    /// </summary>
    internal static ConcurrencyConflictException Conflict(int scheduleId) => new(
        ErrorCodes.JobStateConflict,
        $"Залежності розкладу {scheduleId} змінив паралельний запит; повторіть.",
        new Dictionary<string, object?>
        {
            ["messageKey"] = "err.ECR-JOB-0409.collectionScheduleChanged",
            ["id"] = scheduleId.ToString(CultureInfo.InvariantCulture),
        });

    /// <summary>Виконує зміну; порушення зовнішнього ключа (547) від паралельного видалення чи правки — 409, не 500.</summary>
    internal static async Task RunMappingConflictAsync(ICollectionScheduleStore store, int scheduleId, Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception failure) when (store.IsForeignKeyViolation(failure))
        {
            throw Conflict(scheduleId);
        }
    }

    private static BusinessRuleException Cycle(int dependsOnId) => Invalid(
        $"Залежність від розкладу {dependsOnId} замикає цикл.",
        "err.ECR-REQ-0422.collectionScheduleDependencyCycle",
        dependsOnId);

    private static BusinessRuleException Invalid(string message, string messageKey, int dependsOnId) => new(
        ErrorCodes.RequestInvalid,
        message,
        new Dictionary<string, object?>
        {
            ["messageKey"] = messageKey,
            ["dependsOn"] = dependsOnId.ToString(CultureInfo.InvariantCulture),
        });
}
