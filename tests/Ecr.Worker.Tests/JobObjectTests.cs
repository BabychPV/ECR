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
            idle = Start(Command("--child"));
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
            first = Start(Command("--child", "--eat-mb", "150"));
            job.Assign(first);
            second = Start(Command("--child", "--eat-mb", "150"));
            job.Assign(second);

            // ⛔ Без JOB_OBJECT_LIMIT_JOB_MEMORY обидва живуть.
            var deadline = Stopwatch.StartNew();
            while (!first.HasExited && !second.HasExited && deadline.Elapsed < TimeSpan.FromSeconds(30))
            {
                Thread.Sleep(100);
            }

            var fallen = first.HasExited ? first : second;
            Assert.True(fallen.HasExited, "один із процесів мав упертися в стелю пулу");
            Assert.Equal(ChildStub.ExitOutOfMemory, fallen.ExitCode);
        }
        finally
        {
            Kill(first);
            Kill(second);
        }
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
