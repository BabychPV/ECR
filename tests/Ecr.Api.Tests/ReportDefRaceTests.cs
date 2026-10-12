// tests/Ecr.Api.Tests/ReportDefRaceTests.cs
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Reporting;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Reporting;
using Ecr.Domain.Errors;
using Ecr.Infrastructure;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Створення опису звіту з кодом, що вже є: і послідовний повтор, і ГОНКА двох створень віддаються клієнтові як
/// 409 <c>ECR-RPT-4091</c> з ключем <c>err.ECR-RPT-4091.code</c> (та сама відмова), а не голим 500 від <c>UQ_ReportDef</c>.
/// </summary>
/// <remarks>
/// ⚠ Гонка відтворена справжнім <see cref="CreateReportDefHandler"/> на двох контекстах: порт
/// <see cref="IReportDefinitionStore"/> обгорнуто так, що обидві сторони ПРОЧИТАЛИ «такого коду немає»
/// (<c>ExistsAsync</c> → <c>false</c>) ДО того, як будь-яка зберігається (<c>Barrier</c>, без <c>Sleep</c>).
/// Мутація: прибрати гілку <c>UQ_ReportDef</c> з <c>UnitOfWork.TryMapDuplicateKey</c> — переможений отримує сирий
/// <c>DbUpdateException</c> (500), червоні обидва інтеграційні тести.
/// </remarks>
[Collection("SqlServer")]
public sealed class ReportDefRaceTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Два_паралельні_створення_опису_з_одним_кодом_дають_один_опис_і_одне_409()
    {
        var code = NewCode();
        using var barrier = new Barrier(2);

        async Task<Exception?> CreateAsync()
        {
            try
            {
                await using var db = Context();
                await Handler(db, new RaceStore(new ReportDefinitionStore(db), barrier))
                    .HandleAsync(Command(code), CancellationToken.None);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        var results = await Task.WhenAll(Task.Run(CreateAsync), Task.Run(CreateAsync));

        var failures = results.Where(r => r is not null).ToList();
        Assert.True(
            failures.Count == 1,
            $"очікувалась рівно одна відмова, а їх {failures.Count}: {string.Join(" | ", failures)}");

        await using (var check = Context())
        {
            Assert.Equal(1, await check.ReportDefs.CountAsync(d => d.Code == code));
        }

        await AssertDuplicateAsync(failures[0]!, code);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Застаріла_перевірка_наявності_не_дає_500_другий_запис_відбиває_індекс()
    {
        // Детермінований варіант гонки без потоків: перевірка «код є» бреше (`false`), як у сторони, що прочитала
        // стан до чужого коміту; відбиває лише `UQ_ReportDef`.
        var code = NewCode();

        await using (var first = Context())
        {
            await Handler(first, new ReportDefinitionStore(first)).HandleAsync(Command(code), CancellationToken.None);
        }

        await using var second = Context();
        var error = await Record.ExceptionAsync(
            () => Handler(second, new StaleStore(new ReportDefinitionStore(second)))
                .HandleAsync(Command(code), CancellationToken.None));

        Assert.NotNull(error);
        await AssertDuplicateAsync(error, code);
    }

    private static async Task AssertDuplicateAsync(Exception failure, string code)
    {
        var rule = Assert.IsType<BusinessRuleException>(failure);
        Assert.Equal(ErrorCodes.ReportDefDuplicate, rule.ErrorCode);
        Assert.Equal("err.ECR-RPT-4091.code", rule.Details!["messageKey"]);
        Assert.Equal(code, rule.Details["code"]);

        var problem = JsonDocument.Parse(await ProblemReservedMembersTests.ProblemTextAsync(failure)).RootElement;
        Assert.Equal(409, problem.GetProperty("status").GetInt32());
        Assert.Equal("ECR-RPT-4091", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-RPT-4091.code", problem.GetProperty("messageKey").GetString());
    }

    private static string NewCode() => $"RDR{Guid.NewGuid():N}"[..15].ToUpperInvariant();

    private static CreateReportDefCommand Command(string code)
        => new(
            code,
            new Dictionary<string, string> { ["en"] = "Def race" },
            IsRegulatory: false,
            Version: "1.0",
            Columns: [new ReportColumnCommand("RowKey", "text")],
            Rules: null);

    private static CreateReportDefHandler Handler(EcrDbContext db, IReportDefinitionStore store)
        => new(
            store,
            new Repository<ReportDef, int>(db),
            new Repository<ReportVersion, int>(db),
            new UnitOfWork(db),
            new SystemClock(),
            Access(CreateReportDefHandler.Permission),
            User());

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static IAccessDecisionService Access(string permission)
    {
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission(permission).Build());

        return access;
    }

    private static ICurrentUser User()
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);
        user.Language.Returns("uk");
        user.CorrelationId.Returns("rpt-def-race");

        return user;
    }

    /// <summary>Справжнє сховище, у якого «код є» відповідає лише ПІСЛЯ того, як її відбули обидві сторони.</summary>
    private sealed class RaceStore(IReportDefinitionStore inner, Barrier barrier) : IReportDefinitionStore
    {
        public async Task<bool> ExistsAsync(string code, CancellationToken ct)
        {
            var exists = await inner.ExistsAsync(code, ct).ConfigureAwait(false);

            // Таймаут бар'єра — не «тест пройшов»: друга сторона не дійшла, гонки не було.
            if (!barrier.SignalAndWait(TimeSpan.FromSeconds(30), ct))
            {
                throw new TimeoutException("друга сторона гонки не дійшла до бар'єра за 30 с");
            }

            return exists;
        }

        public Task<bool> VersionExistsAsync(int reportDefId, string version, CancellationToken ct)
            => inner.VersionExistsAsync(reportDefId, version, ct);

        public Task<ReportVersionRef?> FindCurrentVersionAsync(string code, CancellationToken ct)
            => inner.FindCurrentVersionAsync(code, ct);

        public Task<IReadOnlyList<ReportDefinitionDto>> ListAsync(CancellationToken ct) => inner.ListAsync(ct);
    }

    /// <summary>Сховище, що завжди каже «коду немає» (стан, прочитаний до чужого коміту).</summary>
    private sealed class StaleStore(IReportDefinitionStore inner) : IReportDefinitionStore
    {
        public Task<bool> ExistsAsync(string code, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> VersionExistsAsync(int reportDefId, string version, CancellationToken ct)
            => inner.VersionExistsAsync(reportDefId, version, ct);

        public Task<ReportVersionRef?> FindCurrentVersionAsync(string code, CancellationToken ct)
            => inner.FindCurrentVersionAsync(code, ct);

        public Task<IReadOnlyList<ReportDefinitionDto>> ListAsync(CancellationToken ct) => inner.ListAsync(ct);
    }
}
