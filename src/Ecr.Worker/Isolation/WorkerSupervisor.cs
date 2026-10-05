// src/Ecr.Worker/Isolation/WorkerSupervisor.cs

using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Ecr.Worker.Isolation;

/// <summary>Відступ перед перезапуском: подвоюється на кожне падіння поспіль.</summary>
/// <param name="Initial">Перший відступ.</param>
/// <param name="Max">Стеля відступу.</param>
/// <param name="StableAfter">Прожив стільки — лічильник падінь скидається.</param>
public sealed record RestartBackoff(TimeSpan Initial, TimeSpan Max, TimeSpan StableAfter)
{
    /// <summary>1 с → 2 → 4 … ≤ 60 с; скидання після хвилини роботи.</summary>
    public static RestartBackoff Default { get; } =
        new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));

    /// <summary>Відступ після <paramref name="consecutiveFailures"/>-го падіння поспіль (від 1).</summary>
    /// <param name="consecutiveFailures">Падінь поспіль.</param>
    public TimeSpan DelayFor(int consecutiveFailures)
    {
        var ticks = Initial.Ticks;
        for (var i = 1; i < consecutiveFailures && ticks < Max.Ticks; i++)
        {
            ticks *= 2;
        }

        return TimeSpan.FromTicks(Math.Min(ticks, Max.Ticks));
    }
}

/// <summary>Подія життя дочірнього процесу.</summary>
/// <param name="slot">Номер місця в пулі.</param>
/// <param name="processId">PID.</param>
/// <param name="exitCode">Код виходу; <c>null</c> для старту.</param>
public sealed class WorkerChildEventArgs(int slot, int processId, int? exitCode) : EventArgs
{
    /// <summary>Номер місця в пулі.</summary>
    public int Slot { get; } = slot;

    /// <summary>PID.</summary>
    public int ProcessId { get; } = processId;

    /// <summary>Код виходу; <c>null</c> для старту.</summary>
    public int? ExitCode { get; } = exitCode;
}

/// <summary>
/// Наглядач пулу: тримає <see cref="WorkerPoolOptions.Count"/> дочірніх процесів
/// в одному <see cref="JobObject"/> і перезапускає впалі з відступом.
/// </summary>
/// <remarks>
/// ⚠ «Зомбі» (живий, але завислий дочірній) у P1 не розпізнається: без черги
/// ознаки життя були б штучні. З I1 ознака — оренда задачі в черзі
/// (<c>LeaseUntil</c>/<c>Renew</c>) і <see cref="WorkerPoolOptions.MaxDuration"/>.
///
/// ⛔ Зупинка — закриттям Job Object (<c>KILL_ON_JOB_CLOSE</c>), а не
/// по-процесним Kill: так само діти помирають, коли сам наглядач убито.
/// ⛔ L2-09: перед тим — м'який сигнал (<see cref="ChildStopSignal"/>) і до
/// <see cref="DefaultShutdownGrace"/> на штатну зупинку: дочірній повертає задачу в
/// чергу без переклейму. Хто не встиг — гине із закриттям Job Object, як і раніше.
/// </remarks>
public sealed partial class WorkerSupervisor(
    WorkerPoolOptions options,
    ChildCommand child,
    ILogger<WorkerSupervisor> logger,
    RestartBackoff? backoff = null,
    TimeProvider? time = null,
    TimeSpan? shutdownGrace = null)
{
    /// <summary>
    /// Скільки чекати штатної зупинки дітей; менше за <c>HostOptions.ShutdownTimeout</c>
    /// (30 с), щоб служба встигла закрити Job Object сама.
    /// </summary>
    public static readonly TimeSpan DefaultShutdownGrace = TimeSpan.FromSeconds(20);

    private const int StartFailedExitCode = -1;
    private const long Megabyte = 1024L * 1024L;

    private readonly RestartBackoff backoff = backoff ?? RestartBackoff.Default;
    private readonly TimeProvider time = time ?? TimeProvider.System;
    private readonly TimeSpan shutdownGrace = shutdownGrace ?? DefaultShutdownGrace;

    /// <summary>Дочірній процес запущено й додано в Job Object.</summary>
    public event EventHandler<WorkerChildEventArgs>? ChildStarted;

    /// <summary>Дочірній процес завершився (сам, від межі пам'яті чи вбитий).</summary>
    public event EventHandler<WorkerChildEventArgs>? ChildExited;

    /// <summary>Тримає пул до скасування; на виході Job Object закривається й убиває дітей.</summary>
    /// <param name="cancellationToken">Сигнал зупинки.</param>
    /// <exception cref="PlatformNotSupportedException">Не Windows.</exception>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var job = JobObject.Create(new JobObjectLimits(
            options.MemoryLimitMb * Megabyte, options.JobMemoryLimitMb * Megabyte));
        LogPoolStarted(logger, options.Count, options.MemoryLimitMb, options.JobMemoryLimitMb);

        using var stop = ChildStopSignal.CreateForChildren();
        using var signalOnCancel = cancellationToken.Register(() => stop.Set());
        try
        {
            var slots = new Task[options.Count];
            for (var slot = 0; slot < slots.Length; slot++)
            {
                slots[slot] = RunSlotAsync(slot, job, cancellationToken);
            }

            await Task.WhenAll(slots).ConfigureAwait(false);
        }
        finally
        {
            ChildStopSignal.Forget();
        }
    }

    private async Task RunSlotAsync(int slot, JobObject job, CancellationToken cancellationToken)
    {
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var started = time.GetTimestamp();
            if (await RunChildOnceAsync(slot, job, cancellationToken).ConfigureAwait(false) is not { } exitCode)
            {
                return;
            }

            failures = time.GetElapsedTime(started) >= backoff.StableAfter ? 1 : failures + 1;
            var delay = backoff.DelayFor(failures);
            LogChildExited(logger, slot, exitCode, delay);

            try
            {
                await Task.Delay(delay, time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <returns>Код виходу; <c>null</c> — скасовано, поки процес жив.</returns>
    private async Task<int?> RunChildOnceAsync(int slot, JobObject job, CancellationToken cancellationToken)
    {
        // ⛔ I1: процес народжується призупиненим і відпускається лише в Job Object
        // (JobObject.Start). Жодної інструкції поза межами пам'яті й без
        // KILL_ON_JOB_CLOSE — вікна «Process.Start → Assign» (борг P1) більше немає.
        Process process;
        try
        {
            process = job.Start(child);
        }
        catch (Win32Exception ex)
        {
            LogStartFailed(logger, ex, slot, child.FileName);
            return StartFailedExitCode;
        }

        using var owned = process;

        LogChildStarted(logger, slot, process.Id);
        ChildStarted?.Invoke(this, new WorkerChildEventArgs(slot, process.Id, null));

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await WaitStoppedAsync(slot, process).ConfigureAwait(false);
            return null;
        }

        ChildExited?.Invoke(this, new WorkerChildEventArgs(slot, process.Id, process.ExitCode));
        return process.ExitCode;
    }

    /// <summary>Дочірньому подано сигнал зупинки: чекаємо штатного виходу до пільги.</summary>
    private async Task WaitStoppedAsync(int slot, Process process)
    {
        using var grace = new CancellationTokenSource(shutdownGrace, time);
        try
        {
            await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            LogChildStopTimedOut(logger, slot, process.Id, shutdownGrace);
            return;
        }

        LogChildStopped(logger, slot, process.Id, process.ExitCode);
        ChildExited?.Invoke(this, new WorkerChildEventArgs(slot, process.Id, process.ExitCode));
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Воркер {Slot} (pid={ProcessId}) зупинився за сигналом, код {ExitCode}")]
    private static partial void LogChildStopped(ILogger logger, int slot, int processId, int exitCode);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Воркер {Slot} (pid={ProcessId}) не зупинився за {Grace}; його завершить закриття Job Object")]
    private static partial void LogChildStopTimedOut(ILogger logger, int slot, int processId, TimeSpan grace);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Пул воркерів: {Count} процесів, межа {MemoryLimitMb} МБ на процес, {JobMemoryLimitMb} МБ на пул")]
    private static partial void LogPoolStarted(ILogger logger, int count, int memoryLimitMb, int jobMemoryLimitMb);

    [LoggerMessage(Level = LogLevel.Information, Message = "Воркер {Slot} запущено, pid={ProcessId}")]
    private static partial void LogChildStarted(ILogger logger, int slot, int processId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Воркер {Slot} завершився з кодом {ExitCode}; перезапуск через {Delay}")]
    private static partial void LogChildExited(ILogger logger, int slot, int exitCode, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Error, Message = "Воркер {Slot} не запущено ({FileName})")]
    private static partial void LogStartFailed(ILogger logger, Exception exception, int slot, string fileName);
}
