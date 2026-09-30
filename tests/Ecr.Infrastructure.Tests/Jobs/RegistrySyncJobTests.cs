// tests/Ecr.Infrastructure.Tests/Jobs/RegistrySyncJobTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Integration;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Синк довідника в режимі ЛИШЕ ЗВІРКИ (FEATURE-REGISTRY-SYNC S5, <c>ФВ-8.10</c>,
/// <c>ФВ-8.11</c>): сутність із <c>RegistryDefId</c> іде в <see cref="RegistrySyncJob"/>
/// замість збору, задача читає джерело й довідник, пише події в журнал покриття
/// і для <c>Local</c> НЕ пише нічого в <c>dic.*</c>. Запис для <c>External</c>/<c>Hybrid</c>
/// (S7) — <c>RegistrySyncApplyTests</c>; S5-тест «External лише журналює» знято разом із
/// поведінкою, яку він тримав.
/// </summary>
/// <remarks>
/// ⚠ На справжньому SQL Server: «нічого не записано» перевіряється відбитком
/// рядків <c>dic.RegistryValue</c>/<c>RegistryEntry</c>/<c>RegistryExternalKey</c>,
/// ревізії довідника й кількості рядків системної історії (RT-04) — у пам'яті
/// історії немає, і запис через ExecuteUpdate її оминув би.
/// <para>
/// Мутаційні докази (прогнано руками у власному worktree, 2026-09-28):
/// </para>
/// <list type="bullet">
/// <item>М1 — у <c>CollectionJob.ExecuteAsync</c> прибрати гілку
/// <c>IsRegistryBoundAsync</c> → <see cref="Сутність_з_довідником_іде_в_синк_а_не_в_збір"/>
/// червоний (збирач викликано, синк — ні).</item>
/// <item>М2 — у <c>RegistrySyncJob.ExecuteAsync</c> перед <c>SaveChangesAsync</c>
/// застосувати зміни шляху (<c>RegistryExternalKey.MarkSynced</c> на відстежуваному
/// ключі) → <see cref="Local_довідник_розбіжність_зниклий_і_неприв_язаний_елемент_лише_події"/>
/// червоний на відбитку <c>dic.*</c>.</item>
/// <item>М3 — прибрати з <c>CollectionStore.GetFieldMapsAsync</c> умову
/// <c>TargetKind == Column</c> → <see cref="GetFieldMapsAsync_не_повертає_мапінгів_на_поле_довідника"/>
/// червоний.</item>
/// </list>
/// <para>
/// D-212 PR-1 (2026-09-29): <see cref="PiSqlClient_елемент_є_в_джерелі_не_зниклий_і_читається_за_іменем"/> і
/// <see cref="Нуль_елементів_при_наявних_зв_язках_не_дає_зниклих"/> (обидва транспорти) були ЧЕРВОНІ на коді
/// до фіксу (<c>Assert.DoesNotContain() Failure: Filter matched in collection</c> — хибний
/// <c>RegistrySourceMissing</c>). Мутації: М4 — прибрати запобіжник → червоні обидва випадки
/// «нуль елементів»; М5 — запобіжник лише для PiSqlClient → червоний випадок PiWebApi; М6 — брати
/// елементи з <c>DiscoverAsync</c> з <c>DataType == "Element"</c> → червоний PiSqlClient-тест; М7 — адреса
/// читання <c>Path</c> замість <c>ReadAddress</c> → червоний PiSqlClient-тест; М8 — проковтнути відмову
/// переліку порожнім знімком → червоний <see cref="Відмова_переліку_валить_прогін_з_її_кодом_і_не_пише_зниклих"/>.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistrySyncJobTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 9, 28, 6, 0, 0, DateTimeKind.Utc);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.11")]
    public async Task Local_довідник_розбіжність_зниклий_і_неприв_язаний_елемент_лише_події()
    {
        var stand = await ArrangeAsync(RegistrySourceKind.Local);

        try
        {
            var before = await FingerprintAsync(stand);
            var source = new FakeSource(stand.Values);
            var scope = new JobActorScope();

            await using (var db = Context())
            {
                await Job(db, source, stand, scope).ExecuteAsync(stand.EntityId, CancellationToken.None);
            }

            // ⛔ Звірка не пише в довідник: ні значень, ні ревізії, ні шляху/
            // LastSyncedAt зв'язку, ні рядка системної історії.
            Assert.Equal(before, await FingerprintAsync(stand));

            var events = await EventsAsync(stand.EntityId);

            // Local: CAP 10 ≠ 12.5 → розбіжність; NAME однакове → тиша.
            // g2 немає в повному знімку → зниклий; g9 без зв'язку → неприв'язаний.
            // Зміна шляху g1 не застосовується → очікуване оновлення.
            Assert.Equal(
                [
                    CollectionCoverage.RegistryDiverged,
                    CollectionCoverage.RegistryElementUnlinked,
                    CollectionCoverage.RegistryPendingUpdate,
                    CollectionCoverage.RegistrySourceMissing,
                ],
                events.Select(e => e.Status).Order(StringComparer.Ordinal));

            Assert.All(events, e => Assert.Null(e.PeriodKey));
            Assert.Contains(events, e => e.Status == CollectionCoverage.RegistryDiverged
                                         && e.Details!.Contains("field=CAP", StringComparison.Ordinal)
                                         && e.Details.Contains("ecr=10", StringComparison.Ordinal)
                                         && e.Details.Contains("source=12.5", StringComparison.Ordinal));
            Assert.Contains(events, e => e.Status == CollectionCoverage.RegistrySourceMissing
                                         && e.Details!.Contains(stand.MissingGuid, StringComparison.Ordinal));
            Assert.Contains(events, e => e.Status == CollectionCoverage.RegistryElementUnlinked
                                         && e.Details!.Contains(stand.UnlinkedGuid, StringComparison.Ordinal));
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
    public async Task Відмова_шляху_робить_знімок_неповним_зниклих_немає_а_відмова_в_журналі()
    {
        var stand = await ArrangeAsync(RegistrySourceKind.Local);

        try
        {
            var source = new FakeSource(stand.Values, failPath: $@"{stand.ParentPath}\Stack9|Capacity");

            await using (var db = Context())
            {
                await Job(db, source, stand, new JobActorScope()).ExecuteAsync(stand.EntityId, CancellationToken.None);
            }

            var events = await EventsAsync(stand.EntityId);

            // ⛔ D-187: неповний знімок про зникнення нічого не каже.
            Assert.DoesNotContain(events, e => e.Status == CollectionCoverage.RegistrySourceMissing);
            Assert.Contains(events, e => e.Status == CollectionCoverage.RegistryValueRejected
                                         && e.Details!.Contains("ECR-INT-0404", StringComparison.Ordinal)
                                         && e.Details.Contains(stand.UnlinkedGuid, StringComparison.Ordinal));
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
    public async Task PiSqlClient_елемент_є_в_джерелі_не_зниклий_і_читається_за_іменем()
    {
        var stand = await ArrangeAsync(RegistrySourceKind.Local, ExternalTransport.PiSqlClient);

        try
        {
            var source = new FakeSource(stand.Values, transport: ExternalTransport.PiSqlClient);

            await using (var db = Context())
            {
                await Job(db, source, stand, new JobActorScope()).ExecuteAsync(stand.EntityId, CancellationToken.None);
            }

            var events = await EventsAsync(stand.EntityId);

            // ⛔ D-212 §0: g1 у джерелі Є — «зниклим» він бути не може.
            Assert.DoesNotContain(events, e => e.Status == CollectionCoverage.RegistrySourceMissing
                                               && e.Details!.Contains(stand.Linked1Guid, StringComparison.Ordinal));

            // Значення прочитано за адресою «ім'я|атрибут» → розбіжність CAP 10 ≠ 12.5.
            Assert.Contains(events, e => e.Status == CollectionCoverage.RegistryDiverged
                                         && e.Details!.Contains("source=12.5", StringComparison.Ordinal));

            // g2 справді немає, знімок повний → зниклий пишеться, як і для PI Web API.
            Assert.Contains(events, e => e.Status == CollectionCoverage.RegistrySourceMissing
                                         && e.Details!.Contains(stand.MissingGuid, StringComparison.Ordinal));
        }
        finally
        {
            await DeactivateAsync(stand);
        }
    }

    [Theory]
    [InlineData(ExternalTransport.PiWebApi)]
    [InlineData(ExternalTransport.PiSqlClient)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.10")]
    public async Task Нуль_елементів_при_наявних_зв_язках_не_дає_зниклих(ExternalTransport transport)
    {
        var stand = await ArrangeAsync(RegistrySourceKind.Local, transport);

        try
        {
            // Джерело «повністю» віддало порожній перелік — а зв'язки g1, g2 є.
            var empty = stand with { Children = [], Elements = [] };
            var source = new FakeSource(stand.Values, transport: transport);

            await using (var db = Context())
            {
                await Job(db, source, empty, new JobActorScope()).ExecuteAsync(stand.EntityId, CancellationToken.None);
            }

            // ⛔ Запобіжник: порожній перелік при наявних зв'язках — не доказ, що зникли ВСІ.
            Assert.DoesNotContain(
                await EventsAsync(stand.EntityId), e => e.Status == CollectionCoverage.RegistrySourceMissing);
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
    public async Task Відмова_переліку_валить_прогін_з_її_кодом_і_не_пише_зниклих()
    {
        var stand = await ArrangeAsync(RegistrySourceKind.Local, ExternalTransport.PiSqlClient);

        try
        {
            var refusal = new BusinessRuleException(
                "ECR-INT-0422",
                "Запит типу ElementList для PI SQL Client не налаштовано.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0422.queryKindNotConfigured",
                    ["queryKind"] = "ElementList",
                    ["configKey"] = "PiSqlClient:ElementListQuery",
                });
            var source = new FakeSource(stand.Values, transport: ExternalTransport.PiSqlClient) { ElementsFailure = refusal };

            await using (var db = Context())
            {
                // ⛔ Прогін падає з конвертом адаптера (Failed у черзі), а не мовчки пише «порожній повний знімок».
                var error = await Assert.ThrowsAsync<BusinessRuleException>(
                    () => Job(db, source, stand, new JobActorScope()).ExecuteAsync(stand.EntityId, CancellationToken.None));
                Assert.Equal("ECR-INT-0422", error.ErrorCode);
                Assert.Equal("err.ECR-INT-0422.queryKindNotConfigured", error.Details!["messageKey"]);
            }

            Assert.Empty(await EventsAsync(stand.EntityId));
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
    public async Task Автор_задачі_svc_integration_і_виходить_після_неї()
    {
        var stand = await ArrangeAsync(RegistrySourceKind.Local);

        try
        {
            var source = new FakeSource(stand.Values);
            var scope = new JobActorScope();
            source.Scope = scope;

            await using (var db = Context())
            {
                await Job(db, source, stand, scope).ExecuteAsync(stand.EntityId, CancellationToken.None);
            }

            // Під час читання джерела автор — технічний запис інтеграції.
            Assert.Equal(IntegrationActor.UserName, source.SeenUser);
            Assert.True(source.SeenIntegration);
            Assert.Null(scope.Current);
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
    public async Task Сутність_з_довідником_іде_в_синк_а_не_в_збір()
    {
        // Обидві неактивні: диспетчер питає лише RegistryDefId, а збирач і синк
        // тут — замінники.
        var bound = await EntityAsync(await RegistryAsync(RegistrySourceKind.Local), active: false);
        var plain = await EntityAsync(registryDefId: null, active: false);

        await using var db = Context();

        var runner = Substitute.For<ICollectionRunner>();
        var sync = Substitute.For<IRegistrySyncJob>();
        var job = new CollectionJob(
            runner,
            db,
            Substitute.For<IBackgroundJobScheduler>(),
            new TestClock(Now),
            Substitute.For<INotificationOutbox>(),
            new OutboxDispatcher(db, new TestClock(Now), Substitute.For<INotificationSender>()),
            sync);

        await job.ExecuteAsync(new CollectionJobRequest(bound, null, null), Substitute.For<IJobProgress>(), CancellationToken.None);
        await job.ExecuteAsync(new CollectionJobRequest(plain, null, null), Substitute.For<IJobProgress>(), CancellationToken.None);

        // Довідник → синк, збирача не кличуть.
        await sync.Received(1).ExecuteAsync(bound, Arg.Any<CancellationToken>());
        await runner.DidNotReceive().RunAsync(
            bound, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<IJobProgress>(), Arg.Any<CancellationToken>());

        // Контроль: без довідника — старий шлях збору, синку немає.
        await runner.Received(1).RunAsync(
            plain, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<IJobProgress>(), Arg.Any<CancellationToken>());
        await sync.DidNotReceive().ExecuteAsync(plain, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.11")]
    public async Task GetFieldMapsAsync_не_повертає_мапінгів_на_поле_довідника()
    {
        var registryId = await RegistryAsync(RegistrySourceKind.Local);
        var entityId = await EntityAsync(registryId, active: false);
        var chain = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(ct: CancellationToken.None);

        await using (var arrange = Context())
        {
            var field = await arrange.RegistryFieldDefs.AsNoTracking().FirstAsync(f => f.RegistryDefId == registryId);
            var column = chain.ColumnDefIds[1];

            arrange.EntityFieldMaps.AddRange(
                EntityFieldMap.ToColumn(entityId, $"Flow_{_tag}", column),
                EntityFieldMap.ToRegistryField(entityId, $"Capacity_{_tag}", field.Id));
            await arrange.SaveChangesAsync();
        }

        await using var db = Context();
        var maps = await new CollectionStore(db, new TestClock(Now)).GetFieldMapsAsync(entityId, CancellationToken.None);

        // ⛔ Атрибут довідника — не часовий ряд: збирач його не читає.
        var map = Assert.Single(maps);
        Assert.Equal(FieldTargetKind.Column, map.TargetKind);
        Assert.Equal($"Flow_{_tag}", map.SourceField);
    }

    // ─── Стенд ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Довідник (CAP Decimal, NAME String), три записи: E1 ↔ g1 (є в джерелі,
    /// збережений шлях застарів), E2 ↔ g2 (у джерелі немає), E3 без зв'язку;
    /// у джерелі ще g9 без зв'язку.
    /// </summary>
    private async Task<Stand> ArrangeAsync(
        RegistrySourceKind kind, ExternalTransport transport = ExternalTransport.PiWebApi)
    {
        var registryId = await RegistryAsync(kind);
        var parent = $@"\\AF\Db\Plant_{_tag}";
        var entityId = await EntityAsync(registryId, parent, transport: transport);

        await using var db = Context();

        var entity = await db.SourceEntities.AsNoTracking().FirstAsync(e => e.Id == entityId);
        var fields = await db.RegistryFieldDefs.AsNoTracking()
            .Where(f => f.RegistryDefId == registryId)
            .ToDictionaryAsync(f => f.Code);

        var svcId = await db.Users.Where(u => u.UserName == IntegrationActor.UserName).Select(u => u.Id).FirstAsync();

        var e1 = new RegistryEntry(registryId, EcrCode.Create("E1"), Text("E1"));
        var e2 = new RegistryEntry(registryId, EcrCode.Create("E2"), Text("E2"));
        var e3 = new RegistryEntry(registryId, EcrCode.Create("E3"), Text("E3"));
        db.RegistryEntries.AddRange(e1, e2, e3);
        await db.SaveChangesAsync();

        var cap = new RegistryValue(e1.Id, fields["CAP"].Id);
        cap.Set(CellDataType.Decimal, 10m, null);
        cap.MarkChangedBy(svcId);
        var name = new RegistryValue(e1.Id, fields["NAME"].Id);
        name.Set(CellDataType.String, "Stack 1", null);
        name.MarkChangedBy(svcId);
        db.RegistryValues.AddRange(cap, name);

        var g1 = Guid.NewGuid().ToString("D");
        var g2 = Guid.NewGuid().ToString("D");
        var g9 = Guid.NewGuid().ToString("D");

        var k1 = new RegistryExternalKey(e1.Id, entity.DataSourceId, g1);
        k1.MarkSynced($@"\\AF\Db\Old_{_tag}\Stack1", Now.AddDays(-1));
        var k2 = new RegistryExternalKey(e2.Id, entity.DataSourceId, g2);
        k2.MarkSynced($@"{parent}\Stack2", Now.AddDays(-1));
        db.RegistryExternalKeys.AddRange(k1, k2);

        db.EntityFieldMaps.AddRange(
            EntityFieldMap.ToRegistryField(entityId, "Capacity", fields["CAP"].Id),
            EntityFieldMap.ToRegistryField(entityId, "Name", fields["NAME"].Id));
        await db.SaveChangesAsync();

        // ⚠ PI SQL Client (RTQP) адресує елемент ІМЕНЕМ (`WHERE e.Name = ?`), а не шляхом;
        // його каталог (`DiscoverAsync`) — рядки-АТРИБУТИ: Code = елемент|атрибут,
        // EntityPath = ім'я елемента, DataType = ValueType, ExternalId = GUID елемента.
        var rtqp = transport == ExternalTransport.PiSqlClient;
        var a1 = rtqp ? "Stack1" : $@"{parent}\Stack1";
        var a9 = rtqp ? "Stack9" : $@"{parent}\Stack9";

        List<SourceEntityDescriptor> children = rtqp
            ?
            [
                new("Stack1|Capacity", "Capacity", "Stack1", null, "Double", g1),
                new("Stack1|Name", "Name", "Stack1", null, "String", g1),
                new("Stack9|Capacity", "Capacity", "Stack9", null, "Double", g9),
                new("Stack9|Name", "Name", "Stack9", null, "String", g9),
            ]
            :
            [
                new("Stack1", null, a1, null, "Element", g1),
                new("Stack9", null, a9, null, "Element", g9),
            ];

        // Що дає DiscoverElementsAsync: RTQP — без шляху, адреса = ім'я; Web API — адреса = шлях.
        List<SourceElement> elements =
        [
            new(g1, "Stack1", rtqp ? null : a1, a1),
            new(g9, "Stack9", rtqp ? null : a9, a9),
        ];

        var values = new Dictionary<string, SourceDataPoint>(StringComparer.OrdinalIgnoreCase)
        {
            [$"{a1}|Capacity"] = new($"{a1}|Capacity", Now, 12.5m, null, null, "Good"),
            [$"{a1}|Name"] = new($"{a1}|Name", Now, null, "Stack 1", null, "Good"),
            [$"{a9}|Capacity"] = new($"{a9}|Capacity", Now, 3m, null, null, "Good"),
            [$"{a9}|Name"] = new($"{a9}|Name", Now, null, "Stack 9", null, "Good"),
        };

        return new Stand(entityId, registryId, parent, [e1.Id, e2.Id, e3.Id], children, elements, values, g2, g9, g1);
    }

    private async Task<int> RegistryAsync(RegistrySourceKind kind)
    {
        await using var db = Context();

        var registry = new RegistryDef(EcrCode.Create($"SYNC_{_tag}"), Text("Stacks"), isTemporal: false);
        registry.SwitchSource(kind);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();

        db.RegistryFieldDefs.AddRange(
            new RegistryFieldDef(registry.Id, EcrCode.Create("CAP"), Text("Capacity"), CellDataType.Decimal, 1),
            new RegistryFieldDef(registry.Id, EcrCode.Create("NAME"), Text("Name"), CellDataType.String, 2));
        await db.SaveChangesAsync();

        return registry.Id;
    }

    private async Task<int> EntityAsync(
        int? registryDefId,
        string? path = null,
        bool active = true,
        ExternalTransport transport = ExternalTransport.PiWebApi)
    {
        await using var db = Context();

        var dataSource = new DataSource(
            EcrCode.Create($"RS{Guid.NewGuid().ToString("N")[..8]}"), Text("PI AF"), transport,
            "https://af.test", "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync();

        var entity = new SourceEntity(dataSource.Id, $"Plant_{Guid.NewGuid().ToString("N")[..8]}", RegistrySourceKind.External);
        entity.Describe("Plant", path ?? $@"\\AF\Db\Plant_{_tag}");
        entity.BindRegistry(registryDefId);

        // ⚠ Активна сутність без завершеного прогону лишається в спільній базі й
        // жовтить SourcesHealthCheck для кожного наступного прогону. Синку
        // активність потрібна — такі тести вимикають сутність у finally.
        if (!active)
        {
            entity.Deactivate();
        }

        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync();

        return entity.Id;
    }

    /// <summary>Вимикає сутність після тесту — з тієї ж причини (SourcesHealthCheck).</summary>
    private async Task DeactivateAsync(Stand stand)
    {
        await using var db = Context();
        var entity = await db.SourceEntities.FirstAsync(e => e.Id == stand.EntityId);
        entity.Deactivate();
        await db.SaveChangesAsync();
    }

    /// <remarks>
    /// ⚠ Фабрика scope — підробка: тести цього файлу ганяють лише <c>Local</c>, а він не пише
    /// через writer. Покликав би — підробка повернула б порожній scope і тест упав би.
    /// Запис (<c>External</c>/<c>Hybrid</c>) — <c>RegistrySyncApplyTests</c>, зі справжнім контейнером.
    /// </remarks>
    private static RegistrySyncJob Job(EcrDbContext db, FakeSource source, Stand stand, JobActorScope scope)
    {
        source.Catalog = stand.Children;
        source.Elements = stand.Elements;

        return new(db, [source], new IntegrationActor(db, scope), new TestClock(Now), Substitute.For<IServiceScopeFactory>());
    }

    private async Task<List<CollectionCoverage>> EventsAsync(int entityId)
    {
        await using var db = Context();
        return await db.CollectionCoverages.AsNoTracking()
            .Where(c => c.SourceEntityId == entityId && c.Status != null)
            .OrderBy(c => c.Id)
            .ToListAsync();
    }

    /// <summary>
    /// Відбиток усього, що синк міг би змінити в <c>dic.*</c>, разом із рядками
    /// системної історії (RT-04): оновлення значення лишає там рядок, навіть якщо
    /// значення повернули назад.
    /// </summary>
    private async Task<string> FingerprintAsync(Stand stand)
    {
        await using var db = Context();
        var ids = stand.EntryIds;

        var entries = await db.RegistryEntries.AsNoTracking()
            .Where(e => e.RegistryDefId == stand.RegistryId)
            .OrderBy(e => e.Id)
            .Select(e => $"{e.Id}:{e.Code}:{e.IsDeleted}:{e.ChangedByUserId}")
            .ToListAsync();

        var values = await db.RegistryValues.AsNoTracking()
            .Where(v => ids.Contains(v.RegistryEntryId))
            .OrderBy(v => v.Id)
            .Select(v => $"{v.Id}:{v.ValueNumeric}:{v.ValueString}:{v.ChangedByUserId}")
            .ToListAsync();

        var keys = await db.RegistryExternalKeys.AsNoTracking()
            .Where(k => ids.Contains(k.RegistryEntryId))
            .OrderBy(k => k.Id)
            .Select(k => $"{k.Id}:{k.ExternalId}:{k.ExternalPath}:{k.LastSyncedAt}")
            .ToListAsync();

        var revision = await db.RegistryDefs.AsNoTracking()
            .Where(d => d.Id == stand.RegistryId)
            .Select(d => d.DataRevision)
            .FirstAsync();

        // Ідентифікатори — числа зі стенду, не ввід: підстановка в текст безпечна.
        var idList = string.Join(",", ids);
        var valueHistoryQuery = "SELECT COUNT(*) AS [Value] FROM dic.RegistryValueHistory WHERE RegistryEntryId IN (" + idList + ")";
        var entryHistoryQuery = "SELECT COUNT(*) AS [Value] FROM dic.RegistryEntryHistory WHERE Id IN (" + idList + ")";
        var history = await db.Database.SqlQueryRaw<int>(valueHistoryQuery).SingleAsync();
        var entryHistory = await db.Database.SqlQueryRaw<int>(entryHistoryQuery).SingleAsync();

        return string.Join(
            "|",
            string.Join(";", entries),
            string.Join(";", values),
            string.Join(";", keys),
            revision,
            history,
            entryHistory);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private sealed record Stand(
        int EntityId,
        int RegistryId,
        string ParentPath,
        long[] EntryIds,
        IReadOnlyList<SourceEntityDescriptor> Children,
        IReadOnlyList<SourceElement> Elements,
        IReadOnlyDictionary<string, SourceDataPoint> Values,
        string MissingGuid,
        string UnlinkedGuid,
        string Linked1Guid);

    /// <summary>
    /// Джерело з переліком елементів і поточними значеннями; <paramref name="failPath"/> — відмова шляху.
    /// <see cref="Catalog"/> — каталог конфігуратора у формі транспорту (для RTQP — рядки-атрибути):
    /// синк його читати не мусить, він тут, щоб мутація «повернути каталожний шлях» червоніла.
    /// </summary>
    private sealed class FakeSource(
        IReadOnlyDictionary<string, SourceDataPoint> values,
        string? failPath = null,
        ExternalTransport transport = ExternalTransport.PiWebApi) : IExternalDataSource
    {
        public JobActorScope? Scope { get; set; }

        public string? SeenUser { get; private set; }

        public bool SeenIntegration { get; private set; }

        public ExternalTransport Transport => transport;

        public IReadOnlyList<SourceEntityDescriptor> Catalog { get; set; } = [];

        public IReadOnlyList<SourceElement> Elements { get; set; } = [];

        /// <summary>Відмова переліку; <c>null</c> — перелік віддається.</summary>
        public Exception? ElementsFailure { get; init; }

        public Task<IReadOnlyList<SourceEntityDescriptor>> DiscoverAsync(int dataSourceId, CancellationToken ct)
            => Task.FromResult(Catalog);

        public Task<SourceElementsResult> DiscoverElementsAsync(int dataSourceId, string root, CancellationToken ct)
            => ElementsFailure is { } failure
                ? Task.FromException<SourceElementsResult>(failure)
                : Task.FromResult(new SourceElementsResult(Elements, IsComplete: true));

        public Task<CollectionResult> ReadAsync(CollectionRequest request, CancellationToken ct)
            => throw new InvalidOperationException("Синк довідника не читає часових рядів.");

        public Task<CurrentValuesResult> ReadCurrentAsync(
            int dataSourceId, IReadOnlyCollection<string> paths, CancellationToken ct)
        {
            SeenUser = Scope?.Current?.UserName;
            SeenIntegration = Scope?.IsIntegration ?? false;

            var found = new List<SourceDataPoint>();
            var failures = new List<CurrentValueFailure>();

            foreach (var path in paths)
            {
                if (string.Equals(path, failPath, StringComparison.OrdinalIgnoreCase) || !values.TryGetValue(path, out var point))
                {
                    failures.Add(new CurrentValueFailure(path, "ECR-INT-0404", "err.ECR-INT-0404.sourcePathNotFound"));
                    continue;
                }

                found.Add(point);
            }

            return Task.FromResult(new CurrentValuesResult(found, failures));
        }
    }
}
