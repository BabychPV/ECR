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
/// межу, і .NET кидає <see cref="OutOfMemoryException"/> (заглушка дочірнього
/// виходить із кодом <c>ChildStub.ExitOutOfMemory</c>). Процес, що пішов за
/// межу, завершується, сусіди живі.
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

    /// <summary>
    /// Запускає процес ОДРАЗУ в цьому Job Object: <c>CREATE_SUSPENDED</c> →
    /// <c>AssignProcessToJobObject</c> → <c>ResumeThread</c>.
    /// </summary>
    /// <param name="command">Що запустити.</param>
    /// <returns>Запущений процес, уже в Job Object.</returns>
    /// <exception cref="Win32Exception">Відмова kernel32; процес, якщо встиг з'явитися, знищено.</exception>
    /// <remarks>
    /// ⛔ Закриває вікно P1 «<c>Process.Start</c> → <c>Assign</c>»: між ними процес
    /// уже виконувався поза межами пам'яті й без <c>KILL_ON_JOB_CLOSE</c> — смерть
    /// наглядача в цю мить лишала сироту. Призупинений процес не виконує жодної
    /// інструкції, доки його не додано.
    /// <para>
    /// ⚠ Обрано <c>CREATE_SUSPENDED</c>, а не <c>PROC_THREAD_ATTRIBUTE_JOB_LIST</c>:
    /// гарантія та сама (жодної інструкції поза Job Object), але без
    /// <c>STARTUPINFOEX</c> і списку атрибутів, а відмова додавання — детермінована:
    /// призупинений процес знищується, так і не почавши працювати.
    /// </para>
    /// <para>
    /// Дескриптори й оточення — як у <c>Process.Start</c> без перенаправлення:
    /// успадковуються від наглядача (рядок підключення служби — теж).
    /// </para>
    /// </remarks>
    public Process Start(ChildCommand command) => Start(command, beforeResume: null);

    /// <summary>Чи процес у цьому Job Object (<c>IsProcessInJob</c>).</summary>
    /// <param name="process">Процес.</param>
    public bool Contains(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        ObjectDisposedException.ThrowIf(handle.IsClosed, this);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(UnsupportedMessage);
        }

        return NativeMethods.IsProcessInJob(process.SafeHandle, (SafeJobHandle)handle, out var result)
            ? result
            : throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    /// <summary><see cref="Start(ChildCommand)"/> з гачком між додаванням і відпуском потоку (для тестів).</summary>
    internal unsafe Process Start(ChildCommand command, Action<Process>? beforeResume)
    {
        ArgumentNullException.ThrowIfNull(command);
        ObjectDisposedException.ThrowIf(handle.IsClosed, this);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(UnsupportedMessage);
        }

        var startup = new NativeMethods.StartupInfo
        {
            Size = Marshal.SizeOf<NativeMethods.StartupInfo>(),
            Flags = NativeMethods.StartfUseStdHandles,
            StdInput = NativeMethods.GetStdHandle(NativeMethods.StdInput),
            StdOutput = NativeMethods.GetStdHandle(NativeMethods.StdOutput),
            StdError = NativeMethods.GetStdHandle(NativeMethods.StdError),
        };

        // CreateProcessW може змінювати буфер командного рядка — лише власна копія.
        var line = (CommandLine(command) + '\0').ToCharArray();
        NativeMethods.ProcessInformation info;
        fixed (char* text = line)
        {
            if (!NativeMethods.CreateProcess(
                    null, text, 0, 0, inheritHandles: true,
                    NativeMethods.CreateSuspended | NativeMethods.CreateNoWindow,
                    0, null, ref startup, out info))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }

        Process? process = null;
        try
        {
            if (!NativeMethods.AssignProcessToJobObject((SafeJobHandle)handle, info.Process))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            // ⚠ Власний дескриптор Process відкривається ДО відпуску потоку: поки
            // тримаємо свій, PID не може дістатися іншому процесу.
            process = Process.GetProcessById(info.ProcessId);
            _ = process.SafeHandle;

            beforeResume?.Invoke(process);

            if (NativeMethods.ResumeThread(info.Thread) == -1)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            return process;
        }
        catch
        {
            // ⛔ Процес, що не став (або не відпущений) у Job Object, не живе: він
            // ще не виконав жодної інструкції, і знищити його — без наслідків.
            NativeMethods.TerminateProcess(info.Process, 1);
            process?.Dispose();
            throw;
        }
        finally
        {
            NativeMethods.CloseHandle(info.Thread);
            NativeMethods.CloseHandle(info.Process);
        }
    }

    /// <summary>Командний рядок за правилами <c>CommandLineToArgvW</c>: кожен аргумент — окремо.</summary>
    internal static string CommandLine(ChildCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var line = new System.Text.StringBuilder();
        AppendQuoted(line, command.FileName);
        foreach (var argument in command.Arguments)
        {
            line.Append(' ');
            AppendQuoted(line, argument);
        }

        return line.ToString();
    }

    private static void AppendQuoted(System.Text.StringBuilder line, string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            line.Append(argument);
            return;
        }

        line.Append('"');
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            // Скісні перед лапкою подвоюються, сама лапка екранується.
            line.Append('\\', c == '"' ? (backslashes * 2) + 1 : backslashes);
            backslashes = 0;
            line.Append(c);
        }

        // Скісні перед закривною лапкою — подвоєні, інакше вони її екранують.
        line.Append('\\', backslashes * 2);
        line.Append('"');
    }

    /// <summary>Закриває дескриптор; через <c>KILL_ON_JOB_CLOSE</c> це вбиває всі процеси пулу.</summary>
    public void Dispose() => handle.Dispose();
}
