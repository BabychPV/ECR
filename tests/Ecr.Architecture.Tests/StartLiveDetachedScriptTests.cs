// tests/Ecr.Architecture.Tests/StartLiveDetachedScriptTests.cs
using System.Diagnostics;
using System.Text;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// b14 source-test-flert, AC-4: <c>tools/live/Start-LiveDetached.ps1</c> доводить, що живий Api пише серверний
/// журнал, — за рядком-маркером СВОГО процесу (<c>(pid=&lt;PID&gt;).</c>, <c>FileLog.ReportFileLog</c>), прочитаним
/// із самого файлу, а не з переліку каталогу.
/// </summary>
/// <remarks>
/// ⛔ Причина (копія live EcrLandB14Flert, 25.11): Api, піднятий detached через WMI, писав у
/// <c>%ProgramData%\ECR\logs\ecr-20261125_015.log</c>, а <c>Get-ChildItem</c> показував розмір і час запису від
/// сіду (200647 Б / 16:32:47 проти справжніх 248114 Б) — Windows не оновлює їх у переліку, поки файл відкритий.
/// Тому тест тримає файл ВІДКРИТИМ на запис, як Serilog, і дописує маркер після старту скрипта.
/// Мутаційний доказ (лише цей клас): у скрипті читати <c>Get-Content</c> замість спільного <c>FileStream</c> або
/// шукати маркер без <c>pid=</c> — червоніє відповідний тест.
/// </remarks>
public sealed class StartLiveDetachedScriptTests
{
    private static readonly string Script = Path.Combine(SourceTree.Root, "tools", "live", "Start-LiveDetached.ps1");

    [Fact]
    [Trait("Requirement", "D14-09")]
    public void B14F_07_маркер_свого_процесу_у_відкритому_файлі_дає_OK()
    {
        var directory = Directory.CreateTempSubdirectory("ecr-live-log-").FullName;
        try
        {
            var file = Path.Combine(directory, $"ecr-{DateTime.Now:yyyyMMdd}_015.log");

            // Як Serilog (shared: false): єдиний writer, читачі дозволені; рядок дописується вже ПІСЛЯ старту скрипта.
            using var writer = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.Read | FileShare.Delete);
            Write(writer, "2026-11-25 16:32:47.000 +02:00 [INF] seed line (pid=111).\n");

            var run = Start("-ProcessId", "4242", "-LogDirectory", directory, "-LogLineTimeoutSec", "20");
            Thread.Sleep(1500);
            Write(writer, "2026-11-25 16:32:56.000 +02:00 [INF] Ecr.Api.Observability.FileLog: Файловий журнал пише в "
                          + directory + " (pid=4242).\n");

            var (exit, output) = Finish(run);
            Assert.True(exit == 0, output);
            Assert.Contains("OK: API pid 4242", output, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    [Trait("Requirement", "D14-09")]
    public void B14F_08_рядок_чужого_процесу_чи_його_відсутність_FAIL_за_тайм_аут()
    {
        var directory = Directory.CreateTempSubdirectory("ecr-live-log-").FullName;
        try
        {
            // Свіжий файл від ІНШОГО процесу (сід, setup) — не доказ: маркер чужого pid і «pid=42420» не рахуються.
            File.WriteAllText(
                Path.Combine(directory, $"ecr-{DateTime.Now:yyyyMMdd}.log"),
                "x (pid=111).\nx (pid=42420).\n",
                new UTF8Encoding(false));

            var (exit, output) = Finish(Start("-ProcessId", "4242", "-LogDirectory", directory, "-LogLineTimeoutSec", "2"));

            Assert.Equal(1, exit);
            Assert.Contains("FAIL: no server log line '(pid=4242).'", output, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    private static void Write(FileStream stream, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
    }

    private static Process Start(params string[] args)
    {
        var start = new ProcessStartInfo(DeployScriptHarness.FindPowerShell())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script }
                     .Concat(args))
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start) ?? throw new InvalidOperationException("PowerShell не запустився");
    }

    private static (int Exit, string Output) Finish(Process process)
    {
        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromMinutes(1)))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("PowerShell не завершився за 1 хв");
            }

            return (process.ExitCode, stdout.Result + stderr.Result);
        }
    }

    private static void TryDelete(string directory)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(200);
            }
        }
    }
}
