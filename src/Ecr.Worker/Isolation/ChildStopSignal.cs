// src/Ecr.Worker/Isolation/ChildStopSignal.cs

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Ecr.Worker.Isolation;

/// <summary>
/// М'який сигнал зупинки від наглядача дочірнім процесам (L2-09): іменована подія,
/// ім'я якої діти отримують через успадковане оточення.
/// </summary>
/// <remarks>
/// ⛔ Діти без консолі (<c>CREATE_NO_WINDOW</c>), і Ctrl+C до них не дійде. Без сигналу
/// закриття Job Object убивало їх посеред задачі: рядок черги стояв <c>Running</c> до
/// спливу оренди і переклеймлювався з <c>ReclaimCount + 1</c> — чотири рестарти служби
/// закривали невинну задачу <c>Failed jobs.leaseLostTooOften</c>. Із сигналом дочірній
/// хост зупиняється штатно, і <c>JobWorker</c> повертає задачу в чергу (<c>ReleaseAsync</c>).
/// </remarks>
public static class ChildStopSignal
{
    /// <summary>Ключ конфігурації дочірнього з ім'ям події.</summary>
    public const string ConfigKey = WorkerPoolOptions.SectionName + ":StopEvent";

    /// <summary>Змінна оточення, через яку ім'я події доходить до дітей (префікс <c>ECR_</c>).</summary>
    public const string EnvironmentVariable = "ECR_Jobs__Workers__StopEvent";

    /// <summary>
    /// Створює подію і публікує її ім'я в оточенні процесу: діти, запущені після цього,
    /// його успадковують.
    /// </summary>
    /// <returns>Подія; <c>Set</c> просить дітей зупинитися.</returns>
    public static EventWaitHandle CreateForChildren()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(JobObject.UnsupportedMessage);
        }

        var name = $@"Local\EcrWorker-stop-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var signal = new EventWaitHandle(false, EventResetMode.ManualReset, name);
        Environment.SetEnvironmentVariable(EnvironmentVariable, name);
        return signal;
    }

    /// <summary>Прибирає ім'я події з оточення: діти, запущені пізніше, на неї не чекають.</summary>
    public static void Forget() => Environment.SetEnvironmentVariable(EnvironmentVariable, null);
}

/// <summary>
/// Дочірній: чекає на подію наглядача (<see cref="ChildStopSignal"/>) і зупиняє хост штатно.
/// </summary>
/// <param name="configuration">Конфігурація процесу (ім'я події — <see cref="ChildStopSignal.ConfigKey"/>).</param>
/// <param name="lifetime">Життя хоста.</param>
internal sealed class ChildStopListener(IConfiguration configuration, IHostApplicationLifetime lifetime) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var name = configuration[ChildStopSignal.ConfigKey];
        if (string.IsNullOrEmpty(name) || !OperatingSystem.IsWindows()
            || !EventWaitHandle.TryOpenExisting(name, out var signal))
        {
            return;
        }

        using (signal)
        {
            var stopRequested = await Task.Run(
                    () => WaitHandle.WaitAny([signal, stoppingToken.WaitHandle]) == 0,
                    CancellationToken.None)
                .ConfigureAwait(false);

            if (stopRequested)
            {
                lifetime.StopApplication();
            }
        }
    }
}
