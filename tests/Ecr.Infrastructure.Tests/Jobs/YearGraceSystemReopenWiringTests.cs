// tests/Ecr.Infrastructure.Tests/Jobs/YearGraceSystemReopenWiringTests.cs
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Projects;
using Ecr.Application.Recalculation;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// ФВ-1.8, D-204 (варіант «б», рішення людини 2026-09-28): у вікні року
/// задача станів сама перевідкриває вже закриті періоди року — системним
/// Reopen з аудитом (ФВ-1.10), — правки в них ідуть як у <c>Grace</c>
/// (<c>IsLateEdit</c>, ФВ-1.9) і перераховуються; наприкінці вікна —
/// назад у <c>Closed</c>.
/// </summary>
/// <remarks>
/// Справжня SQL-база, справжні <c>PeriodStateJob</c>, <c>AuditWriter</c>,
/// <c>PeriodStore</c>, <c>AccessDecisionService</c>, сховища запису і
/// <c>RecalculationService</c>. Пояс проєкту будівника — <c>Asia/Atyrau</c>;
/// політика коротка (<c>grace 15</c>, <c>hard-close 30</c>): листопад 2026
/// закривається 30.12.2026 за власними межами, тобто ДО кінця року; вікно +45
/// — 01.01…14.02.2027 включно.
/// <para>
/// ⚠ <c>PeriodStateJob</c> обходить УСІ активні проєкти спільної бази; свої
/// проєкти тест архівує у <c>finally</c>, рядки журналу обслуговування,
/// написані за час тесту, прибирає. Аудит свого періоду шукається за
/// <c>EntityId</c> періоду — чужі рядки на підрахунок не впливають.
/// </para>
/// <para>
/// Мутаційні докази — у коментарях тестів; прогін і результат — в описі коміту.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class YearGraceSystemReopenWiringTests(SqlServerFixture sql) : IDisposable
{
    private const int November = 202611;

    private static readonly TimeZoneInfo Site = SiteTimeZone.Create("Asia/Atyrau").ToTimeZoneInfo();

    private static readonly YearGraceWindow Window = YearGraceWindow.For(new DateOnly(2026, 12, 31), 45, Site);

    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    /// <inheritdoc />
    public void Dispose() => _memory.Dispose();

    /// <remarks>
    /// Мутації (червоний → зелений після повернення): (1) у
    /// <c>PeriodStateJob</c> не застосовувати <c>plan.YearReopens</c> → 01.01
    /// листопад лишається <c>Closed</c>; (2) не писати аудит системного Reopen
    /// → лічильник аудиту 0 замість 1; (3) не ставити матеріалізацію для
    /// системного Reopen → <c>EnqueueAfterTransitionAsync</c> без 202611.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-1.8")]
    [Trait("Requirement", "ФВ-1.10")]
    public async Task Закритий_листопад_01_01_відкривається_системою_з_аудитом_і_15_02_закривається()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(November, rowCount: 1);
        var baseline = await ScalarAsync("SELECT ISNULL(MAX(Id), 0) FROM itg.MaintenanceRun");

        try
        {
            var periodId = await ArmClosedNovemberAsync(builder, doc);

            // До кінця року (31.12 вечір) — нічого: вікно ще не почалось.
            await RunStateJobAsync(builder, SiteTime(2026, 12, 31, 20), Substitute.For<IMaterializationScheduler>());
            Assert.Equal(PeriodState.Closed, (await PeriodAsync(builder, periodId)).State);
            Assert.Equal(0, await ReopenAuditCountAsync(periodId));

            // 01.01 — системний Reopen.
            var materialization = Substitute.For<IMaterializationScheduler>();
            var error = await RunStateJobAsync(builder, SiteTime(2027, 1, 1, 10), materialization);

            var reopened = await PeriodAsync(builder, periodId);
            Assert.True(reopened.State == PeriodState.Grace, $"01.01 листопад мав відкритися. Збій прогону: {error}");
            Assert.Equal(Window.EndsAtUtc, reopened.ReopenedUntil);
            Assert.StartsWith("Вікно року", reopened.ReopenReason, StringComparison.Ordinal);

            // Аудит — як у ручного Reopen (Operation = Reopen, причина), актор — система.
            Assert.Equal(1, await ReopenAuditCountAsync(periodId));
            Assert.Equal(1, await ScalarAsync(
                $"""
                SELECT COUNT(*) FROM aud.StructureChange
                 WHERE EntityType = N'Period' AND EntityId = {periodId} AND Operation = N'Reopen'
                   AND ChangedByUserId = {PeriodStateJob.SystemUserId}
                   AND CorrelationId = N'{PeriodStateJob.AuditCorrelationId}'
                   AND ChangeReason LIKE N'Вікно року%' AND OldJson LIKE N'%Closed%' AND NewJson LIKE N'%Grace%'
                """));

            // Матеріалізація PI — той самий тригер, що в ручного Reopen (Closed → Grace).
            await materialization.Received(1).EnqueueAfterTransitionAsync(
                doc.ProjectId,
                Arg.Is<IReadOnlyCollection<int>>(keys => keys.Contains(November)),
                Arg.Any<CancellationToken>());

            // Повторний прогін у вікні — без другого аудиту і без повторного відкриття.
            await RunStateJobAsync(builder, SiteTime(2027, 1, 1, 11), Substitute.For<IMaterializationScheduler>());
            await RunStateJobAsync(builder, SiteTime(2027, 2, 14, 23), Substitute.For<IMaterializationScheduler>());
            var again = await PeriodAsync(builder, periodId);
            Assert.Equal(PeriodState.Grace, again.State);
            Assert.Equal(reopened.StateChangedAt, again.StateChangedAt);
            Assert.Equal(1, await ReopenAuditCountAsync(periodId));

            // 15.02 (offset 45) — назад у Closed.
            var closeError = await RunStateJobAsync(builder, SiteTime(2027, 2, 15, 10), Substitute.For<IMaterializationScheduler>());
            Assert.True(
                (await PeriodAsync(builder, periodId)).State == PeriodState.Closed,
                $"15.02 листопад мав закритися. Збій прогону: {closeError}");

            // Листопад наступного року — поза вікном: не відкривається.
            await RunStateJobAsync(builder, SiteTime(2027, 11, 15, 10), Substitute.For<IMaterializationScheduler>());
            Assert.Equal(PeriodState.Closed, (await PeriodAsync(builder, periodId)).State);
            Assert.Equal(1, await ReopenAuditCountAsync(periodId));
        }
        finally
        {
            await RetireAsync(doc.ProjectId);
            await ExecuteAsync(
                "DELETE FROM itg.MaintenanceRun WHERE Id > @id AND JobCode = @code",
                ("@id", baseline), ("@code", PeriodStateJob.Code));
        }
    }

    /// <remarks>
    /// Після системного Reopen ставиться разовий пошук осиротілих рядків: нічний
    /// прохід обходить лише <c>Open</c>/<c>Grace</c>, і позначки <c>IsOrphaned</c>
    /// щойно відкритого року застаріли, поки він був закритим. Мутація: не
    /// рахувати відкриті періоди (<c>reopened++</c>) у <c>PeriodStateJob</c> →
    /// постановки немає, червоний (прогнано).
    /// <para>
    /// ⚠ Негативного боку («без Reopen — без постановки») тут немає свідомо:
    /// задача обходить УСІ активні проєкти спільної бази, і чужий проєкт у
    /// своєму вікні року дав би постановку, якої цей тест не спричиняв.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-1.8")]
    public async Task Системний_Reopen_ставить_разовий_пошук_осиротілих_рядків()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(November, rowCount: 1);
        var baseline = await ScalarAsync("SELECT ISNULL(MAX(Id), 0) FROM itg.MaintenanceRun");

        try
        {
            var periodId = await ArmClosedNovemberAsync(builder, doc);
            var jobs = Substitute.For<IBackgroundJobScheduler>();

            var error = await RunStateJobAsync(
                builder, SiteTime(2027, 1, 1, 10), Substitute.For<IMaterializationScheduler>(), jobs);

            Assert.True(
                (await PeriodAsync(builder, periodId)).State == PeriodState.Grace,
                $"Передумова: листопад 01.01 мав відкритися. Збій прогону: {error}");

            await jobs.Received(1).EnqueueExclusiveAsync<IOrphanScanJob>(
                PeriodStateJob.OrphanScanAfterReopenTarget,
                Arg.Any<object?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<int?>());
        }
        finally
        {
            await RetireAsync(doc.ProjectId);
            await ExecuteAsync(
                "DELETE FROM itg.MaintenanceRun WHERE Id > @id AND JobCode = @code",
                ("@id", baseline), ("@code", PeriodStateJob.Code));
        }
    }

    /// <remarks>
    /// Правка й перерахунок у перевідкритому вікном листопаді: запис
    /// позначено <c>IsLateEdit = 1</c>, перерахунок поставлено в чергу і сам
    /// перерахунок (справжній <c>RecalculationService</c> зі справжнім
    /// <c>PeriodStore</c>) оновив залежну формулу, теж із <c>IsLateEdit = 1</c>.
    /// Мутація: не застосовувати <c>plan.YearReopens</c> у задачі → запис
    /// відмовлено як у закритий період (<c>ECR-ACCS-0403</c>), червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-1.8")]
    [Trait("Requirement", "ФВ-1.9")]
    public async Task Правка_в_перевідкритому_листопаді_01_01_IsLateEdit_і_формула_перерахована()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(November, columnCount: 3, rowCount: 1, rowMode: TableRowMode.Mixed);
        var baseline = await ScalarAsync("SELECT ISNULL(MAX(Id), 0) FROM itg.MaintenanceRun");

        try
        {
            var periodId = await ArmClosedNovemberAsync(builder, doc);
            var error = await RunStateJobAsync(builder, SiteTime(2027, 1, 1, 10), Substitute.For<IMaterializationScheduler>());
            Assert.True(
                (await PeriodAsync(builder, periodId)).State == PeriodState.Grace,
                $"Передумова: листопад 01.01 мав відкритися. Збій прогону: {error}");

            var userId = await ArrangeWriterAsync(builder, doc.ProjectId);
            var at = SiteTime(2027, 1, 1, 11);
            var jobs = Substitute.For<IBackgroundJobScheduler>();

            await using (var db = builder.CreateContext())
            {
                await Handler(db, doc, userId, at, jobs).HandleAsync(await RequestAsync(doc, 777m), CancellationToken.None);
            }

            var input = doc.ColumnDefIds[1];
            Assert.Equal(1, await ScalarAsync(
                $"SELECT COUNT(*) FROM aud.CellChange WHERE DocumentId = {doc.DocumentId} AND ColumnDefId = {input} AND IsLateEdit = 1"));

            // Перерахунок поставлено — як для відкритого періоду.
            Assert.Contains(
                jobs.ReceivedCalls(),
                c => c.GetMethodInfo().Name == nameof(IBackgroundJobScheduler.EnqueueCoalescedAsync)
                     && c.GetMethodInfo().IsGenericMethod
                     && c.GetMethodInfo().GetGenericArguments()[0] == typeof(IFormulaRecalculationJob));

            // Сам перерахунок: залежна формула [C2] * 2 оновилась.
            await using (var db = builder.CreateContext())
            {
                await Recalculation(db, doc, at).RecalculateAllAsync(doc.DocumentId, doc.PeriodKey, CancellationToken.None);
            }

            var formula = doc.ColumnDefIds[2];
            Assert.Equal(1554m, await NumericAsync(doc, formula));
            Assert.Equal(1, await ScalarAsync(
                $"""
                SELECT COUNT(*) FROM aud.CellChange
                 WHERE DocumentId = {doc.DocumentId} AND ColumnDefId = {formula}
                   AND Origin = N'Recalculation' AND IsLateEdit = 1
                """));
        }
        finally
        {
            await RetireAsync(doc.ProjectId);
            await ExecuteAsync(
                "DELETE FROM itg.MaintenanceRun WHERE Id > @id AND JobCode = @code",
                ("@id", baseline), ("@code", PeriodStateJob.Code));
        }
    }

    /// <remarks>
    /// Архівація проєкту у вікні відмовляє, навіть якщо задача станів ще не
    /// встигла відкрити закритий листопад: інакше рік став би архівним, і
    /// задача (лише активні проєкти) його вже не відкрила б. Після вікна —
    /// архівація проходить. Мутація: прибрати в <c>ArchiveProjectHandler</c>
    /// умову <c>yearGrace.HoldsInGrace</c> → 01.01 архівація проходить, червоний.
    /// Ворота фізичної архівації (<c>ArchiveJob</c>) до кінця вікна —
    /// <see cref="YearGraceArchiveGateTests"/>.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-1.8")]
    public async Task Архівація_проєкту_у_вікні_відмовляє_для_закритого_листопада_після_вікна_проходить()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(November, rowCount: 1);

        try
        {
            await ArmClosedNovemberAsync(builder, doc);

            await using (var db = builder.CreateContext())
            {
                var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(
                    () => Archive(db, doc.ProjectId, SiteTime(2027, 1, 1, 0)).HandleAsync(doc.ProjectId, CancellationToken.None));

                Assert.Equal("ECR-PRD-0409", error.ErrorCode);
                var open = Assert.IsAssignableFrom<IEnumerable<int>>(error.Details!["periodKeys"]);
                Assert.Equal([November], open);
            }

            await using (var db = builder.CreateContext())
            {
                await Archive(db, doc.ProjectId, SiteTime(2027, 2, 15, 0)).HandleAsync(doc.ProjectId, CancellationToken.None);
            }

            Assert.Equal((long)ProjectStatus.Archived, await ScalarAsync($"SELECT Status FROM doc.Project WHERE Id = {doc.ProjectId}"));
        }
        finally
        {
            await RetireAsync(doc.ProjectId);
        }
    }

    // ── Підготовка ──────────────────────────────────────────────────────

    private static DateTime SiteTime(int year, int month, int day, int hour)
        => TimeZoneInfo.ConvertTimeToUtc(new DateTime(year, month, day, hour, 0, 0, DateTimeKind.Unspecified), Site);

    /// <summary>
    /// Активний проєкт; листопад за короткою політикою, закритий задачею 30.12
    /// (до кінця року). Політика — і в базі (архівація й активація читають її
    /// звідти), і в межах періоду.
    /// </summary>
    private async Task<int> ArmClosedNovemberAsync(TestDocumentBuilder builder, TestDocument doc)
    {
        var policy = new PeriodPolicy(
            EcrCode.Create($"YR{Guid.NewGuid():N}"[..14].ToUpperInvariant()), openOffsetDays: 0, graceOffsetDays: 15,
            hardCloseOffsetDays: 30, yearGraceOffsetDays: 45);

        await using var db = builder.CreateContext();
        db.PeriodPolicies.Add(policy);

        var project = await db.Projects.SingleAsync(p => p.Id == doc.ProjectId);
        project.Activate(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(45, project.YearGraceOffsetDays);

        var period = await db.Periods.SingleAsync(p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == November);
        period.RecomputeBoundaries(policy, Site);

        Assert.True(
            period.ComputedCloseAt < Window.YearEndUtc,
            "Політика не закриває листопад до 31.12 — тест нічого не доводить.");

        period.AdvanceTo(PeriodState.Closed, period.ComputedCloseAt);
        await db.SaveChangesAsync();

        await ExecuteAsync(
            "UPDATE doc.Project SET PeriodPolicyId = @policy WHERE Id = @id",
            ("@policy", policy.Id), ("@id", doc.ProjectId));

        return period.Id;
    }

    /// <summary>Користувач із грантом Write на проєкт — рішення про запис рахує справжня служба.</summary>
    private static async Task<int> ArrangeWriterAsync(TestDocumentBuilder builder, int projectId)
    {
        await using var db = builder.CreateContext();

        var name = $"yr_{Guid.NewGuid():N}"[..20];
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash("Year-Reopen-Wiring-2026!"));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"YR_{Guid.NewGuid():N}"[..16].ToUpperInvariant()),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Year reopen writer" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, projectId, GrantLevel.Write));
        await db.SaveChangesAsync();

        return user.Id;
    }

    private async Task<PatchCellsRequest> RequestAsync(TestDocument doc, decimal value)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.RowKey, r.RowVersion, c.Code
              FROM doc.TableRow r, cfg.ColumnDef c
             WHERE r.Id = @row AND c.Id = @column;
            """;
        command.Parameters.AddWithValue("@row", doc.RowIds[0]);
        command.Parameters.AddWithValue("@column", doc.ColumnDefIds[1]);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        return new PatchCellsRequest(
            doc.TableInstanceId, doc.PeriodKey.Value, "UserEdit",
            [new PatchRow(reader.GetString(0), Convert.ToBase64String((byte[])reader[1]), [new PatchCell(reader.GetString(2), value)])]);
    }

    // ── Обробники зі справжніми сховищами ───────────────────────────────

    /// <summary>
    /// <c>PatchCellsHandler</c> зі справжніми рішенням про доступ, метаданими,
    /// сховищем періодів (звідти <c>IsLateEdit</c>) і записом; підмінено лише
    /// методології, довідники й чергу (її виклики перевіряє тест).
    /// </summary>
    private PatchCellsHandler Handler(
        EcrDbContext db, TestDocument doc, int userId, DateTime at, IBackgroundJobScheduler jobs)
    {
        var clock = new TestClock(at);
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(userId);

        var metadata = new MetadataCache(_memory, db);
        var access = new AccessDecisionService(
            db, metadata, new AccessProfileCache(_memory), clock, user, new WorkflowStore(db));

        var methodologies = Substitute.For<IMethodologyStore>();
        methodologies.GetMethodologyIdsBoundToTableAsync(doc.TableDefId, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<int>>([]));

        var registries = Substitute.For<IRegistryStore>();
        registries.FindExistingEntryIdsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
                  .Returns(call => call.ArgAt<IReadOnlyCollection<long>>(0).ToHashSet());

        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(new Dictionary<string, ExpressionValue>());

        return new PatchCellsHandler(
            new NormalizedCellStore(db), new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), clock),
            new DocumentStore(db), new PeriodStore(db), metadata, access,
            new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
            methodologies, registries, headers, new AuditWriter(db), Substitute.For<IAuditReader>(),
            jobs, new UnitOfWork(db), user, clock,
            Substitute.For<ISheetEditGate>(), Substitute.For<IUnitCatalog>());
    }

    /// <summary>
    /// Справжній <c>RecalculationService</c> зі справжнім <c>PeriodStore</c>
    /// (звідти стан періоду й <c>IsLateEdit</c>); структуру з формулою
    /// <c>[C2] * 2</c> у колонці 3 дає знімок метаданих — у <c>cfg.*</c>
    /// будівника формул немає.
    /// </summary>
    private static RecalculationService Recalculation(EcrDbContext db, TestDocument doc, DateTime at)
    {
        var clock = new TestClock(at);
        var bulk = new BulkCellLoader(db.Database.GetConnectionString()!, 1000);

        var versions = Substitute.For<ITemplateVersionStore>();
        versions.ListFormulaDependenciesAsync(doc.TemplateVersionId, Arg.Any<CancellationToken>()).Returns([]);

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(new Dictionary<string, ExpressionValue>());

        return new RecalculationService(
            new NormalizedCellStore(db), new RowStore(db, bulk, clock), new PeriodStore(db),
            Metadata(doc), versions, new RealFormulaEngine(), units, Substitute.For<IRegistryStore>(), headers,
            new AuditWriter(db), clock, new UnitOfWork(db), new SheetEditGate(db));
    }

    private static IMetadataCache Metadata(TestDocument doc)
    {
        var tag = doc.SheetCode["SHEET".Length..];

        var sheet = new SheetDef(doc.TemplateVersionId, EcrCode.Create(doc.SheetCode), Name("Sheet"), 1);
        SetId(sheet, doc.SheetDefId);
        var table = new TableDef(doc.SheetDefId, EcrCode.Create($"TBL{tag}"), Name("Table"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Mixed);
        SetId(table, doc.TableDefId);

        var columns = new Dictionary<int, ColumnDef>();
        for (var i = 1; i <= 3; i++)
        {
            var def = new ColumnDef(
                doc.TableDefId, EcrCode.Create($"C{i}_{tag}"), Name($"Col {i}"), i,
                i == 1 ? CellDataType.String : CellDataType.Decimal);
            SetId(def, doc.ColumnDefIds[i - 1]);
            table.AddColumn(def);
            columns[def.Id] = def;
        }

        var row = new RowDef(doc.TableDefId, RowKey.Create($"R1_{tag}"), 1, Name("Row 1"), RowKind.Item);
        SetId(row, doc.RowDefIds[0]);
        table.AddRow(row);

        var formula = new FormulaDef(table.Id, FormulaScope.Column, $"[C2_{tag}] * 2", ExpressionDialect.Template);
        SetId(formula, 930_001);
        formula.AssignColumn(doc.ColumnDefIds[2]);
        formula.SetEvaluationOrder(1);
        table.AddFormula(formula);

        sheet.AddTable(table);

        var snapshot = new TemplateVersionSnapshot(
            TemplateVersionId: doc.TemplateVersionId, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: columns,
            RowsByKey: new Dictionary<(int, string), RowDef> { [(doc.TableDefId, row.RowKeyValue)] = row });

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(doc.TemplateVersionId, Arg.Any<CancellationToken>()).Returns(snapshot);
        return metadata;
    }

    private static ArchiveProjectHandler Archive(EcrDbContext db, int projectId, DateTime at)
    {
        var access = Substitute.For<IAccessDecisionService>();
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }
                .Permission(ArchiveProjectHandler.Permission)
                .Grant(ResourceKind.Project, projectId, GrantLevel.Manage)
                .Build());

        return new ArchiveProjectHandler(new PeriodStore(db), access, user, new UnitOfWork(db), new TestClock(at));
    }

    /// <summary>Справжній прогін задачі станів (зі справжнім аудитом); збій чужого проєкту — текстом.</summary>
    private static async Task<string?> RunStateJobAsync(
        TestDocumentBuilder builder, DateTime at, IMaterializationScheduler materialization,
        IBackgroundJobScheduler? jobs = null)
    {
        await using var db = builder.CreateContext();

        try
        {
            await new PeriodStateJob(
                    db, new PeriodStateCalculator(), new UnitOfWork(db), new TestClock(at),
                    materialization, logger: null, audit: new AuditWriter(db), jobs: jobs)
                .ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ex.ToString();
        }
    }

    // ── Прибирання й читання ────────────────────────────────────────────

    private static async Task<Period> PeriodAsync(TestDocumentBuilder builder, int periodId)
    {
        await using var db = builder.CreateContext();
        return await db.Periods.AsNoTracking().SingleAsync(p => p.Id == periodId);
    }

    private Task<long> ReopenAuditCountAsync(int periodId)
        => ScalarAsync(
            $"SELECT COUNT(*) FROM aud.StructureChange WHERE EntityType = N'Period' AND EntityId = {periodId} AND Operation = N'Reopen'");

    private async Task<decimal?> NumericAsync(TestDocument doc, int columnDefId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT ValueNumeric FROM doc.CellValue WHERE PeriodKey = {doc.PeriodKey.Value} " +
            $"AND TableRowId = {doc.RowIds[0]} AND ColumnDefId = {columnDefId}";
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? null : (decimal)result;
    }

    /// <summary>Архівує проєкт тесту: задачі станів інших тестів обходять лише активні.</summary>
    private Task RetireAsync(int projectId)
        => ExecuteAsync(
            "UPDATE doc.Project SET Status = @status WHERE Id = @id",
            ("@status", (byte)ProjectStatus.Archived), ("@id", projectId));

    private async Task<long> ScalarAsync(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task ExecuteAsync(string sqlText, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sqlText;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static void SetId(object entity, int id)
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
