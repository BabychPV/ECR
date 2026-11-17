// tests/Ecr.Infrastructure.Tests/Persistence/TemplatePeriodDuplicateTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Periods;
using Ecr.Application.Ports;
using Ecr.Application.Projects;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// B158 (b): програш гонки двох однакових запитів над шаблонами, версіями, структурою версії, політиками й
/// календарем періодів — та сама відмова, що й послідовний шлях (409), або ідемпотентний успіх, а не сирий
/// <c>DbUpdateException</c> (500 <c>ECR-SYS-0500</c>).
/// </summary>
/// <remarks>
/// Детермінований зонд «переможець тримає вставку»: переможець вставляє ключ у відкритій транзакції, переможений
/// іде бойовим шляхом (під RCSI його перевірка «чи є вже» переможця не бачить), блокується на ключі, і лише тоді
/// переможець комітить. Так кожен прогін проходить саме через вікно гонки, без надії на планувальник.
/// </remarks>
[Collection("SqlServer")]
public sealed class TemplatePeriodDuplicateTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 1, 15, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158")]
    public async Task B1_TemplateCodeRace_GivesTemplateCodeTaken()
    {
        var code = Code("B158T");
        try
        {
            await using var loser = Context();
            var thrown = await RaceAsync(
                winner =>
                {
                    winner.Templates.Add(NewTemplate(code));
                    return winner.SaveChangesAsync();
                },
                () => new TemplateVersionStore(loser).CreateTemplateAsync(
                    code, new Dictionary<string, string> { ["en"] = "B158" }, 1, Now, CancellationToken.None));

            var refusal = Assert.IsType<BusinessRuleException>(thrown);
            Assert.Equal("ECR-TMPL-0409", refusal.ErrorCode);
            Assert.Equal("err.ECR-TMPL-0409.templateCodeTaken", refusal.Details!["messageKey"]);
            Assert.Equal(code, refusal.Details["code"]);
        }
        finally
        {
            await CleanupTemplateAsync(code);
        }
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("clone")]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158")]
    public async Task B2_VersionNumberRace_GivesVersionNumberTaken(string path)
    {
        var code = Code("B158N");
        try
        {
            var (templateId, sourceVersionId) = await SeedTemplateAsync(code);

            await using var loser = Context();
            var store = new TemplateVersionStore(loser);
            var thrown = await RaceAsync(
                winner =>
                {
                    winner.TemplateVersions.Add(new TemplateVersion(templateId, "2.0", 1, Now));
                    return winner.SaveChangesAsync();
                },
                () => path == "draft"
                    ? store.CreateDraftAsync(templateId, "2.0", 1, Now, CancellationToken.None)
                    : store.CloneAsync(sourceVersionId, "2.0", 1, Now, CancellationToken.None));

            var refusal = Assert.IsType<BusinessRuleException>(thrown);
            Assert.Equal("ECR-TMPL-0409", refusal.ErrorCode);
            Assert.Equal("err.ECR-TMPL-0409.versionNumberTaken", refusal.Details!["messageKey"]);
            Assert.Equal("2.0", refusal.Details["version"]);
        }
        finally
        {
            await CleanupTemplateAsync(code);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158")]
    public async Task B9_PeriodPolicyRace_GivesPolicyCodeTaken()
    {
        var code = Code("B158P");
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(new AccessBuilder { UserId = 1 }.Permission(CreatePeriodPolicyHandler.Permission).Build());
        try
        {
            await using var loser = Context();
            var handler = new CreatePeriodPolicyHandler(new PeriodStore(loser), access, new TestUser(), new UnitOfWork(loser));
            var thrown = await RaceAsync(
                winner =>
                {
                    winner.PeriodPolicies.Add(new PeriodPolicy(EcrCode.Create(code), 0, 5, 10, 0));
                    return winner.SaveChangesAsync();
                },
                () => handler.HandleAsync(code, 0, 5, 10, 0, CancellationToken.None));

            var refusal = Assert.IsType<BusinessRuleException>(thrown);
            Assert.Equal("ECR-PRD-4091", refusal.ErrorCode);
            Assert.Equal("err.ECR-PRD-4091.code", refusal.Details!["messageKey"]);
            Assert.Equal(code, refusal.Details["code"]);
        }
        finally
        {
            await using var db = Context();
            await db.PeriodPolicies.Where(p => p.Code == code).ExecuteDeleteAsync();
        }
    }

    /// <summary>
    /// b3–b8: гілка «нового» upsert-обробника структури (рішення прийнято до блоку версії) — переможець зберіг,
    /// переможений зберігає той самий ключ.
    /// </summary>
    [Theory]
    [InlineData("sheet")]
    [InlineData("table")]
    [InlineData("column")]
    [InlineData("row")]
    [InlineData("headerField")]
    [InlineData("validationRule")]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158")]
    public async Task B3toB8_StructureUpsertRace_GivesConcurrentChange(string part)
    {
        var code = Code("B158S");
        try
        {
            var (_, versionId) = await SeedTemplateAsync(code);
            var (sheetId, tableId) = await SeedSheetAndTableAsync(versionId);

            await using (var winner = Context())
            {
                winner.Add(NewPart(part, versionId, sheetId, tableId));
                await new UnitOfWork(winner).SaveChangesAsync(CancellationToken.None);
            }

            await using var loser = Context();
            loser.Add(NewPart(part, versionId, sheetId, tableId));

            var thrown = await Assert.ThrowsAsync<ConcurrencyConflictException>(
                () => new UnitOfWork(loser).SaveChangesAsync(CancellationToken.None));

            Assert.Equal("ECR-CELL-0409", thrown.ErrorCode);
            Assert.Equal("err.ECR-CELL-0409.concurrentChange", thrown.Details!["messageKey"]);
        }
        finally
        {
            await CleanupTemplateAsync(code);
        }
    }

    /// <summary>
    /// Справжня паралель (b3): два однакові <c>PUT …/sheets/{code}</c> через обробник, кожен у своєму контексті.
    /// Один створює; другий — або оновлення (якщо прочитав після коміту), або 409 concurrentChange. Жодного 500.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158")]
    public async Task B3_ParallelSaveSheet_NoRawDbError()
    {
        var code = Code("B158R");
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(new AccessBuilder { UserId = 1 }.Permission(SaveSheetDefHandler.Permission).Build());
        try
        {
            var (_, versionId) = await SeedTemplateAsync(code);

            using var barrier = new Barrier(2);
            async Task<Exception?> RunAsync()
            {
                await using var db = Context();
                var handler = new SaveSheetDefHandler(
                    new TemplateVersionStore(db), new ChangeClassifier(), Substitute.For<IMetadataCache>(),
                    Substitute.For<IAuditWriter>(), new UnitOfWork(db), new FixedClock(Now), access, new TestUser());
                await Task.Yield();
                barrier.SignalAndWait();
                try
                {
                    await handler.HandleAsync(
                        versionId, "SP",
                        new SaveSheetDefCommand(new Dictionary<string, string> { ["en"] = "B158" }, null, null, false, true),
                        CancellationToken.None);
                    return null;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            }

            var results = await Task.WhenAll(Task.Run(RunAsync), Task.Run(RunAsync));

            Assert.Contains(results, r => r is null);
            Assert.All(results.Where(r => r is not null), r =>
            {
                var conflict = Assert.IsType<ConcurrencyConflictException>(r);
                Assert.Equal("ECR-CELL-0409", conflict.ErrorCode);
            });

            await using var check = Context();
            Assert.Equal(1, await check.SheetDefs.CountAsync(s => s.TemplateVersionId == versionId && s.Code == "SP"));
        }
        finally
        {
            await CleanupTemplateAsync(code);
        }
    }

    /// <summary>
    /// b10: <c>GET …/periods</c> (добудова календаря) поруч із тим, хто вже тримає блок проєкту й вставляє період, —
    /// чекає, бачить його період і добудовує лише решту. Без блоку — <c>UQ_Period</c>.
    /// </summary>
    [Theory]
    [InlineData("build")]
    [InlineData("activate")]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158")]
    public async Task B10_CalendarRace_IsIdempotentSuccess(string path)
    {
        var code = Code("B158C");
        try
        {
            var (_, versionId) = await SeedTemplateAsync(code);
            var projectId = await SeedProjectAsync(code, versionId);

            await using var loser = Context();
            var thrown = await RaceAsync(
                async winner =>
                {
                    await new PeriodStore(winner).LockProjectCalendarAsync(projectId, CancellationToken.None);
                    winner.Periods.Add(new Period(
                        projectId, new PeriodKey(209901), 1, new DateOnly(2099, 1, 1), new DateOnly(2099, 1, 31)));
                    await winner.SaveChangesAsync();
                },
                () => path == "build"
                    ? BuildHandler(loser, projectId).HandleAsync(projectId, CancellationToken.None)
                    : ActivateHandler(loser, projectId).HandleAsync(projectId, CancellationToken.None));

            Assert.Null(thrown);

            await using var check = Context();
            Assert.Equal(12, await check.Periods.CountAsync(p => p.ProjectId == projectId));
        }
        finally
        {
            await CleanupTemplateAsync(code);
        }
    }

    /// <summary>
    /// b10, страховка: період, записаний повз блок проєкту (переможець уже зберіг той самий ключ), — 409
    /// concurrentChange, а не 500.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158")]
    public async Task B10_PeriodKeyDuplicate_GivesConcurrentChange()
    {
        var code = Code("B158K");
        try
        {
            var (_, versionId) = await SeedTemplateAsync(code);
            var projectId = await SeedProjectAsync(code, versionId);
            static Period January(int projectId)
                => new(projectId, new PeriodKey(209901), 1, new DateOnly(2099, 1, 1), new DateOnly(2099, 1, 31));

            await using (var winner = Context())
            {
                winner.Periods.Add(January(projectId));
                await new UnitOfWork(winner).SaveChangesAsync(CancellationToken.None);
            }

            await using var loser = Context();
            loser.Periods.Add(January(projectId));

            var thrown = await Assert.ThrowsAsync<ConcurrencyConflictException>(
                () => new UnitOfWork(loser).SaveChangesAsync(CancellationToken.None));
            Assert.Equal("err.ECR-CELL-0409.concurrentChange", thrown.Details!["messageKey"]);
        }
        finally
        {
            await CleanupTemplateAsync(code);
        }
    }

    /// <summary>Справжня паралель (b10): два одночасні <c>GET …/periods</c> — обидва успішні, календар один.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158")]
    public async Task B10_ParallelBuildCalendar_BothSucceed()
    {
        var code = Code("B158G");
        try
        {
            var (_, versionId) = await SeedTemplateAsync(code);
            var projectId = await SeedProjectAsync(code, versionId);

            using var barrier = new Barrier(2);
            async Task<(int Created, Exception? Error)> RunAsync()
            {
                await using var db = Context();
                var handler = BuildHandler(db, projectId);
                await Task.Yield();
                barrier.SignalAndWait();
                try
                {
                    return (await handler.HandleAsync(projectId, CancellationToken.None), null);
                }
                catch (Exception ex)
                {
                    return (0, ex);
                }
            }

            var results = await Task.WhenAll(Task.Run(RunAsync), Task.Run(RunAsync));

            Assert.All(results, r => Assert.Null(r.Error));
            Assert.Equal(12, results.Sum(r => r.Created));

            await using var check = Context();
            Assert.Equal(12, await check.Periods.CountAsync(p => p.ProjectId == projectId));
        }
        finally
        {
            await CleanupTemplateAsync(code);
        }
    }

    /// <summary>
    /// Переможець вставляє ключ у відкритій транзакції; переможений іде бойовим шляхом і блокується на ключі; лише
    /// тоді переможець комітить. Повертає виняток переможеного або <c>null</c>.
    /// </summary>
    private async Task<Exception?> RaceAsync(Func<EcrDbContext, Task> winnerInsert, Func<Task> loser)
    {
        await using var winner = Context();
        await using var tx = await winner.Database.BeginTransactionAsync();
        await winnerInsert(winner);
        var spid = (await winner.Database.SqlQuery<int>($"SELECT CAST(@@SPID AS int) AS [Value]").ToListAsync())[0];

        var loserTask = Task.Run(async () =>
        {
            try
            {
                await loser();
                return (Exception?)null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        });

        await WaitUntilBlockedAsync(spid, loserTask);
        await tx.CommitAsync();
        return await loserTask;
    }

    private async Task WaitUntilBlockedAsync(int blockerSpid, Task loser)
    {
        await using var probe = new SqlConnection(sql.ConnectionString);
        await probe.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (loser.IsCompleted)
            {
                // Переможений не дійшов до ключа (відмова раніше) — асерти покажуть, яка саме.
                return;
            }

            await using var command = probe.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id = @b";
            command.Parameters.AddWithValue("@b", blockerSpid);
            if ((int)(await command.ExecuteScalarAsync())! > 0)
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("Переможений так і не заблокувався на ключі переможця.");
    }

    private BuildPeriodCalendarHandler BuildHandler(EcrDbContext db, int projectId)
    {
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(new AccessBuilder { UserId = 1 }
                  .Grant(ResourceKind.Project, projectId, GrantLevel.Write)
                  .Permission("Document.View")
                  .Build());
        var periods = new PeriodStore(db);
        return new BuildPeriodCalendarHandler(
            periods, new UnitOfWork(db), new FixedClock(Now), access, new TestUser(), new PeriodCalendarMaterializer(periods));
    }

    private static ActivateProjectHandler ActivateHandler(EcrDbContext db, int projectId)
    {
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(new AccessBuilder { UserId = 1 }
                  .Grant(ResourceKind.Project, projectId, GrantLevel.Manage)
                  .Permission(ActivateProjectHandler.Permission)
                  .Build());
        var periods = new PeriodStore(db);
        return new ActivateProjectHandler(
            periods, access, new TestUser(), new UnitOfWork(db), new PeriodStateCalculator(), new FixedClock(Now),
            new PeriodCalendarMaterializer(periods));
    }

    private static string Code(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..24].ToUpperInvariant();

    private static LocalizedText Name() => new(new Dictionary<string, string> { ["en"] = "B158" });

    private static Template NewTemplate(string code) => new(EcrCode.Create(code), Name(), 1, Now);

    private static object NewPart(string part, int versionId, int sheetId, int tableId) => part switch
    {
        "sheet" => new SheetDef(versionId, EcrCode.Create("S1"), Name(), 1),
        "table" => new TableDef(sheetId, EcrCode.Create("T1"), Name(), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed),
        "column" => new ColumnDef(tableId, EcrCode.Create("C1"), Name(), 1, CellDataType.Decimal),
        "row" => new RowDef(tableId, RowKey.Create("R1"), 1, Name(), RowKind.Item),
        "headerField" => new HeaderFieldDef(versionId, EcrCode.Create("H1"), Name(), 1, CellDataType.String),
        "validationRule" => new ValidationRule(tableId, EcrCode.Create("V1"), ValidationSeverity.Warning, 0, "1 = 1", Name()),
        _ => throw new ArgumentOutOfRangeException(nameof(part), part, null),
    };

    /// <summary>Шаблон тесту з версією-чернеткою «1.0».</summary>
    private async Task<(int TemplateId, int VersionId)> SeedTemplateAsync(string code)
    {
        await using var db = Context();
        var template = NewTemplate(code);
        db.Templates.Add(template);
        await db.SaveChangesAsync();
        var version = new TemplateVersion(template.Id, "1.0", 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();
        return (template.Id, version.Id);
    }

    private async Task<(int SheetId, int TableId)> SeedSheetAndTableAsync(int versionId)
    {
        await using var db = Context();
        var sheet = new SheetDef(versionId, EcrCode.Create("S0"), Name(), 0);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();
        var table = new TableDef(sheet.Id, EcrCode.Create("T0"), Name(), 0, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync();
        return (sheet.Id, table.Id);
    }

    /// <summary>Проєкт-чернетка на 2099 рік (12 місячних періодів, жодного ще не побудовано).</summary>
    private async Task<int> SeedProjectAsync(string code, int versionId)
    {
        await using var db = Context();
        var policyId = await db.PeriodPolicies.OrderBy(p => p.Id).Select(p => p.Id).FirstAsync();
        var project = new Project(
            EcrCode.Create(code), Name(), new DateOnly(2099, 1, 1), new DateOnly(2099, 12, 31),
            versionId, PeriodKind.Monthly, policyId, "Asia/Atyrau");
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        return project.Id;
    }

    /// <summary>Прибирає шаблон тесту з версіями, структурою, проєктами й періодами.</summary>
    private async Task CleanupTemplateAsync(string code)
    {
        await using var db = Context();
        var templateIds = await db.Templates.Where(t => t.Code == code).Select(t => t.Id).ToListAsync();
        var versionIds = await db.TemplateVersions.Where(v => templateIds.Contains(v.TemplateId)).Select(v => v.Id).ToListAsync();
        var projectIds = await db.Projects.Where(p => versionIds.Contains(p.TemplateVersionId)).Select(p => p.Id).ToListAsync();
        var sheetIds = await db.SheetDefs.Where(s => versionIds.Contains(s.TemplateVersionId)).Select(s => s.Id).ToListAsync();
        var tableIds = await db.TableDefs.Where(t => sheetIds.Contains(t.SheetDefId)).Select(t => t.Id).ToListAsync();

        await db.Periods.Where(p => projectIds.Contains(p.ProjectId)).ExecuteDeleteAsync();
        await db.Projects.Where(p => projectIds.Contains(p.Id)).ExecuteDeleteAsync();
        await db.ValidationRules.Where(r => tableIds.Contains(r.TableDefId)).ExecuteDeleteAsync();
        await db.RowDefs.Where(r => tableIds.Contains(r.TableDefId)).ExecuteDeleteAsync();
        await db.ColumnDefs.Where(c => tableIds.Contains(c.TableDefId)).ExecuteDeleteAsync();
        await db.TableDefs.Where(t => tableIds.Contains(t.Id)).ExecuteDeleteAsync();
        await db.SheetDefs.Where(s => sheetIds.Contains(s.Id)).ExecuteDeleteAsync();
        await db.HeaderFieldDefs.Where(h => versionIds.Contains(h.TemplateVersionId)).ExecuteDeleteAsync();
        await db.TemplateVersions.Where(v => versionIds.Contains(v.Id)).ExecuteDeleteAsync();
        await db.Templates.Where(t => templateIds.Contains(t.Id)).ExecuteDeleteAsync();
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options);

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    private sealed class TestUser : ICurrentUser
    {
        public int? UserId => 1;

        public string? UserName => "b158";

        public string CorrelationId => "b158";

        public string Language => "en";

        public IReadOnlyList<string> GroupSids => [];
    }
}
