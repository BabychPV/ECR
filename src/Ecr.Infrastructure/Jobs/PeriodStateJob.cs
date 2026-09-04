using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Services;
using Ecr.Infrastructure.Persistence;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Переводить періоди між станами і оновлює <c>Project.CurrentPeriod</c>.
/// </summary>
/// <remarks>
/// Стан періоду — **збережене значення**, а не функція від <c>now()</c> у
/// запиті (ФВ-1.12). Інакше кожна перевірка доступу рахувала б offsets, а межа
/// «останнього дня» залежала б від того, о котрій виконано запит.
/// </remarks>
public sealed class PeriodStateJob(
    EcrDbContext db,
    PeriodStateCalculator calculator,
    IClock clock) : IBackgroundJob
{
    /// <inheritdoc />
    public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: для кожного активного проєкту:\n" +
            "1) взяти TimeZoneInfo за Project.TimeZoneId — межі рахуються В ПОЯСІ МАЙДАНЧИКА (D-68);\n" +
            "2) для кожного періоду обчислити цільовий стан calculator.Calculate;\n" +
            "3) змінені стани зберегти пакетно через ExecuteUpdate;\n" +
            "4) якщо CurrentPeriodMode == Auto — оновити CurrentPeriodId через " +
            "   calculator.SelectCurrentPeriod; при Pinned не чіпати;\n" +
            "5) записати MaintenanceRun.\n" +
            "Задача має запускатися за розкладом у поясі майданчика, не о UTC-опівночі.");
}
