// tests/Ecr.Infrastructure.Tests/Jobs/PartitionCheckJobAuditFailureTests.cs
using System.Data.Common;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// L2-05 (аудит 2026-10-03, HU-11 Q8 дефолт A): збій продовження меж аудиту —
/// стан <c>Degraded</c>, а основна перевірка запасу <c>pf_ByPeriodKey</c>
/// виконується завжди.
/// </summary>
/// <remarks>
/// ⚠ Відмову процедури дає не зміна прав у спільній базі колекції, а перехоплювач
/// команд: текст EXEC підмінюється на <c>THROW</c> з номером 229 («EXECUTE
/// permission was denied») — справжній <c>SqlException</c> від сервера, без DDL.
/// Мутаційні докази — в описі коміту.
/// </remarks>
[Collection("SqlServer")]
public sealed class PartitionCheckJobAuditFailureTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "L2-05")]
    public async Task Відмова_продовження_меж_аудиту_не_ховає_перевірку_запасу_pf_ByPeriodKey()
    {
        var (status, details) = await RunAsync("usp_EnsureAuditPartitions");

        Assert.Equal("Degraded", status);
        Assert.Contains("\"boundariesAhead\":", details, StringComparison.Ordinal);
        Assert.Contains("\"auditBoundariesFailed\":true", details, StringComparison.Ordinal);
        Assert.Contains("\"auditBoundariesErrorNumber\":50229", details, StringComparison.Ordinal);

        // ⛔ Текст помилки бази — у журнал, не в DetailsJson (той іде в лист, V-03).
        Assert.DoesNotContain("permission", details, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "L2-05")]
    public async Task Відмова_архівації_аудиту_дає_номер_помилки_без_тексту()
    {
        var (status, details) = await RunAsync("usp_ArchiveAudit");

        Assert.Equal("Degraded", status);
        Assert.Contains("\"auditArchiveFailed\":true", details, StringComparison.Ordinal);
        Assert.Contains("\"auditArchiveErrorNumber\":50229", details, StringComparison.Ordinal);
        Assert.DoesNotContain("permission", details, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "L2-05")]
    public async Task Процедури_обслуговування_йдуть_під_довгим_таймаутом_а_решта_під_звичайним()
    {
        var spy = new TimeoutSpy();
        await RunAsync(null, spy);

        Assert.Equal(ArchiveJob.CommandTimeoutSeconds, spy.TimeoutOf("usp_EnsureAuditPartitions"));
        Assert.Equal(ArchiveJob.CommandTimeoutSeconds, spy.TimeoutOf("usp_ArchiveAudit"));
        Assert.NotEqual(ArchiveJob.CommandTimeoutSeconds, spy.TimeoutOf("pf_ByPeriodKey"));
    }

    private async Task<(string Status, string Details)> RunAsync(string? deniedProcedure, IInterceptor? extra = null)
    {
        var started = DateTime.UtcNow;
        var builder = new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.CommandTimeout(60));
        var interceptors = new List<IInterceptor>();
        if (deniedProcedure is not null)
        {
            interceptors.Add(new DenyProcedure(deniedProcedure));
        }

        if (extra is not null)
        {
            interceptors.Add(extra);
        }

        await using var db = new EcrDbContext(builder.AddInterceptors(interceptors).Options);

        // 2026-01: попереду багато зашитих меж pf_ByPeriodKey — без збою аудиту це Succeeded.
        var job = new PartitionCheckJob(
            db, Substitute.For<ISqlCapabilities>(), new TestClock(new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc)));
        await job.ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);

        await using var reader = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .Options);

        var run = await reader.MaintenanceRuns
            .Where(r => r.JobCode == PartitionCheckJob.Code)
            .OrderByDescending(r => r.Id)
            .FirstAsync(CancellationToken.None);

        return (run.Status, run.DetailsJson ?? string.Empty);
    }

    /// <summary>Відмова процедури справжньою помилкою сервера.</summary>
    private sealed class DenyProcedure(string procedure) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(procedure, StringComparison.Ordinal))
            {
                command.CommandText =
                    "THROW 50229, N'The EXECUTE permission was denied on the object, schema arc.', 1;";
            }

            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>Таймаут, з яким пішла кожна команда.</summary>
    private sealed class TimeoutSpy : DbCommandInterceptor
    {
        private readonly List<(string Text, int Timeout)> seen = [];

        public int TimeoutOf(string fragment)
        {
            lock (seen)
            {
                return seen.Last(s => s.Text.Contains(fragment, StringComparison.Ordinal)).Timeout;
            }
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Record(DbCommand command)
        {
            lock (seen)
            {
                seen.Add((command.CommandText, command.CommandTimeout));
            }
        }
    }
}
