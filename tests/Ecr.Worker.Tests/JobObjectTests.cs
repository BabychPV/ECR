// tests/Ecr.Worker.Tests/JobObjectTests.cs

using System.Diagnostics;
using Ecr.TestKit;
using Ecr.Worker.Isolation;
using Xunit;
using static Ecr.Worker.Tests.WorkerProcess;

namespace Ecr.Worker.Tests;

/// <summary>
/// ФВ-9.8 (D-206, P1): межі пам'яті Job Object діють на справжні процеси
/// <c>Ecr.Worker --child</c>.
/// </summary>
/// <remarks>
/// ⚠ Job Object існує лише у Windows. На Linux-CI (усі гейти на
/// <c>ubuntu-latest</c>) ці ж тести перевіряють інше твердження — зрозумілу
/// відмову <see cref="PlatformNotSupportedException"/>, — а не проходять порожньо.
/// </remarks>
[Collection(WorkerProcessSerial.Name)]
public sealed class JobObjectTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-9.8")]
    public void Процес_що_комітить_удвічі_більше_межі_завершується_а_сусід_живий()
    {
        var limits = new JobObjectLimits(128 * Megabyte, 1024 * Megabyte);
        if (!OperatingSystem.IsWindows())
        {
            var refusal = Assert.Throws<PlatformNotSupportedException>(() => JobObject.Create(limits));
            Assert.Contains("Windows", refusal.Message, StringComparison.Ordinal);
            return;
        }

        Process? eater = null;
        Process? idle = null;
        using var job = JobObject.Create(limits);
        try
        {
            eater = Start(Command("--child", "--eat-mb", "256"));
            job.Assign(eater);
            idle = Start(Command("--child", "--stub"));
            job.Assign(idle);

            // ⛔ Без SetInformationJobObject 256 МБ комітяться спокійно і
            // процес живе далі — тоді тут червоне.
            Assert.True(eater.WaitForExit(30_000), "процес, що комітить 2× межі, мав завершитися");
            Assert.Equal(ChildStub.ExitOutOfMemory, eater.ExitCode);
            Assert.False(idle.HasExited, "сусід у тому ж Job Object мав лишитися живим");
        }
        finally
        {
            Kill(eater);
            Kill(idle);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-9.8")]
    public void Стеля_пулу_обмежує_сумарну_пам_ять_навіть_коли_кожен_процес_у_своїй_межі()
    {
        // Кожен комітить ~150 МБ (+ ~30–40 МБ самого .NET) < 256 на процес,
        // разом > 320 на пул.
        var limits = new JobObjectLimits(256 * Megabyte, 320 * Megabyte);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Throws<PlatformNotSupportedException>(() => JobObject.Create(limits));
            return;
        }

        Process? first = null;
        Process? second = null;
        using var job = JobObject.Create(limits);
        try
        {
            // ⚠ Послідовно, а не разом. Коли обидва їли одночасно, стелю пулу міг
            // зачепити будь-який коміт будь-якого з двох — і не лише масив у
            // EatOrExit, а й сторінку стека чи службову пам'ять рантайму. Тоді
            // процес падав не з ExitOutOfMemory, а з STATUS_STACK_OVERFLOW
            // (-1073741571 = 0xC00000FD; CI worker (windows), runs 36661226035,
            // 36663774685). Перший спершу досягає свого обсягу й затихає;
            // понад стелю тоді виходить лише другий — і саме на масиві.
            first = job.Start(Command("--child", "--eat-mb", "150"));
            WaitSettled(first, 150 * Megabyte);

            second = job.Start(Command("--child", "--eat-mb", "150"));

            // ⛔ Без JOB_OBJECT_LIMIT_JOB_MEMORY другий теж живе (150 + рантайм < 256
            // на процес) — тоді тут червоне.
            Assert.True(second.WaitForExit(30_000), "другий процес мав упертися в стелю пулу");
            Assert.Equal(ChildStub.ExitOutOfMemory, second.ExitCode);
            Assert.False(first.HasExited, "перший у своїй межі й під стелею пулу мав лишитися живим");
        }
        finally
        {
            Kill(first);
            Kill(second);
        }
    }

    /// <summary>
    /// Чекає, доки процес закомітить щонайменше <paramref name="bytes"/> і перестане
    /// рости (два заміри поспіль без приросту понад 1 МБ).
    /// </summary>
    private static void WaitSettled(Process process, long bytes)
    {
        var deadline = Stopwatch.StartNew();
        var previous = -1L;
        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            Assert.False(process.HasExited, $"процес завершився до стелі пулу з кодом {(process.HasExited ? process.ExitCode : 0)}");
            process.Refresh();
            var now = process.PrivateMemorySize64;
            if (now >= bytes && now - previous < Megabyte)
            {
                return;
            }

            previous = now;
            Thread.Sleep(200);
        }

        Assert.Fail($"процес не закомітив {bytes / Megabyte} МБ за 30 с");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-9.8")]
    public void Межі_перевіряються_до_виклику_kernel32()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => JobObject.Create(new JobObjectLimits(0, Megabyte)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => JobObject.Create(new JobObjectLimits(2 * Megabyte, Megabyte)));
    }
}

/// <summary>Тести, що запускають процеси, — послідовно: межі пам'яті міряються без сусідів.</summary>
[CollectionDefinition(Name)]
public sealed class WorkerProcessSerial
{
    public const string Name = "WorkerProcesses";
}
