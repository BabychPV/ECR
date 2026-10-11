// tests/Ecr.Api.Tests/ReportVersionRaceTests.cs
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Reporting;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Reporting;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Хвіст інтеграції 11.10: два одночасні створення версії звіту з ОДНИМ номером — порушення
/// <c>UQ_ReportVersion</c> віддається клієнтові як 409 <c>ECR-RPT-4091</c>, а не голим 500.
/// </summary>
/// <remarks>
/// ⚠ Гонка відтворена справжнім <see cref="CreateReportVersionHandler"/> на двох контекстах: порт
/// <see cref="IReportDefinitionStore"/> обгорнуто так, що обидві сторони ПРОЧИТАЛИ «такого номера немає»
/// (<c>VersionExistsAsync</c> → <c>false</c>) ДО того, як будь-яка зберігається (<c>Barrier</c>, без
/// <c>Sleep</c>). Через HTTP два <c>POST</c> недетерміновані: другий міг би прийти після першого й чесно
/// отримати відмову обробника «версія вже є» (409 + 409).
/// <para>
/// Мутація: прибрати арм <c>UQ_ReportVersion</c> з <c>ExceptionHandlingMiddleware</c> — відповідь 500,
/// червоні обидва тести.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class ReportVersionRaceTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Порушення_UQ_ReportVersion_віддається_як_409_ECR_RPT_4091_з_ключем_каталогу()
    {
        var body = await ProblemReservedMembersTests.ProblemTextAsync(new DbUpdateException(
            "save failed",
            new InvalidOperationException(
                "Cannot insert duplicate key row in object 'rpt.ReportVersion' with unique index "
                + "'UQ_ReportVersion'. The duplicate key value is (12, 2.0).")));

        var problem = JsonDocument.Parse(body).RootElement;

        Assert.Equal(409, problem.GetProperty("status").GetInt32());
        Assert.Equal("ECR-RPT-4091", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-RPT-4091.versionConcurrent", problem.GetProperty("messageKey").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Два_паралельні_створення_версії_з_одним_номером_дають_одну_версію_і_одне_409()
    {
        var reportDefId = await ArrangeAsync();
        using var barrier = new Barrier(2);

        async Task<Exception?> CreateAsync()
        {
            try
            {
                await using var db = Context();
                var handler = new CreateReportVersionHandler(
                    new RaceStore(new ReportDefinitionStore(db), barrier),
                    new Repository<ReportDef, int>(db),
                    new Repository<ReportVersion, int>(db),
                    new UnitOfWork(db),
                    new SystemClock(),
                    Access(CreateReportVersionHandler.Permission),
                    User());

                await handler.HandleAsync(
                    reportDefId,
                    new CreateReportVersionCommand("2.0", [new ReportColumnCommand("RowKey", "text")], null),
                    CancellationToken.None);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        var results = await Task.WhenAll(
            Task.Run(CreateAsync),
            Task.Run(CreateAsync));

        var failures = results.Where(r => r is not null).ToList();
        Assert.True(
            failures.Count == 1,
            $"очікувалась рівно одна відмова, а їх {failures.Count}: {string.Join(" | ", failures)}");

        await using (var check = Context())
        {
            Assert.Equal(1, await check.ReportVersions.CountAsync(v => v.ReportDefId == reportDefId && v.Version == "2.0"));
        }

        var failure = failures[0]!;
        Assert.IsType<DbUpdateException>(failure);
        Assert.Contains("UQ_ReportVersion", failure.GetBaseException().Message, StringComparison.Ordinal);

        var problem = JsonDocument.Parse(await ProblemReservedMembersTests.ProblemTextAsync(failure)).RootElement;
        Assert.Equal(409, problem.GetProperty("status").GetInt32());
        Assert.Equal("ECR-RPT-4091", problem.GetProperty("errorCode").GetString());
    }

    /// <summary>Опис звіту без версій; повертає його Id.</summary>
    private async Task<int> ArrangeAsync()
    {
        await using var db = Context();
        var tag = Guid.NewGuid().ToString("N")[..12];

        var def = new ReportDef(
            EcrCode.Create($"RVR{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Version race" }),
            isRegulatory: false);
        db.ReportDefs.Add(def);
        await db.SaveChangesAsync();

        return def.Id;
    }

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
        user.CorrelationId.Returns("rpt-version-race");

        return user;
    }

    /// <summary>
    /// Справжнє сховище, у якого перевірка «версія є» відповідає лише ПІСЛЯ того, як її відбули обидві сторони:
    /// обидві бачать <c>false</c> і йдуть у вставку.
    /// </summary>
    private sealed class RaceStore(IReportDefinitionStore inner, Barrier barrier) : IReportDefinitionStore
    {
        public async Task<bool> VersionExistsAsync(int reportDefId, string version, CancellationToken ct)
        {
            var exists = await inner.VersionExistsAsync(reportDefId, version, ct).ConfigureAwait(false);

            // Таймаут бар'єра — не «тест пройшов»: друга сторона не дійшла, гонки не було.
            if (!barrier.SignalAndWait(TimeSpan.FromSeconds(30), ct))
            {
                throw new TimeoutException("друга сторона гонки не дійшла до бар'єра за 30 с");
            }

            return exists;
        }

        public Task<ReportVersionRef?> FindCurrentVersionAsync(string code, CancellationToken ct)
            => inner.FindCurrentVersionAsync(code, ct);

        public Task<IReadOnlyList<ReportDefinitionDto>> ListAsync(CancellationToken ct) => inner.ListAsync(ct);

        public Task<bool> ExistsAsync(string code, CancellationToken ct) => inner.ExistsAsync(code, ct);
    }
}
