using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Reporting;
using Ecr.Application.Security;
using Ecr.Application.Workflow;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Reporting;
using Ecr.Infrastructure.Startup;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// DB-1: застосунок працює під обліковим записом служби, який має РІВНО права з runbook
/// (<see cref="ServiceAccountGrants"/>: <c>db_datareader</c> + <c>db_datawriter</c>, <c>UPDATE</c> на послідовностях,
/// <c>EXECUTE</c> на TVP-типах, процедурах <c>arc.*</c>/<c>rpt.*</c> і функції <c>doc.fn_VersionOf</c>) — і нічого понад.
/// </summary>
/// <remarks>
/// ⛔ Решта тестів ганяє застосунок від імені власника бази, тож бракуюче право не бачив жоден. Тут реальні
/// шляхи (обробники й сховища застосунку, не рукописний SQL) ідуть від імені користувача без входу
/// (<see cref="SqlImpersonation"/>: кожен сеанс, хоч би хто його відкрив, — <c>EXECUTE AS USER</c>).
/// Підготовка даних (<see cref="TestDocumentBuilder"/>) і перевірка результату — від імені власника.
///
/// ⚠ Власна база (<see cref="SqlServerFixture.WithOwnDatabase"/>): користувачі й права не мають торкатися
/// спільної бази колекції. Скидається наприкінці класу.
///
/// Доказ, що тест бачить бракуюче право: <see cref="Без_EXECUTE_на_aud_CellChangeTvp_правка_комірки_падає_і_нічого_не_пише"/>
/// — той самий шлях правки під користувачем без одного гранту червоний (229), а не зелений.
/// </remarks>
public sealed class ServiceAccountLeastPrivilegeTests(ServiceAccountLeastPrivilegeTests.Database database)
    : IClassFixture<ServiceAccountLeastPrivilegeTests.Database>
{
    private const int UserId = 1;

    private static readonly DateTime Now = new(2026, 2, 1, 9, 0, 1, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Сеанс_служби_працює_від_імені_користувача_без_DDL()
    {
        await using var db = database.LowPrivContext();

        Assert.Equal(Database.ServiceUser, await ScalarAsync<string>(db, "SELECT USER_NAME() AS Value"));
        // Права рівня бази — рівно ті, що дають db_datareader + db_datawriter (і типові права public), без DDL.
        var databasePermissions = await db.Database
            .SqlQueryRaw<string>("SELECT permission_name AS Value FROM fn_my_permissions(NULL, 'DATABASE')")
            .ToListAsync();
        // ⚠ «VIEW ANY COLUMN … KEY DEFINITION» SQL Server типово дає public (метадані Always Encrypted) — не з гранту.
        Assert.Equal(
            ["CONNECT", "DELETE", "INSERT", "SELECT", "UPDATE"],
            databasePermissions
                .Where(p => !p.StartsWith("VIEW ANY COLUMN ", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal));
        Assert.Equal(1, await ScalarAsync<int>(db, "SELECT ISNULL(HAS_PERMS_BY_NAME(N'arc.usp_ArchiveYear', 'OBJECT', 'EXECUTE'), 0) AS Value"));

        var ddl = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync("CREATE TABLE dbo.lowpriv_must_fail (Id int);"));
        Assert.Equal(262, ddl.Number);
        Assert.True(SqlImpersonation.SwitchCount(database.LowPrivConnectionString) > 0);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Старт_перевірка_схеми_сід_і_вʼюхи_звітів_проходять_під_службою()
    {
        await using var db = database.LowPrivContext();
        db.Database.SetCommandTimeout(120);

        var capabilities = new SqlCapabilitiesProbe();
        await capabilities.ProbeAsync(database.LowPrivConnectionString, SqlEditionMode.Auto, CancellationToken.None);
        Assert.True(capabilities.IsReadCommittedSnapshotOn, "RCSI мусить читатися з sys.databases і від імені служби");

        // Кроки старту в порядку StartupSequence; збій кроку не ховає наступних — видно весь перелік.
        var problems = new List<string>();
        var validator = new SchemaValidator(db, capabilities, new FixedClock(DateTime.UtcNow));
        await StepAsync(problems, "SchemaValidator", () => validator.ValidateAsync("Validate", CancellationToken.None));
        problems.AddRange(validator.Critical.Select(c => "Critical: " + c));
        problems.AddRange(validator.Warnings.Where(w => w.Contains("usp_", StringComparison.Ordinal)).Select(w => "Warning: " + w));

        await StepAsync(problems, "SeedRunner", () => new SeedRunner(db).RunAsync(CancellationToken.None));

        // Старт генерує вʼюхи rpt.* (StartupSequence 4b): EXECUTE на rpt.usp_Generate*.
        await StepAsync(problems, "ReportViewGenerator", () => new ReportViewGenerator(db).GenerateAsync(templateVersionId: null, CancellationToken.None));

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static async Task StepAsync(List<string> problems, string step, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or DbUpdateException)
        {
            problems.Add($"{step}: {ex.GetType().Name}: {ex.GetBaseException().Message}");
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Додавання_і_видалення_рядка_з_журналом_рядків_проходять_під_службою()
    {
        var doc = await database.ArrangeAsync();
        var since = await database.AdminScalarAsync<long>("SELECT ISNULL(MAX(Id), 0) FROM aud.RowChange");
        var deletedKey = RowKeyOf(doc, 2);

        await using (var db = database.LowPrivContext())
        {
            var clock = new FixedClock(Now);
            var created = await new CreateRowHandler(
                    new RowStore(db, new BulkCellLoader(database.LowPrivConnectionString, 1000), clock), new DocumentStore(db),
                    Metadata(doc), Access(doc), new UnitOfWork(db), clock, new SheetEditGate(db),
                    rowAudit: new RowChangeAudit(db))
                .HandleAsync(doc.DocumentId, doc.TableInstanceId, RowKey.Create("LOWPRIV_1"), Profile(), CancellationToken.None);
            Assert.Equal("LOWPRIV_1", created.Value);
        }

        await using (var db = database.LowPrivContext())
        {
            var clock = new FixedClock(Now);
            var jobs = Substitute.For<IBackgroundJobScheduler>();
            jobs.EnlistsInCallerTransaction.Returns(false);
            await new DeleteRowHandler(
                    new RowStore(db, new BulkCellLoader(database.LowPrivConnectionString, 1000), clock), new DocumentStore(db),
                    Metadata(doc), Access(doc), new NormalizedCellStore(db), new AuditWriter(db), jobs,
                    new UnitOfWork(db), clock, new SheetEditGate(db), rowAudit: new RowChangeAudit(db))
                .HandleAsync(doc.DocumentId, doc.TableInstanceId, deletedKey, Profile(), CancellationToken.None);
        }

        Assert.Equal(1, await database.AdminScalarAsync<int>(
            $"SELECT COUNT(*) FROM doc.TableRow WHERE TableInstanceId = {doc.TableInstanceId} AND RowKey = N'LOWPRIV_1' AND IsDeleted = 0"));
        Assert.Equal(1, await database.AdminScalarAsync<int>(
            $"SELECT COUNT(*) FROM doc.TableRow WHERE TableInstanceId = {doc.TableInstanceId} AND RowKey = N'{deletedKey}' AND IsDeleted = 1"));
        Assert.Equal(2, await database.AdminScalarAsync<int>(
            $"SELECT COUNT(*) FROM aud.RowChange WHERE Id > {since} AND TableInstanceId = {doc.TableInstanceId} AND RowKey IN (N'LOWPRIV_1', N'{deletedKey}')"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Правка_комірки_TVP_і_журнал_комірок_та_подання_аркуша_проходять_під_службою()
    {
        var doc = await database.ArrangeAsync();
        await PatchAsync(database.LowPrivConnectionString, doc, 4321.5m);

        Assert.Equal(4321.5m, await database.AdminScalarAsync<decimal>(
            $"SELECT ValueNumeric FROM doc.CellValue WHERE PeriodKey = {doc.PeriodKey.Value} AND TableRowId = {doc.RowIds[0]} AND ColumnDefId = {doc.ColumnDefIds[1]}"));
        Assert.Equal(1, await database.AdminScalarAsync<int>(
            $"SELECT COUNT(*) FROM aud.CellChange WHERE DocumentId = {doc.DocumentId} AND TableRowId = {doc.RowIds[0]} AND ColumnDefId = {doc.ColumnDefIds[1]}"));

        await using (var db = database.LowPrivContext())
        {
            await BuildSubmit(db, doc).HandleAsync(doc.DocumentId, doc.SheetDefId, doc.PeriodKey.Value, CancellationToken.None);
        }

        Assert.Equal((byte)DocumentStatus.Submitted, await database.AdminScalarAsync<byte>(
            $"SELECT Status FROM wf.ApprovalState WHERE DocumentId = {doc.DocumentId} AND SheetDefId = {doc.SheetDefId} AND PeriodKey = {doc.PeriodKey.Value}"));
        Assert.Equal(1, await database.AdminScalarAsync<int>(
            $"SELECT COUNT(*) FROM calc.SubmissionSnapshot WHERE DocumentId = {doc.DocumentId} AND SheetDefId = {doc.SheetDefId}"));

        // Стабільна вʼюха звітів (SSRS читає її тим самим обліковим записом у малих інсталяціях).
        await using (var db = database.LowPrivContext())
        {
            var lowCount = await ScalarAsync<int>(db, $"SELECT COUNT(*) AS Value FROM rpt.v_DocumentCells WHERE DocumentId = {doc.DocumentId}");
            Assert.Equal(await database.AdminScalarAsync<int>($"SELECT COUNT(*) FROM rpt.v_DocumentCells WHERE DocumentId = {doc.DocumentId}"), lowCount);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Нічна_перевірка_партицій_виконує_процедури_аудиту_під_службою()
    {
        await using (var db = database.LowPrivContext())
        {
            db.Database.SetCommandTimeout(120);
            var capabilities = new SqlCapabilitiesProbe();
            await capabilities.ProbeAsync(database.LowPrivConnectionString, SqlEditionMode.Auto, CancellationToken.None);
            await new PartitionCheckJob(db, capabilities, new FixedClock(DateTime.UtcNow))
                .ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);
        }

        var details = await database.AdminScalarAsync<string>(
            $"SELECT TOP (1) DetailsJson FROM itg.MaintenanceRun WHERE JobCode = N'{PartitionCheckJob.Code}' ORDER BY Id DESC");
        Assert.DoesNotContain("Failed", details, StringComparison.Ordinal);
    }

    /// <summary>
    /// Без EXECUTE на процедуру <c>rpt.*</c> перевірка старту не бачить її (OBJECT_ID = NULL) — попередження мусить назвати
    /// і цю причину, а не лише «базу оновлено без кроку схеми». Мутація: повернути старий текст у
    /// <c>SchemaValidator.ValidateSqlOnlyProceduresAsync</c> — тест червоний.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_EXECUTE_на_процедуру_rpt_попередження_старту_називає_брак_права()
    {
        await using var db = Database.Context(database.NoStableViewsConnectionString);
        var capabilities = new SqlCapabilitiesProbe();
        await capabilities.ProbeAsync(database.NoStableViewsConnectionString, SqlEditionMode.Auto, CancellationToken.None);
        var validator = new SchemaValidator(db, capabilities, new FixedClock(DateTime.UtcNow));

        await validator.ValidateAsync("Validate", CancellationToken.None);

        var warning = Assert.Single(validator.Warnings, w => w.Contains("usp_", StringComparison.Ordinal));
        Assert.Contains("rpt.usp_GenerateStableViews", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("rpt.usp_GenerateTemplateViews", warning, StringComparison.Ordinal);
        Assert.Contains("не має на них EXECUTE", warning, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_EXECUTE_на_aud_CellChangeTvp_правка_комірки_падає_і_нічого_не_пише()
    {
        var doc = await database.ArrangeAsync();

        var error = await Assert.ThrowsAnyAsync<Exception>(() => PatchAsync(database.NoCellChangeConnectionString, doc, 99m));

        var sql = Flatten(error).OfType<SqlException>().FirstOrDefault();
        Assert.True(sql is not null, "очікувано SqlException у ланцюгу: " + error);
        Assert.Equal(229, sql.Number);
        Assert.Contains("CellChangeTvp", sql.Message, StringComparison.Ordinal);
        Assert.Equal(0, await database.AdminScalarAsync<int>(
            $"SELECT COUNT(*) FROM doc.CellValue WHERE PeriodKey = {doc.PeriodKey.Value} AND TableRowId = {doc.RowIds[0]} AND ColumnDefId = {doc.ColumnDefIds[1]}"));
    }

    // ── Шляхи ────────────────────────────────────────────────────────────────

    private async Task PatchAsync(string connectionString, TestDocument doc, decimal value)
    {
        var rowKey = RowKeyOf(doc, 1);
        var version = Convert.ToBase64String(await database.AdminScalarAsync<byte[]>(
            $"SELECT RowVersion FROM doc.TableRow WHERE TableInstanceId = {doc.TableInstanceId} AND RowKey = N'{rowKey}'"));

        await using var db = Database.Context(connectionString);
        var clock = new FixedClock(Now);
        var bulk = new BulkCellLoader(connectionString, 1000);

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodStateAsync(doc.DocumentId, doc.PeriodKey.Value, Arg.Any<CancellationToken>())
               .Returns((PeriodState?)PeriodState.Open);

        var access = Access(doc);
        access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(Profile());
        access.CanEditCellsAsync(
                  Arg.Any<AccessProfile>(), doc.TableInstanceId, Arg.Any<PeriodKey>(),
                  Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
              .Returns(call => (IReadOnlyDictionary<CellAddress, EditDecision>)call
                  .ArgAt<IReadOnlyCollection<CellAddress>>(3).ToDictionary(a => a, _ => EditDecision.Allow()));

        var methodologies = Substitute.For<IMethodologyStore>();
        methodologies.GetMethodologyIdsBoundToTableAsync(doc.TableDefId, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<int>>([]));
        methodologies.GetMethodologyIdsBoundToTablesAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<int>>([]));

        var registries = Substitute.For<IRegistryStore>();
        registries.FindExistingEntryIdsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
                  .Returns(call => call.ArgAt<IReadOnlyCollection<long>>(0).ToHashSet());

        var handler = new PatchCellsHandler(
            new NormalizedCellStore(db), new RowStore(db, bulk, clock), new DocumentStore(db), periods,
            Metadata(doc), access,
            new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
            methodologies, registries, Headers(), new AuditWriter(db), new AuditReader(db),
            Substitute.For<IBackgroundJobScheduler>(), new UnitOfWork(db), User(), clock, new SheetEditGate(db),
            new UnitCatalog(db));

        await handler.HandleAsync(
            new PatchCellsRequest(doc.TableInstanceId, doc.PeriodKey.Value, "UserEdit",
                [new PatchRow(rowKey, version, [new PatchCell("COL2", value)])]),
            CancellationToken.None);
    }

    private SubmitSheetHandler BuildSubmit(EcrDbContext db, TestDocument doc)
    {
        var clock = new FixedClock(Now);
        var bulk = new BulkCellLoader(database.LowPrivConnectionString, 1000);

        var documents = Substitute.For<IDocumentStore>();
        documents.HasSheetAsync(doc.DocumentId, doc.SheetDefId, Arg.Any<CancellationToken>()).Returns(true);
        documents.FindProjectIdAsync(doc.DocumentId, Arg.Any<CancellationToken>()).Returns(doc.ProjectId);

        var access = Access(doc);
        access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(Profile());
        access.CanSubmitAsync(
                  Arg.Any<AccessProfile>(), doc.DocumentId, doc.SheetDefId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
              .Returns(EditDecision.Allow());
        access.CurrentApprovalStepAsync(doc.DocumentId, doc.SheetDefId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
              .Returns((ApprovalStepView?)null);

        var snapshots = Substitute.For<IReportSnapshotBuilder>();
        snapshots.ListAsync(Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<CancellationToken>())
                 .Returns<IReadOnlyList<ReportSnapshotSummary>>([]);

        var methodologies = Substitute.For<IMethodologyStore>();
        methodologies.GetCalculationFreshnessAsync(
                         Arg.Any<long>(), Arg.Any<int>(), Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<CancellationToken>())
                     .Returns(new CalculationFreshness(null, null));

        return new SubmitSheetHandler(
            new NormalizedCellStore(db), new RowStore(db, bulk, clock), new WorkflowStore(db), documents,
            Metadata(doc), access,
            new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
            Headers(), new ReportSnapshotSync(snapshots, documents),
            new UnitOfWork(db), User(), clock, new SheetEditGate(db),
            Substitute.For<Ecr.Application.Recalculation.ISubmitRecalculation>(),
            methodologies, new TemplateVersionStore(db), new RegistryStore(db), new AuditWriter(db));
    }

    // ── Допоміжне ────────────────────────────────────────────────────────────

    private static IEnumerable<Exception> Flatten(Exception error)
    {
        for (var e = error; e is not null; e = e.InnerException)
        {
            yield return e;
        }
    }

    private static string RowKeyOf(TestDocument doc, int ordinal) => $"R{ordinal}_{doc.SheetCode["SHEET".Length..]}";

    private static IDocumentHeaderStore Headers()
    {
        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(new Dictionary<string, ExpressionValue>());
        headers.GetValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(new Dictionary<int, DocumentHeaderValueData>());
        return headers;
    }

    /// <summary>Доступ дозволено: предмет класу — права БАЗИ, не рішення застосунку про доступ.</summary>
    private static IAccessDecisionService Access(TestDocument doc)
    {
        var access = Substitute.For<IAccessDecisionService>();
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
              .Returns(EditDecision.Allow());
        access.CanCreateRowsAsync(
                  Arg.Any<AccessProfile>(), doc.TableInstanceId, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
              .Returns(call => (IReadOnlyDictionary<string, NewRowAccess>)call.ArgAt<IReadOnlyCollection<string>>(2)
                  .ToDictionary(k => k, _ => new NewRowAccess(EditDecision.Allow(), new Dictionary<int, EditDecision>()), StringComparer.Ordinal));
        access.CanClearCellsAsync(
                  Arg.Any<AccessProfile>(), doc.TableInstanceId, Arg.Any<PeriodKey>(),
                  Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
              .Returns(call => (IReadOnlyDictionary<CellAddress, EditDecision>)call
                  .ArgAt<IReadOnlyCollection<CellAddress>>(3).ToDictionary(a => a, _ => EditDecision.Allow()));
        return access;
    }

    private static IMetadataCache Metadata(TestDocument doc)
    {
        var sheet = new SheetDef(doc.TemplateVersionId, EcrCode.Create($"SH{doc.SheetDefId}"), Name("Sheet"), 1);
        SetId(sheet, doc.SheetDefId);
        var table = new TableDef(doc.SheetDefId, EcrCode.Create($"TB{doc.TableDefId}"), Name("Table"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        SetId(table, doc.TableDefId);

        var columns = new Dictionary<int, ColumnDef>();
        for (var i = 0; i < doc.ColumnDefIds.Count; i++)
        {
            var column = new ColumnDef(doc.TableDefId, EcrCode.Create($"COL{i + 1}"), Name($"Col {i + 1}"), i + 1,
                i == 0 ? CellDataType.String : CellDataType.Decimal);
            SetId(column, doc.ColumnDefIds[i]);
            table.AddColumn(column);
            columns[doc.ColumnDefIds[i]] = column;
        }

        sheet.AddTable(table);
        var snapshot = new TemplateVersionSnapshot(
            TemplateVersionId: doc.TemplateVersionId, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: columns, RowsByKey: new Dictionary<(int, string), RowDef>());

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(doc.TemplateVersionId, Arg.Any<CancellationToken>()).Returns(snapshot);
        return metadata;
    }

    private static void SetId(object entity, int id) => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static AccessProfile Profile() => new()
    {
        CacheKey = "p", UserId = UserId, SecurityStamp = "s",
        Permissions = new HashSet<string>(), Grants = new Dictionary<string, GrantLevel>(),
        Denies = new HashSet<string>(), RoleIds = new HashSet<int>(),
    };

    private static ICurrentUser User()
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(UserId);
        return user;
    }

    private static async Task<T> ScalarAsync<T>(EcrDbContext db, string query)
        => (await db.Database.SqlQueryRaw<T>(query).ToListAsync()).Single();

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    /// <summary>Власна база з користувачами служби: повні права за переліком і переліком без одного гранту.</summary>
    public sealed class Database : IAsyncLifetime
    {
        /// <summary>Користувач служби з рівно переліком прав.</summary>
        public const string ServiceUser = "ecr_lowpriv";

        /// <summary>Той самий перелік без <c>EXECUTE</c> на <c>aud.CellChangeTvp</c>.</summary>
        private const string NoCellChangeUser = "ecr_lowpriv_nocellchange";

        /// <summary>Той самий перелік без <c>EXECUTE</c> на <c>rpt.usp_GenerateStableViews</c>.</summary>
        private const string NoStableViewsUser = "ecr_lowpriv_nostableviews";

        private readonly SqlServerFixture _sql = SqlServerFixture.WithOwnDatabase("_lowpriv");

        /// <summary>Рядок, кожен сеанс якого — від імені <see cref="ServiceUser"/>.</summary>
        public string LowPrivConnectionString { get; private set; } = string.Empty;

        /// <summary>Рядок, кожен сеанс якого — від імені користувача без гранту на <c>aud.CellChangeTvp</c>.</summary>
        public string NoCellChangeConnectionString { get; private set; } = string.Empty;

        /// <summary>Рядок, кожен сеанс якого — від імені користувача без гранту на <c>rpt.usp_GenerateStableViews</c>.</summary>
        public string NoStableViewsConnectionString { get; private set; } = string.Empty;

        /// <inheritdoc />
        public async Task InitializeAsync()
        {
            await _sql.InitializeAsync();
            await using var connection = new SqlConnection(_sql.ConnectionString);
            await connection.OpenAsync();
            foreach (var (user, except) in new[]
                     {
                         (ServiceUser, (string?)null), (NoCellChangeUser, "aud.CellChangeTvp"),
                         (NoStableViewsUser, "rpt.usp_GenerateStableViews"),
                     })
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"CREATE USER [{user}] WITHOUT LOGIN;" + Environment.NewLine + ServiceAccountGrants.Script(user, except);
                await command.ExecuteNonQueryAsync();
            }

            LowPrivConnectionString = SqlImpersonation.ConnectionStringFor(_sql.ConnectionString, ServiceUser);
            NoCellChangeConnectionString = SqlImpersonation.ConnectionStringFor(_sql.ConnectionString, NoCellChangeUser);
            NoStableViewsConnectionString = SqlImpersonation.ConnectionStringFor(_sql.ConnectionString, NoStableViewsUser);
        }

        /// <inheritdoc />
        public async Task DisposeAsync()
        {
            await _sql.DisposeAsync();
            var master = new SqlConnectionStringBuilder(_sql.ConnectionString) { InitialCatalog = "master", Pooling = false };
            await using var connection = new SqlConnection(master.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"IF DB_ID(N'{_sql.DatabaseName}') IS NOT NULL BEGIN ALTER DATABASE [{_sql.DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_sql.DatabaseName}]; END";
            await command.ExecuteNonQueryAsync();
        }

        /// <summary>Контекст застосунку від імені служби.</summary>
        public EcrDbContext LowPrivContext() => Context(LowPrivConnectionString);

        /// <summary>Контекст на заданий рядок — так само, як його будує застосунок.</summary>
        public static EcrDbContext Context(string connectionString)
            => new(EfWarningGuard.Apply(new DbContextOptionsBuilder<EcrDbContext>()
                .UseSqlServer(connectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))).Options);

        /// <summary>Документ (динамічні рядки) — від імені власника.</summary>
        public Task<TestDocument> ArrangeAsync()
            => new TestDocumentBuilder(_sql.ConnectionString)
                .BuildAsync(columnCount: 3, rowCount: 3, rowMode: TableRowMode.Dynamic, ct: CancellationToken.None);

        /// <summary>Скаляр від імені власника (перевірка результату).</summary>
        public async Task<T> AdminScalarAsync<T>(string query)
        {
            await using var connection = new SqlConnection(_sql.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = query;
            return (T)(await command.ExecuteScalarAsync())!;
        }
    }
}
