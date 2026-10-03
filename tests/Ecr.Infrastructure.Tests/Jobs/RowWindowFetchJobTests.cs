// tests/Ecr.Infrastructure.Tests/Jobs/RowWindowFetchJobTests.cs
using System.Globalization;
using Ecr.Application;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// <see cref="RowWindowFetchJob"/> на РЕАЛЬНОМУ SQL Server із справжнім записом комірок
/// (HSE301 A1, FEATURE-HSE301-VIEW §4.4): вікно рядка → значення PI → комірка й провенанс.
/// </summary>
/// <remarks>
/// ⚠ Усе справжнє й зібране контейнером (патчер, <c>PatchCellsHandler</c>, автор <c>svc-integration</c>);
/// підроблене лише джерело (<see cref="FakeWindowSource"/>) і годинник. Проєкт у <c>Asia/Atyrau</c> (+05:00).
///
/// Мутаційні докази, кожен — точковою правкою задачі чи <c>RowWindowFetch</c>:
/// <list type="bullet">
/// <item>вікно в UTC без пояса проєкту — червоніє <see cref="Вікно_питається_в_UTC_з_часу_проєкту_і_значення_лягає_в_комірку"/>;</item>
/// <item>запис поза патчером / без перевірки <c>KeptManual</c> — червоніє <see cref="Ручна_правка_лишається_статус_KeptManual"/>;</item>
/// <item><c>NeedsFetch</c> завжди <c>true</c> — червоніє <see cref="Повторний_прогін_не_питає_джерело_вдруге"/>;</item>
/// <item>недійсне вікно йде до PI — червоніє <see cref="Недійсне_вікно_не_питає_джерело"/>;</item>
/// <item>прибрати <c>Supersede</c> — червоніє <see cref="NoData_пізніше_стає_Fetched_а_попередній_запис_лишається_історією"/>.</item>
/// </list>
/// </remarks>
[Collection("SqlServer")]
public sealed class RowWindowFetchJobTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 1, 28, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime LocalStart = new(2026, 1, 28, 14, 9, 20);
    private static readonly DateTime LocalEnd = new(2026, 1, 28, 14, 24, 50);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A1")]
    public async Task Вікно_питається_в_UTC_з_часу_проєкту_і_значення_лягає_в_комірку()
    {
        await using var stand = await ArrangeAsync(RowWindowSummaryKind.Total, u => (u.PerHourId, u.StdCubicId));
        await AddRowAsync(stand, "R1", LocalStart, LocalEnd);
        var source = new FakeWindowSource(Result(3348m));

        await RunAsync(stand, source);

        var request = Assert.Single(source.Requests);
        Assert.Equal(new DateTime(2026, 1, 28, 9, 9, 20), request.FromUtc);
        Assert.Equal(new DateTime(2026, 1, 28, 9, 24, 50), request.ToUtc);
        Assert.Equal((SourceSummaryKind.Total, "tag.total"), (request.Summary, request.SourcePath));

        // 3348 Sm3/h·с ÷ 3600 = 0.93 Sm3.
        Assert.Equal(0.93m, (await CellsAsync(stand, "R1"))[stand.VolumeColumn].Numeric);

        var value = Assert.Single(await ValuesAsync(stand));
        Assert.Equal(
            (RowWindowValueStatus.Fetched, true, 0.93m, 3348m),
            (value.Status, value.IsCurrent, value.ValueTarget, value.ValueSource));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A1")]
    public async Task Ручна_правка_лишається_статус_KeptManual()
    {
        await using var stand = await ArrangeAsync(RowWindowSummaryKind.Average, s => (s.StdCubicId, s.StdCubicId));
        await AddRowAsync(stand, "R1", LocalStart, LocalEnd);
        await HumanWriteAsync(stand, "R1", new PatchCell(stand.VolumeCode, 42m));

        await RunAsync(stand, new FakeWindowSource(Result(5m)));

        Assert.Equal(42m, (await CellsAsync(stand, "R1"))[stand.VolumeColumn].Numeric);
        Assert.Equal(RowWindowValueStatus.KeptManual, Assert.Single(await ValuesAsync(stand)).Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A1")]
    public async Task Недійсне_вікно_не_питає_джерело()
    {
        await using var stand = await ArrangeAsync(RowWindowSummaryKind.Average, s => (s.StdCubicId, s.StdCubicId));
        await AddRowAsync(stand, "R1", LocalEnd, LocalStart);
        var source = new FakeWindowSource(Result(5m));

        await RunAsync(stand, source);

        Assert.Empty(source.Requests);
        Assert.Equal(RowWindowValueStatus.InvalidWindow, Assert.Single(await ValuesAsync(stand)).Status);
        Assert.False((await CellsAsync(stand, "R1")).ContainsKey(stand.VolumeColumn));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A1")]
    public async Task Повторний_прогін_не_питає_джерело_вдруге()
    {
        await using var stand = await ArrangeAsync(RowWindowSummaryKind.Average, s => (s.StdCubicId, s.StdCubicId));
        await AddRowAsync(stand, "R1", LocalStart, LocalEnd);
        var source = new FakeWindowSource(Result(5m));
        var trigger = Substitute.For<ICalculationTrigger>();

        await RunAsync(stand, source, trigger);
        await RunAsync(stand, source, trigger);

        Assert.Single(source.Requests);
        Assert.Single(await ValuesAsync(stand));

        // Перерахунок — після запису, один раз (другий прогін нічого не записав).
        await trigger.Received(1).RequestAsync(stand.DocumentId, new PeriodKey(202601), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A1")]
    public async Task NoData_пізніше_стає_Fetched_а_попередній_запис_лишається_історією()
    {
        await using var stand = await ArrangeAsync(RowWindowSummaryKind.Average, s => (s.StdCubicId, s.StdCubicId));
        await AddRowAsync(stand, "R1", LocalStart, LocalEnd);
        var source = new FakeWindowSource(Result(null, percentGood: 0m));
        var trigger = Substitute.For<ICalculationTrigger>();

        await RunAsync(stand, source, trigger);

        Assert.Equal(RowWindowValueStatus.NoData, Assert.Single(await ValuesAsync(stand)).Status);
        Assert.False((await CellsAsync(stand, "R1")).ContainsKey(stand.VolumeColumn));
        Assert.Empty(trigger.ReceivedCalls());

        // Пізні дані PI: наступний прогін знову питає (NoData у межах RefetchWithinDays).
        source.Result = Result(7m);
        await RunAsync(stand, source, trigger, now: Now.AddHours(1));

        var values = await ValuesAsync(stand);
        Assert.Equal(2, values.Count);
        Assert.Equal(
            [(RowWindowValueStatus.NoData, false), (RowWindowValueStatus.Fetched, true)],
            values.OrderBy(v => v.Id).Select(v => (v.Status, v.IsCurrent)));
        Assert.Equal(7m, (await CellsAsync(stand, "R1"))[stand.VolumeColumn].Numeric);
        await trigger.Received(1).RequestAsync(stand.DocumentId, new PeriodKey(202601), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A1")]
    public async Task Закритий_період_нічого_не_питає_й_не_пише()
    {
        await using var stand = await ArrangeAsync(RowWindowSummaryKind.Average, s => (s.StdCubicId, s.StdCubicId));
        await AddRowAsync(stand, "R1", LocalStart, LocalEnd);
        await ExecuteAsync($"UPDATE doc.Period SET State = {(int)PeriodState.Closed} WHERE ProjectId = {stand.ProjectId} AND PeriodKey = 202601");
        var source = new FakeWindowSource(Result(5m));

        await RunAsync(stand, source);

        Assert.Empty(source.Requests);
        Assert.Empty(await ValuesAsync(stand));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A1")]
    public async Task Відмова_джерела_одного_рядка_SourceError_а_решта_рядків_рахуються()
    {
        await using var stand = await ArrangeAsync(RowWindowSummaryKind.Average, s => (s.StdCubicId, s.StdCubicId));
        await AddRowAsync(stand, "R1", LocalStart, LocalEnd);
        await AddRowAsync(stand, "R2", LocalStart.AddHours(1), LocalEnd.AddHours(1));
        var source = new FakeWindowSource(Result(5m))
        {
            Fail = request => request.FromUtc.Hour == 9,
        };

        await RunAsync(stand, source);

        var values = (await ValuesAsync(stand)).ToDictionary(v => v.RowKey);
        Assert.Equal((RowWindowValueStatus.SourceError, "ECR-INT-0503"), (values["R1"].Status, values["R1"].ErrorCode));
        Assert.Equal(RowWindowValueStatus.Fetched, values["R2"].Status);
        Assert.Equal(5m, (await CellsAsync(stand, "R2"))[stand.VolumeColumn].Numeric);
    }

    // ── Стенд ────────────────────────────────────────────────────────────────

    private static WindowResult Result(decimal? value, decimal? percentGood = 100m)
        => new(value, null, value is null ? 0 : 4, percentGood, WindowComputedBy.Local, [], null);

    private async Task RunAsync(Stand stand, FakeWindowSource source, ICalculationTrigger? trigger = null, DateTime? now = null)
    {
        await using var scope = stand.Provider.CreateAsyncScope();
        var services = scope.ServiceProvider;

        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(now ?? Now);

        var job = new RowWindowFetchJob(
            services.GetRequiredService<EcrDbContext>(),
            [source],
            services.GetRequiredService<ICellPatcher>(),
            services.GetRequiredService<IntegrationActor>(),
            clock,
            recalculation: trigger);

        await job.ExecuteAsync(
            new RowWindowFetchRequest(stand.InstanceId, 202601), Substitute.For<IJobProgress>(), CancellationToken.None);
    }

    /// <summary>Джерело з готовою відповіддю вікна; запам'ятовує запити.</summary>
    private sealed class FakeWindowSource(WindowResult result) : IExternalDataSource
    {
        public ExternalTransport Transport => ExternalTransport.PiWebApi;

        public WindowResult Result { get; set; } = result;

        public Func<WindowRequest, bool>? Fail { get; init; }

        public List<WindowRequest> Requests { get; } = [];

        public Task<IReadOnlyList<SourceEntityDescriptor>> DiscoverAsync(int dataSourceId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<CollectionResult> ReadAsync(CollectionRequest request, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<SourceEventResult> ReadEventsAsync(SourceEventQuery query, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<WindowResult> ReadWindowAsync(WindowRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            if (Fail?.Invoke(request) == true)
            {
                throw new BusinessRuleException("ECR-INT-0503", "source down");
            }

            return Task.FromResult(Result);
        }
    }

    private sealed record Stand(
        ServiceProvider Provider,
        Func<ValueTask> Cleanup,
        int ProjectId,
        long DocumentId,
        long InstanceId,
        int StartColumn,
        int EndColumn,
        int VolumeColumn,
        string VolumeCode,
        int StdCubicId,
        int PerHourId,
        int HumanId,
        int RoleId) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Cleanup();
    }

    private async Task<Stand> ArrangeAsync(
        RowWindowSummaryKind summary, Func<UnitIds, (int Source, int Target)> units)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(
            periodKey: 202601, columnCount: 2, rowCount: 0, rowMode: TableRowMode.Dynamic, ct: CancellationToken.None);

        await using var db = builder.CreateContext();
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        var january = await db.Periods.SingleAsync(p => p.ProjectId == chain.ProjectId && p.PeriodKeyValue == 202601);
        january.TransitionTo(PeriodState.Open, Now);

        var start = new ColumnDef(chain.TableDefId, EcrCode.Create($"RS_{tag}"), Name("Start"), 10, CellDataType.Date);
        var end = new ColumnDef(chain.TableDefId, EcrCode.Create($"RE_{tag}"), Name("End"), 11, CellDataType.Date);
        db.ColumnDefs.AddRange(start, end);

        var dataSource = new DataSource(
            EcrCode.Create($"A1SRC{tag}"), Name("HSE301-A1"), ExternalTransport.PiWebApi, "https://example.test", "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync(CancellationToken.None);

        var entity = new SourceEntity(dataSource.Id, $"Tag{tag}", RegistrySourceKind.External);
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync(CancellationToken.None);

        var stdCubic = await db.Units.AsNoTracking().SingleAsync(u => u.Code == "Sm3");
        var perHour = await db.Units.AsNoTracking().SingleAsync(u => u.Code == "Sm3_per_h");
        var (sourceUnit, targetUnit) = units(new UnitIds(stdCubic.Id, perHour.Id));

        var columns = await db.ColumnDefs.AsNoTracking().Where(c => c.TableDefId == chain.TableDefId).ToDictionaryAsync(c => c.Id);
        var volumeId = chain.ColumnDefIds[1];

        var map = RowWindowMap.Create(columns[volumeId], columns[start.Id], columns[end.Id], null, summary, isStep: false, targetUnit);
        db.RowWindowMaps.Add(map);
        await db.SaveChangesAsync(CancellationToken.None);
        map.AddSource(null, entity.Id, "tag.total", sourceUnit);
        await db.SaveChangesAsync(CancellationToken.None);

        var roleId = await CreateWriterRoleAsync(db, chain.ProjectId);
        var humanId = await AddHumanAsync(db, roleId);
        var provider = BuildProvider();

        return new Stand(
            provider,
            async () =>
            {
                // Активна сутність у спільній базі фарбувала б SourcesHealthCheck інших тестів.
                await ExecuteAsync($"UPDATE ext.SourceEntity SET IsActive = 0 WHERE Id = {entity.Id}");
                await ExecuteAsync($"UPDATE ext.RowWindowMap SET IsActive = 0 WHERE Id = {map.Id}");
                await RevokeAsync(roleId);
                await provider.DisposeAsync();
            },
            chain.ProjectId,
            chain.DocumentId,
            chain.TableInstanceId,
            start.Id,
            end.Id,
            volumeId,
            columns[volumeId].Code,
            stdCubic.Id,
            perHour.Id,
            humanId,
            roleId);
    }

    private sealed record UnitIds(int StdCubicId, int PerHourId);

    private ServiceProvider BuildProvider()
    {
        var configuration = Substitute.For<IConfiguration>();
        configuration[Arg.Any<string>()].Returns((string?)null);
        var connectionStrings = Substitute.For<IConfigurationSection>();
        connectionStrings["Ecr"].Returns(sql.ConnectionString);
        configuration.GetSection("ConnectionStrings").Returns(connectionStrings);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEcrApplication();
        services.AddEcrInfrastructure(configuration);

        services.AddScoped<JobActorScope>();
        services.AddScoped<ICurrentUser>(sp => new JobAwareCurrentUser(
            new NoHttpRequestUser(), sp.GetRequiredService<JobActorScope>()));
        services.AddScoped(_ => Substitute.For<IBackgroundJobScheduler>());

        return services.BuildServiceProvider();
    }

    /// <summary>Рядок із Початком і Кінцем — від імені інтеграції тим самим патчером.</summary>
    private async Task AddRowAsync(Stand stand, string rowKey, DateTime start, DateTime end)
    {
        await using var scope = stand.Provider.CreateAsyncScope();
        using var author = await scope.ServiceProvider.GetRequiredService<IntegrationActor>().EnterAsync(CancellationToken.None);

        var result = await scope.ServiceProvider.GetRequiredService<ICellPatcher>().ApplyIntegrationRowsAsync(
            stand.DocumentId,
            stand.InstanceId,
            new PeriodKey(202601),
            [new IntegrationRowUpsert(rowKey, [
                new IntegrationRowCell(stand.StartColumn, IntegrationValue.Date(start)),
                new IntegrationRowCell(stand.EndColumn, IntegrationValue.Date(end)),
            ])],
            CancellationToken.None);
        Assert.Equal(2, result.Applied);
    }

    private async Task HumanWriteAsync(Stand stand, string rowKey, PatchCell cell)
    {
        await using var scope = stand.Provider.CreateAsyncScope();
        using var author = scope.ServiceProvider.GetRequiredService<JobActorScope>()
            .Enter(new JobActor(stand.HumanId, "human", "en", [], Guid.NewGuid().ToString("N")));

        var rows = await scope.ServiceProvider.GetRequiredService<IRowStore>()
            .GetRowsAsync(stand.InstanceId, new PeriodKey(202601), CancellationToken.None);
        var version = rows.Single(r => r.RowKey == rowKey).RowVersion;

        await scope.ServiceProvider.GetRequiredService<PatchCellsHandler>().HandleAsync(
            new PatchCellsRequest(
                stand.InstanceId, 202601, CellChangeOrigins.UserEdit, [new PatchRow(rowKey, version, [cell])]),
            CancellationToken.None);
    }

    private static async Task<int> CreateWriterRoleAsync(EcrDbContext db, int projectId)
    {
        var role = new Role(
            EcrCode.Create($"A1{Guid.NewGuid():N}"[..16]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "A1 human writer (test)" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync(CancellationToken.None);

        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, projectId, GrantLevel.Write));
        await db.SaveChangesAsync(CancellationToken.None);

        return role.Id;
    }

    private static async Task<int> AddHumanAsync(EcrDbContext db, int roleId)
    {
        var human = new User($"human_{Guid.NewGuid():N}"[..20], "Test human", AuthProvider.Local);
        human.SetPassword("not-a-real-hash"); // CK_User_Provider: локальному — хеш.
        db.Users.Add(human);
        await db.SaveChangesAsync(CancellationToken.None);

        db.RoleAssignments.Add(new RoleAssignment(roleId, human.Id, principalSid: null));
        await db.SaveChangesAsync(CancellationToken.None);

        return human.Id;
    }

    private Task RevokeAsync(int roleId)
        => ExecuteAsync(
            $"DELETE FROM sec.ResourceGrant WHERE RoleId = {roleId}; "
            + $"DELETE FROM sec.RoleAssignment WHERE RoleId = {roleId}; "
            + $"DELETE FROM sec.Role WHERE Id = {roleId};");

    // ── Читання стану ────────────────────────────────────────────────────────

    private sealed record Stored(string? Text, decimal? Numeric, DateTime? Date, long? EntryId);

    private async Task<List<RowWindowValue>> ValuesAsync(Stand stand)
    {
        await using var db = sql.CreateContext();
        return await db.RowWindowValues.AsNoTracking().Where(v => v.TableInstanceId == stand.InstanceId).ToListAsync();
    }

    private async Task<Dictionary<int, Stored>> CellsAsync(Stand stand, string rowKey)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT v.ColumnDefId, v.ValueString, v.ValueNumeric, v.ValueDate, v.ValueRegistryEntryId "
            + "FROM doc.CellValue v JOIN doc.TableRow r ON r.PeriodKey = v.PeriodKey AND r.Id = v.TableRowId "
            + "WHERE r.TableInstanceId = @instance AND r.RowKey = @rowKey";
        command.Parameters.AddWithValue("@instance", stand.InstanceId);
        command.Parameters.AddWithValue("@rowKey", rowKey);
        await using var reader = await command.ExecuteReaderAsync();

        var result = new Dictionary<int, Stored>();
        while (await reader.ReadAsync())
        {
            result[reader.GetInt32(0)] = new Stored(
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetDecimal(2),
                reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                reader.IsDBNull(4) ? null : Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture));
        }

        return result;
    }

    private async Task ExecuteAsync(string statement)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync();
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary><c>Ecr.Api.Auth.CurrentUser</c> поза HTTP-запитом: анонімний.</summary>
    private sealed class NoHttpRequestUser : ICurrentUser
    {
        public int? UserId => null;

        public string? UserName => null;

        public string CorrelationId
            => throw new InvalidOperationException("ICurrentUser використано поза запитом: HttpContext немає.");

        public string Language => "en";

        public IReadOnlyList<string> GroupSids => [];
    }
}
