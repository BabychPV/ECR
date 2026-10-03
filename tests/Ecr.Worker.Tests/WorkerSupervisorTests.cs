// tests/Ecr.Worker.Tests/WorkerSupervisorTests.cs

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Ecr.Worker.Isolation;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Ecr.Worker.Tests.WorkerProcess;

namespace Ecr.Worker.Tests;

/// <summary>
/// ФВ-9.8 (D-206, P1): наглядач перезапускає впалих дітей із відступом, а його
/// смерть забирає дітей із собою (<c>KILL_ON_JOB_CLOSE</c>).
/// </summary>
[Collection(WorkerProcessSerial.Name)]
public sealed partial class WorkerSupervisorTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-9.8")]
    public async Task Впалий_дочірній_перезапускається_з_відступом_що_зростає()
    {
        var options = new WorkerPoolOptions { Count = 1, MemoryLimitMb = 256, JobMemoryLimitMb = 512 };
        var backoff = new RestartBackoff(
            TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(1200), TimeSpan.FromSeconds(30));
        var supervisor = new WorkerSupervisor(
            options,
            Command("--child", "--exit-after-ms", "200", "--exit-code", "7"),
            NullLogger<WorkerSupervisor>.Instance,
            backoff);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        if (!OperatingSystem.IsWindows())
        {
            await Assert.ThrowsAsync<PlatformNotSupportedException>(() => supervisor.RunAsync(cancellation.Token));
            return;
        }

        var clock = Stopwatch.StartNew();
        var starts = new ConcurrentQueue<(int Pid, TimeSpan At)>();
        var exits = new ConcurrentQueue<(int Code, TimeSpan At)>();
        supervisor.ChildStarted += (_, e) =>
        {
            starts.Enqueue((e.ProcessId, clock.Elapsed));
            if (starts.Count >= 4)
            {
                cancellation.Cancel();
            }
        };
        supervisor.ChildExited += (_, e) => exits.Enqueue((e.ExitCode!.Value, clock.Elapsed));

        try
        {
            await supervisor.RunAsync(cancellation.Token);
        }
        finally
        {
            foreach (var (pid, _) in starts)
            {
                Kill(pid);
            }
        }

        // ⛔ Без перезапуску — рівно один старт.
        var started = starts.ToArray();
        var exited = exits.ToArray();
        Assert.True(started.Length >= 4, $"стартів {started.Length}, очікувалося ≥ 4");
        // ⚠ L2-09: останній дочірній зупиняється за сигналом штатно (код 0) — рахуються падіння до нього.
        Assert.All(exited.Take(3), e => Assert.Equal(7, e.Code));

        // ⛔ Без відступу пауза між падінням і новим стартом — мілісекунди.
        TimeSpan[] expected = [TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(600), TimeSpan.FromMilliseconds(1200)];
        for (var i = 0; i < expected.Length; i++)
        {
            var pause = started[i + 1].At - exited[i].At;
            Assert.True(
                pause >= expected[i] * 0.9,
                $"пауза перед рестартом {i + 1}: {pause.TotalMilliseconds:F0} мс, очікувалося ≥ {expected[i].TotalMilliseconds:F0}");
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-9.8")]
    public void Смерть_процесу_наглядача_вбиває_дітей_не_довше_ніж_за_5_секунд()
    {
        // ⚠ Діти — `--hang`: на м'який сигнал вони не реагують, тож помирають
        // лише від закриття Job Object ядром.
        Process? supervisor = null;
        var children = new ConcurrentDictionary<int, bool>();
        try
        {
            supervisor = Start(Command("--supervisor", "--", "--hang"), PoolEnvironment("2"));
            if (!OperatingSystem.IsWindows())
            {
                Assert.True(supervisor.WaitForExit(30_000));
                Assert.Equal(WorkerProgram.ExitUnsupportedPlatform, supervisor.ExitCode);
                Assert.Contains("Windows", supervisor.StandardError.ReadToEnd(), StringComparison.Ordinal);
                return;
            }

            supervisor.OutputDataReceived += (_, e) =>
            {
                if (e.Data is { } line && ChildPid().Match(line) is { Success: true } match)
                {
                    children.TryAdd(int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), true);
                }
            };
            supervisor.BeginOutputReadLine();

            var waiting = Stopwatch.StartNew();
            while (children.Count < 2 && waiting.Elapsed < TimeSpan.FromSeconds(30))
            {
                Thread.Sleep(100);
            }

            Assert.Equal(2, children.Count);
            Assert.All(children.Keys, pid => Assert.False(WaitGone(pid, TimeSpan.FromMilliseconds(500)), "дитина мала бути живою до вбивства наглядача"));

            supervisor.Kill(entireProcessTree: false);

            // ⚠ Лише з таймаутом: WaitForExit() без нього чекає кінця stdout, а
            // його успадкували діти — під мутацією тест завис би назавжди.
            Assert.True(supervisor.WaitForExit(10_000), "наглядач не помер від Kill");

            // ⛔ Без KILL_ON_JOB_CLOSE діти переживають наглядача — сироти.
            var killed = Stopwatch.StartNew();
            Assert.All(children.Keys, pid => Assert.True(WaitGone(pid, TimeSpan.FromSeconds(5)), $"дитина {pid} пережила наглядача"));
            Assert.True(killed.Elapsed <= TimeSpan.FromSeconds(5), $"діти помирали {killed.Elapsed.TotalSeconds:F1} с");
        }
        finally
        {
            Kill(supervisor);
            foreach (var pid in children.Keys)
            {
                Kill(pid);
            }
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "L2-09")]
    public async Task Зупинка_наглядача_дає_дочірньому_зупинитися_штатно_до_закриття_Job_Object()
    {
        var options = new WorkerPoolOptions { Count = 1, MemoryLimitMb = 256, JobMemoryLimitMb = 512 };
        var supervisor = new WorkerSupervisor(
            options,
            Command("--child", "--exit-after-ms", "600000", "--exit-code", "7"),
            NullLogger<WorkerSupervisor>.Instance,
            shutdownGrace: TimeSpan.FromSeconds(15));

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        if (!OperatingSystem.IsWindows())
        {
            await Assert.ThrowsAsync<PlatformNotSupportedException>(() => supervisor.RunAsync(cancellation.Token));
            return;
        }

        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exits = new ConcurrentQueue<int>();
        supervisor.ChildStarted += (_, e) => started.TrySetResult(e.ProcessId);
        supervisor.ChildExited += (_, e) => exits.Enqueue(e.ExitCode!.Value);

        var run = supervisor.RunAsync(cancellation.Token);
        var pid = 0;
        try
        {
            pid = await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Task.Delay(TimeSpan.FromSeconds(2));
            await cancellation.CancelAsync();
            await run.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            if (pid != 0)
            {
                Kill(pid);
            }
        }

        // ⛔ Без сигналу дочірній гине від закриття Job Object посеред задачі: штатного
        // виходу немає, і задача перерахунку переклеймлюється з ReclaimCount + 1.
        Assert.Equal(0, Assert.Single(exits));
    }

    [GeneratedRegex(@"pid=(\d+)")]
    private static partial Regex ChildPid();
}
