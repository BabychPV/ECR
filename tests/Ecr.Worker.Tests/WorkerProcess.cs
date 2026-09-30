// tests/Ecr.Worker.Tests/WorkerProcess.cs

using System.ComponentModel;
using System.Diagnostics;
using Ecr.Worker.Isolation;

namespace Ecr.Worker.Tests;

/// <summary>Запуск справжнього <c>Ecr.Worker</c> з теки тестів і прибирання за собою.</summary>
internal static class WorkerProcess
{
    public const long Megabyte = 1024L * 1024L;

    /// <summary>Команда запуску: apphost, якщо є, інакше <c>dotnet Ecr.Worker.dll</c>.</summary>
    public static ChildCommand Command(params string[] args)
    {
        var directory = AppContext.BaseDirectory;
        var apphost = Path.Combine(directory, OperatingSystem.IsWindows() ? "Ecr.Worker.exe" : "Ecr.Worker");
        if (File.Exists(apphost))
        {
            return new ChildCommand(apphost, args);
        }

        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        return new ChildCommand(dotnet, [Path.Combine(directory, "Ecr.Worker.dll"), .. args]);
    }

    public static Process Start(ChildCommand command, IReadOnlyDictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo(command.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in command.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
        {
            info.Environment[key] = value;
        }

        return Process.Start(info) ?? throw new InvalidOperationException("Процес не запущено.");
    }

    /// <summary>Середовище наглядача з пулом <paramref name="count"/> і невеликими межами.</summary>
    public static Dictionary<string, string> PoolEnvironment(string count) => new()
    {
        ["ECR_Jobs__Workers__Count"] = count,
        ["ECR_Jobs__Workers__MemoryLimitMb"] = "256",
        ["ECR_Jobs__Workers__JobMemoryLimitMb"] = "1024",
    };

    /// <summary>Чекає, доки процес із PID зникне; <c>true</c> — зник.</summary>
    public static bool WaitGone(int processId, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.WaitForExit(timeout);
        }
        catch (ArgumentException)
        {
            // Процесу з таким PID уже немає — саме те, чого чекали.
            return true;
        }
    }

    /// <summary>Прибирання у finally: жодних сиріт після тесту.</summary>
    public static void Kill(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Процес завершився між перевіркою і Kill — прибирати нічого.
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>Прибирання за PID (діти наглядача, яких тест не запускав сам).</summary>
    public static void Kill(int processId)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return;
        }

        Kill(process);
    }
}
