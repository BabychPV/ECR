// tests/Ecr.Infrastructure.Tests/Jobs/RegistrySyncValidityJobTests.cs
using Ecr.Application;
using Ecr.Application.Common;
using Ecr.Application.Integration.RegistrySync;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Синк довідника пише вікно дії з дат AF (<c>D-212</c> (8), PR-7): створення й оновлення з датами,
/// зміна вікна з перерахунком <c>IsOrphaned</c> у транзакції спроби, невалідна дата, нетемпоральний
/// довідник, пояс AF з конфігурації.
/// </summary>
/// <remarks>
/// ⚠ Справжній SQL Server і контейнер як у проді; підроблені лише джерело й обгортка сканера
/// (<see cref="SpyScanner"/> кличе справжній <c>OrphanScanner</c> і фіксує, ЩО він бачив: чи є
/// транзакція і яке вікно запису вже збережене).
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistrySyncValidityJobTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 9, 30, 7, 0, 0, DateTimeKind.Utc);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-212")]
    public async Task Створення_й_оновлення_з_датами_пишуть_вікно_а_повтор_нічого_не_змінює()
    {
        var stand = await ArrangeAsync(temporal: true);
        stand.Put("Stack1", "Start", "2024-01-01T00:00:00.0000000Z");
        stand.Put("Stack1", "End", "2024-12-31");
        var g9 = stand.Add("Stack9");
        stand.Put("Stack9", "Start", "2026-01-01");
        var rescans = new List<Rescan>();
        await using var provider = BuildProvider(rescans);

        try
        {
            await RunAsync(provider, stand);

            // Кінець включний (ValidToInclusive) → виключний 2025-01-01.
            Assert.Equal(new ValidityWindow(new DateOnly(2024, 1, 1), new DateOnly(2025, 1, 1)), await WindowAsync(stand.E1));
            var created = await EntryAsync(stand, g9);
            Assert.NotNull(created);
            Assert.Equal(new ValidityWindow(new DateOnly(2026, 1, 1), null), created.Window);
            Assert.Equal(1, await CountAsync(KeyQuery(stand, g9)));

            // Аудит @validity від svc-integration; IsOrphaned перераховано для E1 (не для нового) —
            // у транзакції, уже з новим вікном.
            Assert.Equal(1, await CountAsync(ValidityAuditQuery(stand.E1, stand.SvcId)));
            var rescan = Assert.Single(rescans);
            Assert.Equal(new Rescan(stand.E1, InTransaction: true, new DateOnly(2025, 1, 1)), rescan);
            Assert.DoesNotContain(await EventsAsync(stand), e => e.Status == CollectionCoverage.RegistryValueRejected);

            // ── Повтор із тим самим джерелом: ні ревізії, ні аудиту, ні перерахунку.
            var revision = await RevisionAsync(stand);
            await RunAsync(provider, stand);
            Assert.Equal(revision, await RevisionAsync(stand));
            Assert.Equal(1, await CountAsync(ValidityAuditQuery(stand.E1, stand.SvcId)));
            Assert.Single(rescans);
        }
        finally
        {
            await DeactivateAsync(stand);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-212")]
    public async Task Зміна_вікна_в_джерелі_звужує_запис_а_невалідна_дата_лише_подія()
    {
        var stand = await ArrangeAsync(temporal: true);
        stand.Put("Stack1", "Start", "2024-01-01");
        stand.Put("Stack1", "End", "2025-12-31");
        var rescans = new List<Rescan>();
        await using var provider = BuildProvider(rescans);

        try
        {
            await RunAsync(provider, stand);
            Assert.Equal(new ValidityWindow(new DateOnly(2024, 1, 1), new DateOnly(2026, 1, 1)), await WindowAsync(stand.E1));

            // AF звузив вікно: кінець 30 червня 2025 (включно).
            stand.Put("Stack1", "End", "2025-06-30");
            await RunAsync(provider, stand);
            Assert.Equal(new ValidityWindow(new DateOnly(2024, 1, 1), new DateOnly(2025, 7, 1)), await WindowAsync(stand.E1));
            Assert.Equal(2, rescans.Count);
            Assert.Equal(new DateOnly(2025, 7, 1), rescans[1].SeenValidTo);

            // Дата не ISO: межа не чіпається, подія ValueRejected з ключем каталогу; повтор — без дубля.
            stand.Put("Stack1", "End", "31.12.2025");
            await RunAsync(provider, stand);
            await RunAsync(provider, stand);

            Assert.Equal(new ValidityWindow(new DateOnly(2024, 1, 1), new DateOnly(2025, 7, 1)), await WindowAsync(stand.E1));
            var rejected = Assert.Single(await EventsAsync(stand), e => e.Status == CollectionCoverage.RegistryValueRejected);
            Assert.Contains($"field={RegistrySyncValidity.ToFieldCode}", rejected.Details, StringComparison.Ordinal);
            Assert.Contains($"messageKey={RegistrySyncValidity.DateInvalidKey}", rejected.Details, StringComparison.Ordinal);
            Assert.Equal(2, rescans.Count);
        }
        finally
        {
            await DeactivateAsync(stand);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-212")]
    public async Task Нетемпоральний_довідник_дат_не_читає_і_вікна_не_пише()
    {
        // Політика з датами на нетемпоральному — те, що PUT …/policy відхиляє 422; тут вона вже в
        // базі (довідник міг перестати бути темпоральним). Невалідна дата не дає навіть події.
        var stand = await ArrangeAsync(temporal: false);
        stand.Put("Stack1", "Start", "не дата");
        stand.Put("Stack1", "End", "2024-12-31");
        var rescans = new List<Rescan>();
        await using var provider = BuildProvider(rescans);

        try
        {
            await RunAsync(provider, stand);

            Assert.Equal(ValidityWindow.Always, await WindowAsync(stand.E1));
            Assert.Empty(rescans);
            Assert.DoesNotContain(await EventsAsync(stand), e => e.Status == CollectionCoverage.RegistryValueRejected);
        }
        finally
        {
            await DeactivateAsync(stand);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-212")]
    public async Task Невідомий_пояс_AF_у_конфігурації_зупиняє_прогін_з_ім_ям_ключа()
    {
        var stand = await ArrangeAsync(temporal: true);
        stand.Put("Stack1", "Start", "2024-01-01");
        await using var provider = BuildProvider([]);

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { [RegistrySyncValidity.TimeZoneKey] = "Mars/Olympus_Mons" })
                .Build();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(provider, stand, configuration));
            Assert.Contains(RegistrySyncValidity.TimeZoneKey, ex.Message, StringComparison.Ordinal);
            Assert.Equal(ValidityWindow.Always, await WindowAsync(stand.E1));
        }
        finally
        {
            await DeactivateAsync(stand);
        }
    }

    // ─── Стенд ──────────────────────────────────────────────────────────────

    /// <summary>
    /// External-довідник (темпоральний за <paramref name="temporal"/>) із полем CAP, запис E1 ↔ g1 (Stack1,
    /// CAP = 10 і в джерелі — значення не змінюється). Політика сутності: дати з атрибутів Start/End,
    /// кінець включний.
    /// </summary>
    private async Task<Stand> ArrangeAsync(bool temporal)
    {
        await using var db = Context();

        var registry = new RegistryDef(EcrCode.Create($"SYNC7D_{_tag}"), Text("Stacks"), isTemporal: temporal);
        registry.SwitchSource(RegistrySourceKind.External);
        registry.UseCodeMode(RegistryCodeMode.Auto); // Q6=C: автостворення лише в Auto
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();

        var cap = new RegistryFieldDef(registry.Id, EcrCode.Create("CAP"), Text("Capacity"), CellDataType.Decimal, 1);
        db.RegistryFieldDefs.Add(cap);

        var dataSource = new DataSource(
            EcrCode.Create($"RS7D{_tag}"), Text("PI AF"), ExternalTransport.PiWebApi, "https://af.test", "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync();

        var parent = $@"\\AF\Db\Plant7D_{_tag}";
        var entity = new SourceEntity(dataSource.Id, $"Plant7D_{_tag}", RegistrySourceKind.External);
        entity.Describe("Plant", parent);
        entity.BindRegistry(registry.Id);
        entity.ConfigureRegistrySync(RegistryMissingPolicy.MarkOrphaned, "Start", "End", validToInclusive: true);
        db.SourceEntities.Add(entity);

        var svcId = await db.Users.Where(u => u.UserName == IntegrationActor.UserName).Select(u => u.Id).FirstAsync();

        var e1 = new RegistryEntry(registry.Id, EcrCode.Create("E1"), Text("E1"));
        db.RegistryEntries.Add(e1);
        await db.SaveChangesAsync();

        var v1 = new RegistryValue(e1.Id, cap.Id);
        v1.Set(CellDataType.Decimal, 10m, null);
        v1.MarkChangedBy(svcId);
        db.RegistryValues.Add(v1);

        var g1 = Guid.NewGuid().ToString("D");
        var k1 = new RegistryExternalKey(e1.Id, dataSource.Id, g1);
        k1.MarkSynced($@"{parent}\Stack1", Now.AddDays(-1));
        db.RegistryExternalKeys.Add(k1);
        db.EntityFieldMaps.Add(EntityFieldMap.ToRegistryField(entity.Id, "Capacity", cap.Id));
        await db.SaveChangesAsync();

        var stand = new Stand(entity.Id, registry.Id, dataSource.Id, svcId, parent, e1.Id);
        stand.Add("Stack1", g1);
        return stand;
    }

    private async Task RunAsync(ServiceProvider provider, Stand stand, IConfiguration? configuration = null)
    {
        await using var db = Context();
        var job = new RegistrySyncJob(
            db,
            [new FakeSource(stand)],
            new IntegrationActor(db, new JobActorScope()),
            new TestClock(Now),
            provider.GetRequiredService<IServiceScopeFactory>(),
            configuration);

        await job.ExecuteAsync(stand.EntityId, CancellationToken.None);
    }

    private ServiceProvider BuildProvider(List<Rescan> rescans)
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
        services.AddScoped<IOrphanScanner>(sp => new SpyScanner(
            ActivatorUtilities.CreateInstance<OrphanScanner>(sp), sp.GetRequiredService<EcrDbContext>(), rescans));

        return services.BuildServiceProvider();
    }

    private async Task DeactivateAsync(Stand stand)
    {
        await using var db = Context();
        var entity = await db.SourceEntities.FirstAsync(e => e.Id == stand.EntityId);
        entity.Deactivate();
        await db.SaveChangesAsync();
    }

    private async Task<ValidityWindow> WindowAsync(long entryId)
    {
        await using var db = Context();
        var entry = await db.RegistryEntries.AsNoTracking().SingleAsync(e => e.Id == entryId);
        return entry.Window;
    }

    /// <summary>Автостворений запис елемента: код із послідовності (Auto), тож шукаємо за ключем.</summary>
    private async Task<RegistryEntry?> EntryAsync(Stand stand, string externalId)
    {
        await using var db = Context();
        var entryId = await db.RegistryExternalKeys.AsNoTracking()
            .Where(k => k.DataSourceId == stand.DataSourceId && k.ExternalId == externalId)
            .Select(k => (long?)k.RegistryEntryId)
            .SingleOrDefaultAsync();
        return entryId is null ? null : await db.RegistryEntries.AsNoTracking().SingleAsync(e => e.Id == entryId);
    }

    private async Task<long> RevisionAsync(Stand stand)
    {
        await using var db = Context();
        return await db.RegistryDefs.AsNoTracking().Where(d => d.Id == stand.RegistryId).Select(d => d.DataRevision).SingleAsync();
    }

    private async Task<List<CollectionCoverage>> EventsAsync(Stand stand)
    {
        await using var db = Context();
        return await db.CollectionCoverages.AsNoTracking()
            .Where(c => c.SourceEntityId == stand.EntityId && c.Status != null)
            .OrderBy(c => c.Id)
            .ToListAsync();
    }

    private async Task<int> CountAsync(string query)
    {
        await using var db = Context();
        return await db.Database.SqlQueryRaw<int>(query).SingleAsync();
    }

    // Ідентифікатори — числа й GUID зі стенду, не ввід: підстановка в текст безпечна.
    private static string ValidityAuditQuery(long entryId, int userId)
        => "SELECT COUNT(*) AS [Value] FROM aud.SecurityEvent WHERE EventType = N'"
           + RegistryEntryWriter.ValueChangedEventType + "' AND ChangedByUserId = " + userId
           + " AND DetailsJson LIKE N'%\"entryId\":" + entryId + ",%'"
           + " AND DetailsJson LIKE N'%\"field\":\"" + RegistryEntryWriter.ValidityFieldCode + "\"%'";

    private static string KeyQuery(Stand stand, string externalId)
        => "SELECT COUNT(*) AS [Value] FROM dic.RegistryExternalKey WHERE DataSourceId = " + stand.DataSourceId
           + " AND ExternalId = N'" + externalId + "'";

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>Що бачив сканер: запис, чи є транзакція, збережений виключний кінець вікна.</summary>
    private sealed record Rescan(long EntryId, bool InTransaction, DateOnly? SeenValidTo);

    private sealed record Stand(int EntityId, int RegistryId, int DataSourceId, int SvcId, string Parent, long E1)
    {
        public List<SourceElement> Elements { get; } = [];

        public Dictionary<string, SourceDataPoint> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Елемент під коренем сутності (адреса = шлях, як PI Web API) з CAP = 10.</summary>
        public string Add(string name, string? externalId = null)
        {
            var id = externalId ?? Guid.NewGuid().ToString("D");
            var path = $@"{Parent}\{name}";
            Elements.Add(new SourceElement(id, name, path, path));
            var cap = $@"{path}|Capacity";
            Values[cap] = new SourceDataPoint(cap, Now, 10m, null, null, "Good");
            return id;
        }

        /// <summary>Текстове значення атрибута — так дату віддає PI SQL Client (<c>"O"</c>).</summary>
        public void Put(string name, string attribute, string value)
        {
            var path = $@"{Parent}\{name}|{attribute}";
            Values[path] = new SourceDataPoint(path, Now, null, value, null, "Good");
        }
    }

    /// <summary>Справжній сканер; фіксує, чи йде він у транзакції спроби й чи бачить уже нове вікно.</summary>
    private sealed class SpyScanner(IOrphanScanner inner, EcrDbContext db, List<Rescan> rescans) : IOrphanScanner
    {
        public Task<OrphanScanSummary> ScanAllAsync(CancellationToken ct) => inner.ScanAllAsync(ct);

        public async Task<int> RescanForEntryAsync(long registryEntryId, CancellationToken ct)
        {
            var validTo = await db.RegistryEntries.AsNoTracking()
                .Where(e => e.Id == registryEntryId).Select(e => e.ValidTo).SingleAsync(ct);
            rescans.Add(new Rescan(registryEntryId, db.Database.CurrentTransaction is not null, validTo));
            return await inner.RescanForEntryAsync(registryEntryId, ct);
        }
    }

    /// <summary>Атрибута немає в словнику — джерело про нього нічого не сказало (знімок лишається повним).</summary>
    private sealed class FakeSource(Stand stand) : IExternalDataSource
    {
        public ExternalTransport Transport => ExternalTransport.PiWebApi;

        public Task<IReadOnlyList<SourceEntityDescriptor>> DiscoverAsync(int dataSourceId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SourceEntityDescriptor>>([]);

        public Task<SourceElementsResult> DiscoverElementsAsync(int dataSourceId, string root, CancellationToken ct)
            => Task.FromResult(new SourceElementsResult([.. stand.Elements], IsComplete: true));

        public Task<CollectionResult> ReadAsync(CollectionRequest request, CancellationToken ct)
            => throw new InvalidOperationException("Синк довідника не читає часових рядів.");

        public Task<CurrentValuesResult> ReadCurrentAsync(
            int dataSourceId, IReadOnlyCollection<string> paths, CancellationToken ct)
            => Task.FromResult(new CurrentValuesResult(
                [.. paths.Where(stand.Values.ContainsKey).Select(p => stand.Values[p])], []));
    }

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
