// tests/Ecr.Worker.Tests/JobObjectStartTests.cs

using System.Diagnostics;
using Ecr.TestKit;
using Ecr.Worker.Isolation;
using Xunit;
using static Ecr.Worker.Tests.WorkerProcess;

namespace Ecr.Worker.Tests;

/// <summary>
/// ФВ-9.8 (D-206, I1): дочірній процес НІКОЛИ не виконується поза Job Object —
/// народжується призупиненим, додається, лише тоді відпускається.
/// </summary>
/// <remarks>
/// ⚠ Поза Windows — зрозуміла відмова (як у P1), а не порожній прохід.
/// Мутації: <c>ResumeThread</c> до <c>AssignProcessToJobObject</c> (гачок бачить
/// процес поза Job Object) і без <c>CREATE_SUSPENDED</c> (потік не призупинений) —
/// обидві червоні.
/// </remarks>
[Collection(WorkerProcessSerial.Name)]
public sealed class JobObjectStartTests
{
    /// <summary>
    /// ⚠ Початковий потік щойно створеного процесу ядро переводить у Wait/Suspended не
    /// миттєво: перші мілісекунди він Initialized/Ready (на CI це давало хибне «уже
    /// виконувався»). Чекаємо стан зі свіжим знімком; потік, що справді біжить
    /// (мутація без CREATE_SUSPENDED), у Suspended не потрапить — це таймаут.
    /// </summary>
    private static bool WaitAllSuspended(Process p, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            p.Refresh();
            var threads = p.Threads.Cast<ProcessThread>().ToList();
            if (threads.Count > 0 && threads.All(t =>
                    t.ThreadState == System.Diagnostics.ThreadState.Wait && t.WaitReason == ThreadWaitReason.Suspended))
            {
                return true;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            Thread.Sleep(10);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-9.8")]
    public void Процес_уже_в_Job_Object_і_ще_призупинений_до_ResumeThread()
    {
        var limits = new JobObjectLimits(256 * Megabyte, 512 * Megabyte);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Throws<PlatformNotSupportedException>(() => JobObject.Create(limits));
            return;
        }

        using var job = JobObject.Create(limits);
        Process? process = null;
        bool? inJobBeforeResume = null;
        bool? suspendedBeforeResume = null;
        try
        {
            process = job.Start(Command("--child", "--stub"), p =>
            {
                inJobBeforeResume = job.Contains(p);
                suspendedBeforeResume = WaitAllSuspended(p, TimeSpan.FromSeconds(10));
            });

            // ⛔ Головне твердження: на мить відпуску процес уже в Job Object.
            Assert.True(inJobBeforeResume, "до ResumeThread процес був поза Job Object");
            Assert.True(suspendedBeforeResume, "до ResumeThread процес уже виконувався");

            // Відпущений — працює, і далі в Job Object.
            Assert.True(job.Contains(process));
            Assert.False(process.WaitForExit(1_000), "дочірній мав працювати після ResumeThread");
        }
        finally
        {
            Kill(process);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-9.8")]
    public void Відмова_гачка_знищує_призупинений_процес_без_сироти()
    {
        var limits = new JobObjectLimits(256 * Megabyte, 512 * Megabyte);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Throws<PlatformNotSupportedException>(() => JobObject.Create(limits));
            return;
        }

        using var job = JobObject.Create(limits);
        var pid = 0;
        try
        {
            Assert.Throws<InvalidOperationException>(() => job.Start(Command("--child", "--stub"), p =>
            {
                pid = p.Id;
                throw new InvalidOperationException("відмова між додаванням і відпуском");
            }));

            Assert.NotEqual(0, pid);
            Assert.True(WaitGone(pid, TimeSpan.FromSeconds(5)), "призупинений процес пережив відмову запуску");
        }
        finally
        {
            Kill(pid);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-9.8")]
    public void Відпущений_процес_отримує_свої_аргументи_і_завершується_власним_кодом()
    {
        var limits = new JobObjectLimits(256 * Megabyte, 512 * Megabyte);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Throws<PlatformNotSupportedException>(() => JobObject.Create(limits));
            return;
        }

        using var job = JobObject.Create(limits);
        Process? process = null;
        try
        {
            // ⛔ Командний рядок збирає сам CreateProcess-шлях: аргумент, що загубився
            // чи злипся з сусіднім, дав би код використання (2), а не 7.
            process = job.Start(Command("--child", "--exit-after-ms", "100", "--exit-code", "7"));

            Assert.True(process.WaitForExit(30_000), "дочірній не завершився сам");
            Assert.Equal(7, process.ExitCode);
        }
        finally
        {
            Kill(process);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-9.8")]
    public void Неіснуючий_файл_і_закритий_Job_Object_дають_відмову_а_не_процес()
    {
        var limits = new JobObjectLimits(256 * Megabyte, 512 * Megabyte);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Throws<PlatformNotSupportedException>(() => JobObject.Create(limits));
            return;
        }

        var job = JobObject.Create(limits);
        var missing = new ChildCommand(Path.Combine(AppContext.BaseDirectory, "немає-такого.exe"), ["--child"]);
        Assert.Throws<System.ComponentModel.Win32Exception>(() => job.Start(missing));

        job.Dispose();
        Assert.Throws<ObjectDisposedException>(() => job.Start(Command("--child", "--stub")));
    }

    [Theory]
    [InlineData(new[] { @"C:\Program Files\ECR\Ecr.Worker.exe", "--child" }, "\"C:\\Program Files\\ECR\\Ecr.Worker.exe\" --child")]
    [InlineData(new[] { "a.exe", "" }, "a.exe \"\"")]
    [InlineData(new[] { "a.exe", "say \"hi\"" }, "a.exe \"say \\\"hi\\\"\"")]
    [InlineData(new[] { "a.exe", @"C:\dir with space\" }, "a.exe \"C:\\dir with space\\\\\"")]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Командний_рядок_зберігає_кожен_аргумент_окремо(string[] parts, string expected)
        => Assert.Equal(expected, JobObject.CommandLine(new ChildCommand(parts[0], parts[1..])));
}
