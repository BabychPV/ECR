// tests/Ecr.Infrastructure.Tests/Jobs/RegistrySyncApplyTests.cs
using Ecr.Application;
using Ecr.Application.Common;
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
/// Синк довідника ЗАСТОСОВУЄ оновлення (FEATURE-REGISTRY-SYNC S7, <c>ФВ-8.10</c>, <c>ФВ-8.11</c>):
/// для <c>External</c>/<c>Hybrid</c> — через <see cref="RegistryEntryWriter.UpdateAsync"/> від
/// <c>svc-integration</c>, у власному DI-scope; <c>Local</c> — лише звірка.
/// </summary>
/// <remarks>
/// ⚠ Справжній SQL Server і справжній контейнер (<c>AddEcrApplication</c> +
/// <c>AddEcrInfrastructure</c> + <see cref="JobAwareCurrentUser"/>, як у <c>Program.cs</c>): writer,
/// одиниця роботи, аудит і темпоральна історія — ті самі, що в проді. Підроблені лише джерело й
/// каталог.
/// <para>
/// Мутаційні докази (у власному worktree, 2026-09-28; кожна мутація окремо, після неї — відкат).
/// ⚠ Станом на коміт прогнано лише М1; М2–М4 — сформульовані, мутаційно ще НЕ доведені
/// (.NET-слот забрано під P0).
/// </para>
/// <list type="bullet">
/// <item>М1 — обхід writer'а: у <c>RegistrySyncJob.ApplyUpdatesAsync</c> замість
/// <c>TryWriteAsync</c> записати значення напряму у відстежений <c>RegistryValue</c> зовнішнього
/// контексту → <see cref="External_пише_через_writer_від_svc_integration_і_оновлює_шлях"/> червоний
/// (немає події аудиту <c>RegistryValueChanged</c> від <c>svc-integration</c>, ревізія не зросла).</item>
/// <item>М2 — без дедупу: <c>DeduplicateAsync</c> повертає всі події →
/// <see cref="Невідомий_автор_це_людина_значення_лишається_і_одна_подія_за_два_прогони"/> і
/// <see cref="External_пише_через_writer_від_svc_integration_і_оновлює_шлях"/> червоні (подія
/// повторилась на другому прогоні).</item>
/// <item>М3 — <c>null</c>-автор як не-людина (<c>IsHuman</c> → <c>changedByUserId is { } by &amp;&amp; by != svcId</c>) →
/// <see cref="Невідомий_автор_це_людина_значення_лишається_і_одна_подія_за_два_прогони"/> червоний
/// (значення перетерто).</item>
/// <item>М4 — <c>Local</c> пише (умова гілки запису включає <c>Local</c>) →
/// <see cref="Local_нічого_не_пише_за_два_прогони"/> червоний (відбиток <c>dic.*</c> змінився:
/// шлях ключа).</item>
/// </list>
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistrySyncApplyTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 9, 28, 7, 0, 0, DateTimeKind.Utc);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.11")]
    public async Task External_пише_через_writer_від_svc_integration_і_оновлює_шлях()
    {
        var stand = await ArrangeAsync(RegistrySourceKind.External, e1Author: Author.Svc);
        await using var provider = BuildProvider();

        try
        {
            var revision = await RevisionAsync(stand);
            var historyBefore = await CountAsync(HistoryQuery(stand));

            await RunAsync(provider, stand);

            // Значення — з джерела, автор — svc-integration (RT-04).
            var values = await CapValuesAsync(stand);
            Assert.Equal(V(12.5m, stand.SvcId), values[stand.E1]);
            Assert.Equal(V(30m, stand.SvcId), values[stand.E2]);

            // ⛔ Через writer: ревізія (RT-03) +1 на пакет, подія аудиту від svc-integration
            // на кожен запис, у темпоральній історії — попередні версії обох значень.
            Assert.Equal(revision + 1, await RevisionAsync(stand));
            Assert.Equal(1, await CountAsync(AuditQuery(stand.E1, stand.SvcId)));
            Assert.Equal(1, await CountAsync(AuditQuery(stand.E2, stand.SvcId)));
            Assert.Equal(historyBefore + 2, await CountAsync(HistoryQuery(stand)));

            // Зміна шляху — MarkSynced(newPath).
            Assert.Equal($@"{stand.Parent}\Stack1", await PathAsync(stand.E1));

            var events = await EventsAsync(stand.EntityId);
            Assert.DoesNotContain(events, e => e.Status == CollectionCoverage.RegistryPendingUpdate);
            Assert.Single(events, e => e.Status == CollectionCoverage.RegistryElementUnlinked);

            // ── Повторний прогін: джерело те саме → нічого не змінюється, подій не додається.
            var fingerprint = await FingerprintAsync(stand);
            var eventCount = events.Count;

            await RunAsync(provider, stand);

            Assert.Equal(fingerprint, await FingerprintAsync(stand));
            Assert.Equal(eventCount, (await EventsAsync(stand.EntityId)).Count);

            // ── Джерело змінилось: значення, записане синком, не «приклеїлось» як людське (D-118
            // в обидва боки) — синк пише знову, а історія містить його попередню версію від svc.
            stand.Values[$@"{stand.Parent}\Stack1|Capacity"] = Point($@"{stand.Parent}\Stack1|Capacity", 13m);

            await RunAsync(provider, stand);

            Assert.Equal(V(13m, stand.SvcId), (await CapValuesAsync(stand))[stand.E1]);
            Assert.Equal(1, await CountAsync(
                $"SELECT COUNT(*) AS [Value] FROM dic.RegistryValueHistory WHERE RegistryEntryId = {stand.E1} "
                + $"AND RegistryFieldDefId = {stand.CapId} AND ValueNumeric = 12.5 AND ChangedByUserId = {stand.SvcId}"));
        }
        finally
        {
            await DeactivateAsync(stand);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.11")]
    public async Task Невідомий_автор_це_людина_значення_лишається_і_одна_подія_за_два_прогони()
    {
        var stand = await ArrangeAsync(RegistrySourceKind.External, e1Author: Author.Unknown);
        await using var provider = BuildProvider();

        try
        {
            await RunAsync(provider, stand);
            await RunAsync(provider, stand);

            // ⛔ D-118, дефолт координатора: автор невідомий (null) = людина — значення лишається.
            var values = await CapValuesAsync(stand);
            Assert.Equal(V(10m, null), values[stand.E1]);

            // Контроль: сусідній запис від svc-integration оновлено.
            Assert.Equal(V(30m, stand.SvcId), values[stand.E2]);

            var kept = (await EventsAsync(stand.EntityId))
                .Where(e => e.Status == CollectionCoverage.RegistryConflictKeptManual)
                .ToList();
            var single = Assert.Single(kept);
            Assert.Contains($"entry={stand.E1}", single.Details, StringComparison.Ordinal);
            Assert.Contains("source=12.5", single.Details, StringComparison.Ordinal);
        }
        finally
        {
            await DeactivateAsync(stand);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.11")]
    public async Task Відмова_writer_для_одного_запису_не_блокує_решту_пакета()
    {
        var stand = await ArrangeAsync(RegistrySourceKind.Hybrid, e1Author: Author.Svc, withRef: true);
        await using var provider = BuildProvider();

        try
        {
            await RunAsync(provider, stand);

            // E1: Lookup на неіснуючий запис — тип планувальник пропустив (це число), а writer
            // відхилив ціль. E1 не записано зовсім (все або нічого на запис), E2 — записано.
            var values = await CapValuesAsync(stand);
            Assert.Equal(V(10m, stand.SvcId), values[stand.E1]);
            Assert.Equal(V(30m, stand.SvcId), values[stand.E2]);

            var rejected = (await EventsAsync(stand.EntityId))
                .Where(e => e.Status == CollectionCoverage.RegistryValueRejected)
                .ToList();
            var single = Assert.Single(rejected);
            Assert.Contains($"entry={stand.E1}", single.Details, StringComparison.Ordinal);
            Assert.Contains("messageKey=err.ECR-REG-0422.lookupEntryNotFound", single.Details, StringComparison.Ordinal);
            Assert.Contains("error=ECR-REG-0422", single.Details, StringComparison.Ordinal);

            // Повтор: відмова не дублюється (дедуп), E2 уже не змінюється.
            await RunAsync(provider, stand);
            Assert.Single(
                await EventsAsync(stand.EntityId),
                e => e.Status == CollectionCoverage.RegistryValueRejected);
        }
        finally
        {
            await DeactivateAsync(stand);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.10")]
    public async Task Local_нічого_не_пише_за_два_прогони()
    {
        var stand = await ArrangeAsync(RegistrySourceKind.Local, e1Author: Author.Svc);
        await using var provider = BuildProvider();

        try
        {
            var before = await FingerprintAsync(stand);

            await RunAsync(provider, stand);
            var afterFirst = (await EventsAsync(stand.EntityId)).Count;
            await RunAsync(provider, stand);

            // ⛔ D-49: Local — лише звірка: ні значень, ні ревізії, ні шляху, ні історії.
            Assert.Equal(before, await FingerprintAsync(stand));
            Assert.Equal(0, await CountAsync(AuditQuery(stand.E1, stand.SvcId)));

            // Розбіжності й очікувана зміна шляху — події, і лише раз за два прогони.
            var events = await EventsAsync(stand.EntityId);
            Assert.Equal(afterFirst, events.Count);
            Assert.Contains(events, e => e.Status == CollectionCoverage.RegistryDiverged);
            Assert.Single(events, e => e.Status == CollectionCoverage.RegistryPendingUpdate);
        }
        finally
        {
            await DeactivateAsync(stand);
        }
    }

    // ─── Стенд ──────────────────────────────────────────────────────────────

    private enum Author
    {
        Svc,
        Unknown,
    }

    /// <summary>
    /// Довідник (CAP Decimal; з <paramref name="withRef"/> — ще REF Lookup на себе), записи E1 ↔ g1
    /// (шлях застарів) і E2 ↔ g2, обидва в джерелі; g9 у джерелі без зв'язку. E1.CAP = 10 (автор
    /// <paramref name="e1Author"/>), E2.CAP = 20 (svc). Джерело: g1 12.5, g2 30; з
    /// <paramref name="withRef"/> — g1.Ref = неіснуючий Id, g2.Ref — Id самого E2 (живий запис).
    /// </summary>
    private async Task<Stand> ArrangeAsync(RegistrySourceKind kind, Author e1Author, bool withRef = false)
    {
        await using var db = Context();

        var registry = new RegistryDef(EcrCode.Create($"SYNC7_{_tag}"), Text("Stacks"), isTemporal: false);
        registry.SwitchSource(kind);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();

        var cap = new RegistryFieldDef(registry.Id, EcrCode.Create("CAP"), Text("Capacity"), CellDataType.Decimal, 1);
        var reference = new RegistryFieldDef(registry.Id, EcrCode.Create("REF"), Text("Ref"), CellDataType.Lookup, 2);
        reference.PointTo(registry.Id);
        db.RegistryFieldDefs.AddRange(cap, reference);
        await db.SaveChangesAsync();

        var dataSource = new DataSource(
            EcrCode.Create($"RS7{_tag}"), Text("PI AF"), ExternalTransport.PiWebApi, "https://af.test", "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync();

        var parent = $@"\\AF\Db\Plant7_{_tag}";
        var entity = new SourceEntity(dataSource.Id, $"Plant7_{_tag}", RegistrySourceKind.External);
        entity.Describe("Plant", parent);
        entity.BindRegistry(registry.Id);
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync();

        var svcId = await db.Users.Where(u => u.UserName == IntegrationActor.UserName).Select(u => u.Id).FirstAsync();

        var e1 = new RegistryEntry(registry.Id, EcrCode.Create("E1"), Text("E1"));
        var e2 = new RegistryEntry(registry.Id, EcrCode.Create("E2"), Text("E2"));
        db.RegistryEntries.AddRange(e1, e2);
        await db.SaveChangesAsync();

        var v1 = new RegistryValue(e1.Id, cap.Id);
        v1.Set(CellDataType.Decimal, 10m, null);
        v1.MarkChangedBy(e1Author == Author.Svc ? svcId : null);
        var v2 = new RegistryValue(e2.Id, cap.Id);
        v2.Set(CellDataType.Decimal, 20m, null);
        v2.MarkChangedBy(svcId);
        db.RegistryValues.AddRange(v1, v2);

        var g1 = Guid.NewGuid().ToString("D");
        var g2 = Guid.NewGuid().ToString("D");
        var g9 = Guid.NewGuid().ToString("D");

        var k1 = new RegistryExternalKey(e1.Id, dataSource.Id, g1);
        k1.MarkSynced($@"\\AF\Db\Old_{_tag}\Stack1", Now.AddDays(-1));
        var k2 = new RegistryExternalKey(e2.Id, dataSource.Id, g2);
        k2.MarkSynced($@"{parent}\Stack2", Now.AddDays(-1));
        db.RegistryExternalKeys.AddRange(k1, k2);

        db.EntityFieldMaps.Add(EntityFieldMap.ToRegistryField(entity.Id, "Capacity", cap.Id));
        if (withRef)
        {
            db.EntityFieldMaps.Add(EntityFieldMap.ToRegistryField(entity.Id, "Ref", reference.Id));
        }

        await db.SaveChangesAsync();

        var children = new List<SourceEntityDescriptor>
        {
            new("Stack1", null, $@"{parent}\Stack1", null, "Element", g1),
            new("Stack2", null, $@"{parent}\Stack2", null, "Element", g2),
            new("Stack9", null, $@"{parent}\Stack9", null, "Element", g9),
        };

        var values = new Dictionary<string, SourceDataPoint>(StringComparer.OrdinalIgnoreCase);
        void Put(string path, decimal value) => values[path] = Point(path, value);
        Put($@"{parent}\Stack1|Capacity", 12.5m);
        Put($@"{parent}\Stack2|Capacity", 30m);
        Put($@"{parent}\Stack9|Capacity", 3m);

        if (withRef)
        {
            Put($@"{parent}\Stack1|Ref", 999_999_999_999m);
            Put($@"{parent}\Stack2|Ref", e2.Id);
            Put($@"{parent}\Stack9|Ref", e2.Id);
        }

        return new Stand(entity.Id, registry.Id, cap.Id, parent, e1.Id, e2.Id, svcId, children, values);
    }

    private static SourceDataPoint Point(string path, decimal value) => new(path, Now, value, null, null, "Good");

    private static (decimal? Value, int? Author) V(decimal value, int? author) => (value, author);

    private async Task RunAsync(ServiceProvider provider, Stand stand)
    {
        await using var db = Context();
        var scope = new JobActorScope();
        var job = new RegistrySyncJob(
            db,
            [new FakeSource(stand.Values)],
            new FakeCatalog(stand.Children),
            new IntegrationActor(db, scope),
            new TestClock(Now),
            provider.GetRequiredService<IServiceScopeFactory>());

        await job.ExecuteAsync(stand.EntityId, CancellationToken.None);
    }

    /// <summary>Контейнер як у проді: застосунок + інфраструктура + обгортка автора задачі.</summary>
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

        // Те саме, що `Program.cs` (F-01): автор задачі поверх користувача «поза запитом».
        services.AddScoped<JobActorScope>();
        services.AddScoped<ICurrentUser>(sp => new JobAwareCurrentUser(
            new NoHttpRequestUser(), sp.GetRequiredService<JobActorScope>()));

        // ⚠ Quartz тримає планувальник у глобальному репозиторії за ім'ям — підробка, як у
        // MaterializeIntegrationActorTests.
        services.AddScoped(_ => Substitute.For<IBackgroundJobScheduler>());

        return services.BuildServiceProvider();
    }

    /// <summary>Вимикає сутність після тесту: активна без прогону жовтить SourcesHealthCheck.</summary>
    private async Task DeactivateAsync(Stand stand)
    {
        await using var db = Context();
        var entity = await db.SourceEntities.FirstAsync(e => e.Id == stand.EntityId);
        entity.Deactivate();
        await db.SaveChangesAsync();
    }

    private async Task<Dictionary<long, (decimal? Value, int? Author)>> CapValuesAsync(Stand stand)
    {
        await using var db = Context();
        return await db.RegistryValues.AsNoTracking()
            .Where(v => (v.RegistryEntryId == stand.E1 || v.RegistryEntryId == stand.E2) && v.RegistryFieldDefId == stand.CapId)
            .ToDictionaryAsync(v => v.RegistryEntryId, v => (v.ValueNumeric, v.ChangedByUserId));
    }

    private async Task<long> RevisionAsync(Stand stand)
    {
        await using var db = Context();
        return await db.RegistryDefs.AsNoTracking().Where(d => d.Id == stand.RegistryId).Select(d => d.DataRevision).FirstAsync();
    }

    private async Task<string?> PathAsync(long entryId)
    {
        await using var db = Context();
        return await db.RegistryExternalKeys.AsNoTracking()
            .Where(k => k.RegistryEntryId == entryId)
            .Select(k => k.ExternalPath)
            .FirstAsync();
    }

    private async Task<List<CollectionCoverage>> EventsAsync(int entityId)
    {
        await using var db = Context();
        return await db.CollectionCoverages.AsNoTracking()
            .Where(c => c.SourceEntityId == entityId && c.Status != null)
            .OrderBy(c => c.Id)
            .ToListAsync();
    }

    private async Task<int> CountAsync(string query)
    {
        await using var db = Context();
        return await db.Database.SqlQueryRaw<int>(query).SingleAsync();
    }

    // Ідентифікатори — числа зі стенду, не ввід: підстановка в текст безпечна.
    private static string HistoryQuery(Stand stand)
        => $"SELECT COUNT(*) AS [Value] FROM dic.RegistryValueHistory WHERE RegistryEntryId IN ({stand.E1},{stand.E2})";

    private static string AuditQuery(long entryId, int userId)
        => "SELECT COUNT(*) AS [Value] FROM aud.SecurityEvent WHERE EventType = N'"
           + RegistryEntryWriter.ValueChangedEventType + "' AND ChangedByUserId = " + userId
           + " AND DetailsJson LIKE N'%\"entryId\":" + entryId + ",%'";

    /// <summary>Відбиток усього, що синк міг би змінити в <c>dic.*</c>, з історією й аудитом.</summary>
    private async Task<string> FingerprintAsync(Stand stand)
    {
        await using var db = Context();
        long[] ids = [stand.E1, stand.E2];

        var values = await db.RegistryValues.AsNoTracking()
            .Where(v => ids.Contains(v.RegistryEntryId))
            .OrderBy(v => v.Id)
            .Select(v => $"{v.Id}:{v.ValueNumeric}:{v.ValueRefEntryId}:{v.ChangedByUserId}")
            .ToListAsync();

        var keys = await db.RegistryExternalKeys.AsNoTracking()
            .Where(k => ids.Contains(k.RegistryEntryId))
            .OrderBy(k => k.Id)
            .Select(k => $"{k.Id}:{k.ExternalPath}:{k.LastSyncedAt}")
            .ToListAsync();

        var audit = await CountAsync(AuditQuery(stand.E1, stand.SvcId)) + await CountAsync(AuditQuery(stand.E2, stand.SvcId));

        return string.Join(
            "|",
            string.Join(";", values),
            string.Join(";", keys),
            await RevisionAsync(stand),
            await CountAsync(HistoryQuery(stand)),
            audit);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private sealed record Stand(
        int EntityId,
        int RegistryId,
        int CapId,
        string Parent,
        long E1,
        long E2,
        int SvcId,
        IReadOnlyList<SourceEntityDescriptor> Children,
        Dictionary<string, SourceDataPoint> Values);

    /// <summary>Каталог: діти елемента сутності.</summary>
    private sealed class FakeCatalog(IReadOnlyList<SourceEntityDescriptor> children) : ISourceCatalogReader
    {
        public Task<IReadOnlyList<SourceEntityDescriptor>> BrowseAsync(
            int dataSourceId, string? parentPath, CancellationToken ct)
            => Task.FromResult(children);

        public Task<IReadOnlyList<SourceEntityDescriptor>> AttributesAsync(
            int dataSourceId, string elementPath, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SourceEntityDescriptor>>([]);
    }

    /// <summary>Джерело з поточними значеннями (словник живий — тест міняє значення між прогонами).</summary>
    private sealed class FakeSource(IReadOnlyDictionary<string, SourceDataPoint> values) : IExternalDataSource
    {
        public ExternalTransport Transport => ExternalTransport.PiWebApi;

        public Task<IReadOnlyList<SourceEntityDescriptor>> DiscoverAsync(int dataSourceId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SourceEntityDescriptor>>([]);

        public Task<CollectionResult> ReadAsync(CollectionRequest request, CancellationToken ct)
            => throw new InvalidOperationException("Синк довідника не читає часових рядів.");

        public Task<CurrentValuesResult> ReadCurrentAsync(
            int dataSourceId, IReadOnlyCollection<string> paths, CancellationToken ct)
        {
            var found = new List<SourceDataPoint>();
            var failures = new List<CurrentValueFailure>();

            foreach (var path in paths)
            {
                if (values.TryGetValue(path, out var point))
                {
                    found.Add(point);
                }
                else
                {
                    failures.Add(new CurrentValueFailure(path, "ECR-INT-0404", "err.ECR-INT-0404.sourcePathNotFound"));
                }
            }

            return Task.FromResult(new CurrentValuesResult(found, failures));
        }
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
