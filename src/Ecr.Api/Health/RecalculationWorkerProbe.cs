using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;

namespace Ecr.Api.Health;

/// <summary>Стан служби <c>EcrWorker</c> на цьому сервері за записом у реєстрі служб.</summary>
public enum WorkerServiceState
{
    /// <summary>Не Windows або реєстр недоступний — невідомо, перевірка не робиться.</summary>
    Unknown,

    /// <summary>Служба не зареєстрована.</summary>
    Missing,

    /// <summary>Зареєстрована, але тип запуску <c>Disabled</c> (<c>Start = 4</c>).</summary>
    Disabled,

    /// <summary>Зареєстрована й не вимкнена.</summary>
    Installed,
}

/// <summary>Черга лейну перерахунку очима перевірки <c>worker</c>.</summary>
/// <param name="Stalled">Задач <c>Queued</c>, доступних довше за межу застою.</param>
/// <param name="LiveLeases">Задач <c>Running</c> із живою орендою — хтось їх виконує.</param>
public sealed record RecalculationQueueState(int Stalled, int LiveLeases);

/// <summary>Звідки перевірка <c>worker</c> бере факти (підміняється в тестах).</summary>
public interface IRecalculationWorkerProbe
{
    /// <summary>Чи зареєстрована служба <c>EcrWorker</c> на цьому сервері.</summary>
    /// <returns>Стан служби.</returns>
    public WorkerServiceState ReadServiceState();

    /// <summary>Застій і живі оренди лейну перерахунку за годинником СУБД.</summary>
    /// <param name="stallAfter">Скільки задача може чекати, перш ніж вважатися застряглою.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Стан черги.</returns>
    public Task<RecalculationQueueState> ReadQueueAsync(TimeSpan stallAfter, CancellationToken ct);
}

/// <summary>Справжні факти: реєстр служб Windows і <c>itg.JobProgress</c>.</summary>
/// <remarks>
/// ⚠ Годинник — СУБД (<c>SYSUTCDATETIME()</c>), як у самій черзі: <c>AvailableAt</c>
/// і <c>LeaseUntil</c> пише SQL Server, і порівнювати їх із годинником Api дало б
/// хибний застій при розбіжності годинників машин.
/// </remarks>
public sealed class RecalculationWorkerProbe(EcrDbContext db) : IRecalculationWorkerProbe
{
    /// <summary>Ключ служби в <c>HKLM</c> — той, що реєструє <c>Worker.wxs</c>.</summary>
    public const string ServiceKey = @"SYSTEM\CurrentControlSet\Services\EcrWorker";

    /// <summary>Значення реєстру служби «тип запуску» (не ключ конфігурації).</summary>
    private const string StartTypeValue = "Start";

    private const int StartDisabled = 4;

    /// <inheritdoc />
    public WorkerServiceState ReadServiceState()
    {
        if (!OperatingSystem.IsWindows())
        {
            return WorkerServiceState.Unknown;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ServiceKey);
            if (key is null)
            {
                return WorkerServiceState.Missing;
            }

            return key.GetValue(StartTypeValue) is int start && start == StartDisabled
                ? WorkerServiceState.Disabled
                : WorkerServiceState.Installed;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return WorkerServiceState.Unknown;
        }
    }

    /// <inheritdoc />
    public async Task<RecalculationQueueState> ReadQueueAsync(TimeSpan stallAfter, CancellationToken ct)
    {
        var stallSeconds = (int)stallAfter.TotalSeconds;

        // Той самий предикат, що й claim (DbJobQueue): Lane, State, AvailableAt —
        // індекс IX_JobProgress_Claim покриває обидва запити.
        var stalled = await db.Database
            .SqlQuery<int>($"""
                SELECT COUNT(*) AS Value FROM itg.JobProgress
                WHERE Lane = {JobLanes.Recalc} AND [State] = 'Queued'
                  AND AvailableAt <= DATEADD(second, -{stallSeconds}, SYSUTCDATETIME())
                """)
            .SingleAsync(ct).ConfigureAwait(false);

        var live = await db.Database
            .SqlQuery<int>($"""
                SELECT COUNT(*) AS Value FROM itg.JobProgress
                WHERE Lane = {JobLanes.Recalc} AND [State] = 'Running'
                  AND LeaseUntil >= SYSUTCDATETIME()
                """)
            .SingleAsync(ct).ConfigureAwait(false);

        return new RecalculationQueueState(stalled, live);
    }
}
