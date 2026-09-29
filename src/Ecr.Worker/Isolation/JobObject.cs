// src/Ecr.Worker/Isolation/JobObject.cs

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ecr.Worker.Isolation;

/// <summary>Межі пам'яті Job Object у байтах.</summary>
/// <param name="ProcessMemoryBytes">Стеля кожного процесу (<c>JOB_OBJECT_LIMIT_PROCESS_MEMORY</c>).</param>
/// <param name="JobMemoryBytes">Стеля всього пулу (<c>JOB_OBJECT_LIMIT_JOB_MEMORY</c>).</param>
public sealed record JobObjectLimits(long ProcessMemoryBytes, long JobMemoryBytes);

/// <summary>
/// Windows Job Object пулу воркерів: стеля пам'яті на процес і на пул, і
/// <c>KILL_ON_JOB_CLOSE</c> — смерть власника дескриптора (наглядача) вбиває
/// всіх дітей, без сиріт.
/// </summary>
/// <remarks>
/// ⚠ Стеля пам'яті не «вбиває» процес сама: Windows відмовляє в коміті понад
/// межу, і .NET падає з <see cref="OutOfMemoryException"/>. Для воркера це
/// рівнозначно — процес, що пішов за межу, завершується, сусіди живі.
///
/// ⚠ Поза Windows — <see cref="PlatformNotSupportedException"/> з поясненням:
/// ізольований пул підтримується лише на Windows Server (цільова платформа).
/// </remarks>
public sealed class JobObject : IDisposable
{
    /// <summary>Пояснення відмови поза Windows.</summary>
    public const string UnsupportedMessage =
        "Job Object існує лише у Windows: ізольований пул воркерів (Ecr.Worker --supervisor) "
        + "працює тільки на Windows Server. Поза Windows перерахунок іде в процесі застосунку.";

    private readonly SafeHandle handle;

    private JobObject(SafeHandle handle) => this.handle = handle;

    /// <summary>Створює анонімний Job Object із межами й <c>KILL_ON_JOB_CLOSE</c>.</summary>
    /// <param name="limits">Межі пам'яті.</param>
    /// <exception cref="PlatformNotSupportedException">Не Windows.</exception>
    /// <exception cref="Win32Exception">Відмова kernel32.</exception>
    public static JobObject Create(JobObjectLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.ProcessMemoryBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.JobMemoryBytes, limits.ProcessMemoryBytes);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(UnsupportedMessage);
        }

        var job = NativeMethods.CreateJobObject(0, null);
        if (job.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            job.Dispose();
            throw new Win32Exception(error);
        }

        var information = new NativeMethods.ExtendedLimitInformation
        {
            ProcessMemoryLimit = checked((nuint)limits.ProcessMemoryBytes),
            JobMemoryLimit = checked((nuint)limits.JobMemoryBytes),
        };
        information.BasicLimitInformation.LimitFlags =
            NativeMethods.LimitProcessMemory | NativeMethods.LimitJobMemory | NativeMethods.LimitKillOnJobClose;

        if (!NativeMethods.SetInformationJobObject(
                job,
                NativeMethods.ExtendedLimitInformationClass,
                ref information,
                (uint)Marshal.SizeOf<NativeMethods.ExtendedLimitInformation>()))
        {
            var error = Marshal.GetLastPInvokeError();
            job.Dispose();
            throw new Win32Exception(error);
        }

        return new JobObject(job);
    }

    /// <summary>Додає запущений процес у Job Object.</summary>
    /// <param name="process">Запущений процес.</param>
    /// <exception cref="Win32Exception">Відмова kernel32 (наприклад, процес уже завершився).</exception>
    public void Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        ObjectDisposedException.ThrowIf(handle.IsClosed, this);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(UnsupportedMessage);
        }

        if (!NativeMethods.AssignProcessToJobObject((SafeJobHandle)handle, process.SafeHandle))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    /// <summary>Закриває дескриптор; через <c>KILL_ON_JOB_CLOSE</c> це вбиває всі процеси пулу.</summary>
    public void Dispose() => handle.Dispose();
}
