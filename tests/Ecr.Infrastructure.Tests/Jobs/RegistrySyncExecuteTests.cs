// tests/Ecr.Infrastructure.Tests/Jobs/RegistrySyncExecuteTests.cs
using Ecr.Application;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Registries.Rules;
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
/// Синк довідника виконує ПОВНИЙ план (<c>D-212</c> PR-6): автостворення, коди Lookup, перепривʼязка,
/// політика зниклих, повернення, правила довідника, атомарність спроби.
/// </summary>
/// <remarks>
/// ⚠ Справжній SQL Server і справжній контейнер (як <c>RegistrySyncApplyTests</c>): writer, одиниця
/// роботи, ключі, аудит — ті самі, що в проді. Підроблені лише джерело (перелік елементів +
/// поточні значення) і — у двох тестах — рушій правил (порушення рівня Error / збій посеред спроби).
/// <para>
/// Стенд: довідник CAP (Decimal), записи E1 ↔ g1 (Stack1, CAP 10) і E2 ↔ g2 (Stack2, CAP 20), обидва
/// в джерелі з тими самими значеннями — прогін без змін порожній.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistrySyncExecuteTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 9, 30, 5, 0, 0, DateTimeKind.Utc);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    // ─── Автостворення ──────────────────────────────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-212")]
    public async Task External_Manual_створює_запис_з_кодом_і_назвою_з_імені_елемента_і_ключем()
    {
        var stand = await ArrangeAsync(RegistrySourceKind.External);
        var g9 = stand.Add("Stack9", 3m);
        await using var provider = BuildProvider();

        try
        {
            await RunAsync(provider, stand);

            var created = await EntryAsync(stand, "Stack9");
            Assert.NotNull(created);
            Assert.Equal("Stack9", created.DisplayL10n.Values["en"]);
            Assert.Equal(3m, await CapAsync(stand, created.Id));
            Assert.Equal(stand.SvcId, created.CreatedByUserId);

            var key = await KeyAsync(stand, g9);
            Assert.Equal(created.Id, key.RegistryEntryId);
            Assert.Equal($@"{stand.Parent}\Stack9", key.ExternalPath);

            var auto = Assert.Single(await EventsAsync(stand), e => e.Status == CollectionCoverage.RegistryAutoCreated);
            Assert.Contains($"element={g9}; entry={created.Id}", auto.Details, StringComparison.Ordinal);

            // Повтор: елемент уже прив'язаний — ні другого запису, ні другої події.
            var count = (await EventsAsync(stand)).Count;
            await RunAsync(provider, stand);
            Assert.Equal(1, await CountEntriesAsync(stand, "Stack9"));
            Assert.Equal(count, (await EventsAsync(stand)).Count);
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
    public async Task External_Auto_бере_код_із_послідовності_а_назву_з_імені()
    {
        var stand = await ArrangeAsync(RegistrySourceKind.External, codeMode: RegistryCodeMode.Auto);
        var g9 = stand.Add("Stack9", 3m);
        await using var provider = BuildProvider();

        try
        {
            await RunAsync(provider, stand);

            var key = await KeyAsync(stand, g9);
            await using var db = Context();
            var created = await db.RegistryEntries.AsNoTracking().SingleAsync(e => e.Id == key.RegistryEntryId);
            Assert.Matches("^E[0-9]{9,}$", created.Code);
            Assert.Equal("Stack9", created.DisplayL10n.Values["en"]);
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
    public async Task Зайнятий_код_не_створює_і_не_привʼязує_а_подія_з_Id_тримача()
    {
        var stand = await ArrangeAsync(RegistrySourceKind.External);
        var g9 = stand.Add("Stack9", 3m);
        var holder = await AddEntryAsync(stand, "Stack9");
        await using var provider = BuildProvider();

        try
        {
            await RunAsync(provider, stand);

            // ⛔ CreateOnly: наявний запис із тим самим кодом не «переймається» елементом.
            Assert.Equal(1, await CountEntriesAsync(stand, "Stack9"));
            await using (var db = Context())
            {
                Assert.False(await db.RegistryExternalKeys.AnyAsync(k => k.ExternalId == g9));
                Assert.False(await db.RegistryValues.AnyAsync(v => v.RegistryEntryId == holder));
            }

            var events = await EventsAsync(stand);
            Assert.DoesNotContain(events, e => e.Status == CollectionCoverage.RegistryAutoCreated);
            var refused = Assert.Single(events, e => e.Status == CollectionCoverage.RegistryElementUnlinked);
            Assert.Contains($"element={g9}", refused.Details, StringComparison.Ordinal);
            Assert.Contains($"id={holder};", refused.Details, StringComparison.Ordinal);
            Assert.Contains($"messageKey={RegistryEntryWriter.EntryCodeTakenKey}", refused.Details, StringComparison.Ordinal);

            // Дедуп: той самий стан — жодної нової події.
            await RunAsync(provider, stand);
            Assert.Single(await EventsAsync(stand), e => e.Status == CollectionCoverage.RegistryElementUnlinked);
        }
        finally
        {
            await DeactivateAsync(stand);
        }
    }

    // ─── Lookup за кодом ────────────────────────────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-212")]
    public async Task Lookup_за_кодом_знайдено_пишеться_Id_не_знайдено_поле_не_чіпається()
    {
        var stand = await ArrangeAsync(RegistrySourceKind.External, withRef: true);

        // Код — регістронезалежно, як колація бази.
        stand.PutText("Stack1", "Fuel", "gas");
        stand.PutText("Stack2", "Fuel", "NOPE");
        await using var provider = BuildProvider();

        try
        {
            await RunAsync(provider, stand);

            Assert.Equal(stand.GasId, await RefAsync(stand, stand.E1));
            Assert.Equal(stand.CoalId, await RefAsync(stand, stand.E2));

            var rejected = Assert.Single(await EventsAsync(stand), e => e.Status == CollectionCoverage.RegistryValueRejected);
            Assert.Contains($"entry={stand.E2}", rejected.Details, StringComparison.Ordinal);
            Assert.Contains("field=REF", rejected.Details, StringComparison.Ordinal);
            Assert.Contains("messageKey=err.ECR-REG-0422.entryRefNotFound", rejected.Details, StringComparison.Ordinal);
        }
        finally
        {
            await DeactivateAsync(stand);
        }
    }

    // ─── Перепривʼязка ──────────────────────────────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-212")]
    public async Task Перестворений_елемент_один_кандидат_перепривʼязується_з_подією_і_StructureChange()
    {
        var stand = await ArrangeAsync(RegistrySourceKind.External);
        var keyId = (await KeyAsync(stand, stand.G2)).Id;
        stand.Remove("Stack2");
        var g2b = stand.Add("Stack2", 20m);
        await using var provider = BuildProvider();

        try
        {
            await RunAsync(provider, stand);

            var key = await KeyAsync(stand, g2b);
            Assert.Equal(keyId, key.Id);
            Assert.Equal(stand.E2, key.RegistryEntryId);
            Assert.Null(key.MissingInSourceSince);
            Assert.Equal(0, await CountEntriesAsync(stand, "Stack2"));

            var events = await EventsAsync(stand);
            var relinked = Assert.Single(events, e => e.Status == CollectionCoverage.RegistryExternalKeyRelinked);
            Assert.Contains($"element={g2b}; entry={stand.E2}; old={stand.G2}", relinked.Details, StringComparison.Ordinal);
            Assert.DoesNotContain(events, e => e.Status == CollectionCoverage.RegistrySourceMissing);
            Assert.DoesNotContain(events, e => e.Status == CollectionCoverage.RegistryAutoCreated);

            Assert.Equal(1, await CountAsync(
                "SELECT COUNT(*) AS [Value] FROM aud.StructureChange WHERE EntityType = N'dic.RegistryExternalKey' "
                + $"AND Operation = N'Relink' AND EntityId = {keyId} AND ChangedByUserId = {stand.SvcId} "
                + $"AND OldJson LIKE N'%{stand.G2}%' AND NewJson LIKE N'%{g2b}%'"));
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
    public async Task Кілька_кандидатів_на_шлях_нічого_не_пишуть()
    {
        var stand = await ArrangeAsync(RegistrySourceKind.External);
        var keyId = (await KeyAsync(stand, stand.G2)).Id;
        stand.Remove("Stack2");
        var b = stand.Add("Stack2", 20m);
        var c = stand.Add("Stack2", 21m);
        await using var provider = BuildProvider();

        try
        {
            await RunAsync(provider, stand);

            var key = await KeyAsync(stand, stand.G2);
            Assert.Equal(keyId, key.Id);
            Assert.Null(key.MissingInSourceSince);
            Assert.Equal(0, await CountEntriesAsync(stand, "Stack2"));
            Assert.Equal(0, await CountAsync(
                $"SELECT COUNT(*) AS [Value] FROM aud.StructureChange WHERE EntityType = N'dic.RegistryExternalKey' AND EntityId = {keyId}"));

            var events = await EventsAsync(stand);
            Assert.DoesNotContain(events, e => e.Status == CollectionCoverage.RegistryExternalKeyRelinked);
            Assert.Contains(events, e => e.Status == CollectionCoverage.RegistrySourceMissing && e.Details!.Contains(stand.G2, StringComparison.Ordinal));
            Assert.Contains(events, e => e.Status == CollectionCoverage.RegistryElementUnlinked && e.Details!.Contains(b, StringComparison.Ordinal));
            Assert.Contains(events, e => e.Status == CollectionCoverage.RegistryElementUnlinked && e.Details!.Contains(c, StringComparison.Ordinal));
        }
        finally
        {
            await DeactivateAsync(stand);
        }
    }

    // ─── Зниклий елемент і повернення ───────────────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-212")]
    public async Task MarkOrphaned_позначає_ключ_і_дедуп_за_since_новий_епізод_нова_подія()
    {
        var stand = await ArrangeAsync(RegistrySourceKind.External);
        stand.Remove("Stack2");
        await using var provider = BuildProvider();

        try
        {
            await RunAsync(provider, stand);
            await RunAsync(provider, stand, Now.AddHours(1));

            Assert.Equal(Now, (await KeyAsync(stand, stand.G2)).MissingInSourceSince);
            Assert.True(await ActiveAsync(stand.E2));
            var missing = Assert.Single(await EventsAsync(stand), e => e.Status == CollectionCoverage.RegistrySourceMissing);
            Assert.Contains("since=2026-09-30T05:00:00Z", missing.Details, StringComparison.Ordinal);

            // Повернувся — позначку знято; зник знову — НОВИЙ епізод, нова подія.
            var g2 = stand.Add("Stack2", 20m, stand.G2);
            await RunAsync(provider, stand, Now.AddHours(2));
            Assert.Null((await KeyAsync(stand, g2)).MissingInSourceSince);

            stand.Remove("Stack2");
            await RunAsync(provider, stand, Now.AddHours(3));
            Assert.Equal(Now.AddHours(3), (await KeyAsync(stand, g2)).MissingInSourceSince);
            Assert.Equal(2, (await EventsAsync(stand)).Count(e => e.Status == CollectionCoverage.RegistrySourceMissing));
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
    public async Task Ignore_не_чіпає_ні_запис_ні_ключ_а_подія_є()
    {
        var stand = await ArrangeAsync(RegistrySourceKind.External, RegistryMissingPolicy.Ignore);
        stand.Remove("Stack2");
        await using var provider = BuildProvider();

        try
        {
            await RunAsync(provider, stand);

            Assert.Null((await KeyAsync(stand, stand.G2)).MissingInSourceSince);
            Assert.True(await ActiveAsync(stand.E2));
            Assert.Single(await EventsAsync(stand), e => e.Status == CollectionCoverage.RegistrySourceMissing);
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
    public async Task External_Deactivate_вимикає_з_аудитом_а_повернення_вмикає_з_подією()
    {
        var stand = await ArrangeAsync(RegistrySourceKind.External, RegistryMissingPolicy.Deactivate);
        stand.Remove("Stack2");
        await using var provider = BuildProvider();

        try
        {
            await RunAsync(provider, stand);

            Assert.False(await ActiveAsync(stand.E2));
            Assert.Equal(Now, (await KeyAsync(stand, stand.G2)).MissingInSourceSince);
            Assert.Equal(1, await CountAsync(ActiveAuditQuery(stand.E2, stand.SvcId)));
            var events = await EventsAsync(stand);
            var deactivated = Assert.Single(events, e => e.Status == CollectionCoverage.RegistryDeactivated);
            Assert.Contains($"element={stand.G2}; entry={stand.E2}", deactivated.Details, StringComparison.Ordinal);
            Assert.DoesNotContain(events, e => e.Status == CollectionCoverage.RegistrySourceMissing);

            // Повернувся — External вмикає сам (Q6).
            stand.Add("Stack2", 20m, stand.G2);
            await RunAsync(provider, stand, Now.AddHours(1));

            Assert.True(await ActiveAsync(stand.E2));
            Assert.Null((await KeyAsync(stand, stand.G2)).MissingInSourceSince);
            Assert.Equal(2, await CountAsync(ActiveAuditQuery(stand.E2, stand.SvcId)));
            var reactivated = Assert.Single(await EventsAsync(stand), e => e.Status == CollectionCoverage.RegistryReactivated);
            Assert.Contains("since=2026-09-30T05:00:00Z", reactivated.Details, StringComparison.Ordinal);
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
    public async Task Hybrid_повернення_не_вмикає_а_Diverged_active()
    {
        var stand = await ArrangeAsync(RegistrySourceKind.Hybrid, RegistryMissingPolicy.Deactivate);
        stand.Remove("Stack2");
        await using var provider = BuildProvider();

        try
        {
            await RunAsync(provider, stand);
            Assert.False(await ActiveAsync(stand.E2));

            stand.Add("Stack2", 20m, stand.G2);
            await RunAsync(provider, stand, Now.AddHours(1));

            // D-212 Q6: вмикає людина — запис лишається вимкненим, позначку знято, подія Diverged.
            Assert.False(await ActiveAsync(stand.E2));
            Assert.Null((await KeyAsync(stand, stand.G2)).MissingInSourceSince);
            var events = await EventsAsync(stand);
            Assert.DoesNotContain(events, e => e.Status == CollectionCoverage.RegistryReactivated);
            var diverged = Assert.Single(events, e => e.Status == CollectionCoverage.RegistryDiverged);
            Assert.Contains("field=@active; ecr=False; source=True", diverged.Details, StringComparison.Ordinal);
        }
        finally
        {
            await DeactivateAsync(stand);
        }
    }

    // ─── Правила довідника й атомарність ───────────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-212")]
    public async Task Порушення_правила_рівня_Error_подія_а_рядок_записано()
    {
        var stand = await ArrangeAsync(RegistrySourceKind.External);
        stand.Put("Stack1", "Capacity", 11m);
        var g9 = stand.Add("Stack9", 3m);
        var rules = new FakeRules(fail: false);
        await using var provider = BuildProvider(rules);

        try
        {
            await RunAsync(provider, stand);

            // ⛔ ThrowIfErrors НЕ викликається: джерело — правда, рядки лишаються.
            Assert.Equal(11m, await CapAsync(stand, stand.E1));
            var created = (await KeyAsync(stand, g9)).RegistryEntryId;

            // Оцінено саме записане цим прогоном: оновлений E1 і створений запис, одним викликом.
            Assert.Equal(new[] { stand.E1, created }, rules.Seen.Order());

            var violations = (await EventsAsync(stand)).Where(e => e.Status == CollectionCoverage.RegistryRuleViolation).ToList();
            Assert.Equal(2, violations.Count);
            Assert.Contains(violations, e => e.Details!.Contains($"element={stand.G1}; entry={stand.E1}", StringComparison.Ordinal)
                                             && e.Details.Contains("rule=R_MAX; severity=Error", StringComparison.Ordinal));
            Assert.Contains(violations, e => e.Details!.Contains($"element={g9}; entry={created}", StringComparison.Ordinal));
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
    public async Task Збій_посеред_спроби_відкочує_весь_пакет_і_не_пише_подій()
    {
        var stand = await ArrangeAsync(RegistrySourceKind.External);
        var keyId = (await KeyAsync(stand, stand.G2)).Id;
        stand.Put("Stack1", "Capacity", 11m);
        stand.Add("Stack9", 3m);
        stand.Remove("Stack2");
        var g2b = stand.Add("Stack2", 20m);
        await using var provider = BuildProvider(new FakeRules(fail: true));

        try
        {
            var revision = await RevisionAsync(stand);

            // Рушій правил падає ПІСЛЯ збереження створення, оновлення й перепривʼязки.
            await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(provider, stand));

            Assert.Equal(10m, await CapAsync(stand, stand.E1));
            Assert.Equal(0, await CountEntriesAsync(stand, "Stack9"));
            var key = await KeyAsync(stand, stand.G2);
            Assert.Equal(keyId, key.Id);
            Assert.Equal(revision, await RevisionAsync(stand));
            await using (var db = Context())
            {
                Assert.False(await db.RegistryExternalKeys.AnyAsync(k => k.ExternalId == g2b));
            }

            Assert.Equal(0, await CountAsync(
                $"SELECT COUNT(*) AS [Value] FROM aud.StructureChange WHERE EntityType = N'dic.RegistryExternalKey' AND EntityId = {keyId}"));
            Assert.Equal(0, await CountAsync(AuditQuery(stand.E1, stand.SvcId)));
            Assert.Empty(await EventsAsync(stand));
        }
        finally
        {
            await DeactivateAsync(stand);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-187")]
    public async Task Неповний_знімок_без_створення_перепривʼязки_і_позначок_а_оновлення_йдуть()
    {
        var stand = await ArrangeAsync(RegistrySourceKind.External, RegistryMissingPolicy.Deactivate);
        stand.Put("Stack1", "Capacity", 11m);
        stand.Add("Stack9", 3m);
        stand.Remove("Stack2");
        var g2b = stand.Add("Stack2", 20m);
        stand.Complete = false;
        await using var provider = BuildProvider();

        try
        {
            await RunAsync(provider, stand);

            Assert.Equal(11m, await CapAsync(stand, stand.E1));
            Assert.Equal(0, await CountEntriesAsync(stand, "Stack9"));
            var key = await KeyAsync(stand, stand.G2);
            Assert.Null(key.MissingInSourceSince);
            Assert.True(await ActiveAsync(stand.E2));
            await using (var db = Context())
            {
                Assert.False(await db.RegistryExternalKeys.AnyAsync(k => k.ExternalId == g2b));
            }

            var events = await EventsAsync(stand);
            Assert.DoesNotContain(events, e => e.Status is CollectionCoverage.RegistryAutoCreated
                or CollectionCoverage.RegistryExternalKeyRelinked
                or CollectionCoverage.RegistrySourceMissing
                or CollectionCoverage.RegistryDeactivated);
            Assert.Equal(2, events.Count(e => e.Status == CollectionCoverage.RegistryElementUnlinked));
        }
        finally
        {
            await DeactivateAsync(stand);
        }
    }

    // ─── Стенд ──────────────────────────────────────────────────────────────

    private async Task<Stand> ArrangeAsync(
        RegistrySourceKind kind,
        RegistryMissingPolicy policy = RegistryMissingPolicy.MarkOrphaned,
        RegistryCodeMode codeMode = RegistryCodeMode.Manual,
        bool withRef = false)
    {
        await using var db = Context();

        var registry = new RegistryDef(EcrCode.Create($"SYNC6_{_tag}"), Text("Stacks"), isTemporal: false);
        registry.SwitchSource(kind);
        registry.UseCodeMode(codeMode);
        db.RegistryDefs.Add(registry);

        var fuels = new RegistryDef(EcrCode.Create($"FUEL6_{_tag}"), Text("Fuels"), isTemporal: false);
        db.RegistryDefs.Add(fuels);
        await db.SaveChangesAsync();

        var gas = new RegistryEntry(fuels.Id, EcrCode.Create("GAS"), Text("Gas"));
        var coal = new RegistryEntry(fuels.Id, EcrCode.Create("COAL"), Text("Coal"));
        db.RegistryEntries.AddRange(gas, coal);

        var cap = new RegistryFieldDef(registry.Id, EcrCode.Create("CAP"), Text("Capacity"), CellDataType.Decimal, 1);
        var reference = new RegistryFieldDef(registry.Id, EcrCode.Create("REF"), Text("Fuel"), CellDataType.Lookup, 2);
        reference.PointTo(fuels.Id);
        db.RegistryFieldDefs.AddRange(cap, reference);

        var dataSource = new DataSource(
            EcrCode.Create($"RS6{_tag}"), Text("PI AF"), ExternalTransport.PiWebApi, "https://af.test", "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync();

        var parent = $@"\\AF\Db\Plant6_{_tag}";
        var entity = new SourceEntity(dataSource.Id, $"Plant6_{_tag}", RegistrySourceKind.External);
        entity.Describe("Plant", parent);
        entity.BindRegistry(registry.Id);
        entity.ConfigureRegistrySync(policy, null, null, false);
        db.SourceEntities.Add(entity);

        var svcId = await db.Users.Where(u => u.UserName == IntegrationActor.UserName).Select(u => u.Id).FirstAsync();

        var e1 = new RegistryEntry(registry.Id, EcrCode.Create("E1"), Text("E1"));
        var e2 = new RegistryEntry(registry.Id, EcrCode.Create("E2"), Text("E2"));
        db.RegistryEntries.AddRange(e1, e2);
        await db.SaveChangesAsync();

        var v1 = new RegistryValue(e1.Id, cap.Id);
        v1.Set(CellDataType.Decimal, 10m, null);
        v1.MarkChangedBy(svcId);
        var v2 = new RegistryValue(e2.Id, cap.Id);
        v2.Set(CellDataType.Decimal, 20m, null);
        v2.MarkChangedBy(svcId);
        var r2 = new RegistryValue(e2.Id, reference.Id);
        r2.Set(CellDataType.Lookup, coal.Id, null);
        r2.MarkChangedBy(svcId);
        db.RegistryValues.AddRange(v1, v2, r2);

        var g1 = Guid.NewGuid().ToString("D");
        var g2 = Guid.NewGuid().ToString("D");
        var k1 = new RegistryExternalKey(e1.Id, dataSource.Id, g1);
        k1.MarkSynced($@"{parent}\Stack1", Now.AddDays(-1));
        var k2 = new RegistryExternalKey(e2.Id, dataSource.Id, g2);
        k2.MarkSynced($@"{parent}\Stack2", Now.AddDays(-1));
        db.RegistryExternalKeys.AddRange(k1, k2);

        db.EntityFieldMaps.Add(EntityFieldMap.ToRegistryField(entity.Id, "Capacity", cap.Id));
        if (withRef)
        {
            db.EntityFieldMaps.Add(EntityFieldMap.ToRegistryField(entity.Id, "Fuel", reference.Id));
        }

        await db.SaveChangesAsync();

        var stand = new Stand(entity.Id, registry.Id, cap.Id, reference.Id, dataSource.Id, svcId, parent, e1.Id, e2.Id, g1, g2)
        {
            GasId = gas.Id,
            CoalId = coal.Id,
        };
        stand.Add("Stack1", 10m, g1);
        stand.Add("Stack2", 20m, g2);
        return stand;
    }

    private async Task<long> AddEntryAsync(Stand stand, string code)
    {
        await using var db = Context();
        var entry = new RegistryEntry(stand.RegistryId, EcrCode.Create(code), Text(code));
        db.RegistryEntries.Add(entry);
        await db.SaveChangesAsync();
        return entry.Id;
    }

    private async Task RunAsync(ServiceProvider provider, Stand stand, DateTime? now = null)
    {
        await using var db = Context();
        var job = new RegistrySyncJob(
            db,
            [new FakeSource(stand)],
            new IntegrationActor(db, new JobActorScope()),
            new TestClock(now ?? Now),
            provider.GetRequiredService<IServiceScopeFactory>());

        await job.ExecuteAsync(stand.EntityId, CancellationToken.None);
    }

    /// <summary>Контейнер як у проді; <paramref name="rules"/> — підміна рушія правил.</summary>
    private ServiceProvider BuildProvider(IRegistryRuleEngine? rules = null)
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

        if (rules is not null)
        {
            services.AddScoped(_ => rules);
        }

        return services.BuildServiceProvider();
    }

    private async Task DeactivateAsync(Stand stand)
    {
        await using var db = Context();
        var entity = await db.SourceEntities.FirstAsync(e => e.Id == stand.EntityId);
        entity.Deactivate();
        await db.SaveChangesAsync();
    }

    private async Task<RegistryEntry?> EntryAsync(Stand stand, string code)
    {
        await using var db = Context();
        return await db.RegistryEntries.AsNoTracking().SingleOrDefaultAsync(e => e.RegistryDefId == stand.RegistryId && e.Code == code);
    }

    private async Task<int> CountEntriesAsync(Stand stand, string code)
    {
        await using var db = Context();
        return await db.RegistryEntries.CountAsync(e => e.RegistryDefId == stand.RegistryId && e.Code == code);
    }

    private async Task<RegistryExternalKey> KeyAsync(Stand stand, string externalId)
    {
        await using var db = Context();
        return await db.RegistryExternalKeys.AsNoTracking()
            .SingleAsync(k => k.DataSourceId == stand.DataSourceId && k.ExternalId == externalId);
    }

    private async Task<decimal?> CapAsync(Stand stand, long entryId)
    {
        await using var db = Context();
        return await db.RegistryValues.AsNoTracking()
            .Where(v => v.RegistryEntryId == entryId && v.RegistryFieldDefId == stand.CapId)
            .Select(v => v.ValueNumeric)
            .SingleAsync();
    }

    private async Task<long?> RefAsync(Stand stand, long entryId)
    {
        await using var db = Context();
        return await db.RegistryValues.AsNoTracking()
            .Where(v => v.RegistryEntryId == entryId && v.RegistryFieldDefId == stand.RefId)
            .Select(v => v.ValueRefEntryId)
            .SingleOrDefaultAsync();
    }

    private async Task<bool> ActiveAsync(long entryId)
    {
        await using var db = Context();
        return await db.RegistryEntries.AsNoTracking().Where(e => e.Id == entryId).Select(e => e.IsActive).SingleAsync();
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

    // Ідентифікатори — числа зі стенду, не ввід: підстановка в текст безпечна.
    private static string AuditQuery(long entryId, int userId)
        => "SELECT COUNT(*) AS [Value] FROM aud.SecurityEvent WHERE EventType = N'"
           + RegistryEntryWriter.ValueChangedEventType + "' AND ChangedByUserId = " + userId
           + " AND DetailsJson LIKE N'%\"entryId\":" + entryId + ",%'";

    private static string ActiveAuditQuery(long entryId, int userId)
        => AuditQuery(entryId, userId) + " AND DetailsJson LIKE N'%\"field\":\"" + RegistryEntryWriter.ActiveFieldCode + "\"%'";

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>Стенд і живий знімок джерела (тест міняє його між прогонами).</summary>
    private sealed record Stand(
        int EntityId,
        int RegistryId,
        int CapId,
        int RefId,
        int DataSourceId,
        int SvcId,
        string Parent,
        long E1,
        long E2,
        string G1,
        string G2)
    {
        public long GasId { get; init; }

        public long CoalId { get; init; }

        /// <summary>Перелік повний (<c>SourceElementsResult.IsComplete</c>).</summary>
        public bool Complete { get; set; } = true;

        public List<SourceElement> Elements { get; } = [];

        public Dictionary<string, SourceDataPoint> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Елемент під коренем сутності (адреса = шлях, як PI Web API) з CAP.</summary>
        /// <returns>GUID елемента.</returns>
        public string Add(string name, decimal capacity, string? externalId = null)
        {
            var id = externalId ?? Guid.NewGuid().ToString("D");
            var path = $@"{Parent}\{name}";
            Elements.Add(new SourceElement(id, name, path, path));
            Put(name, "Capacity", capacity);
            return id;
        }

        /// <summary>Прибирає з переліку ВСІ елементи з цим іменем (значення лишаються — їх ніхто не читає).</summary>
        public void Remove(string name) => Elements.RemoveAll(e => e.Name == name);

        public void Put(string name, string attribute, decimal value)
        {
            var path = $@"{Parent}\{name}|{attribute}";
            Values[path] = new SourceDataPoint(path, Now, value, null, null, "Good");
        }

        public void PutText(string name, string attribute, string value)
        {
            var path = $@"{Parent}\{name}|{attribute}";
            Values[path] = new SourceDataPoint(path, Now, null, value, null, "Good");
        }
    }

    /// <summary>
    /// ⚠ Кілька елементів з ОДНИМ шляхом мають спільну адресу читання: значення атрибута — одне на
    /// шлях, як і в PI Web API.
    /// </summary>
    private sealed class FakeSource(Stand stand) : IExternalDataSource
    {
        public ExternalTransport Transport => ExternalTransport.PiWebApi;

        public Task<IReadOnlyList<SourceEntityDescriptor>> DiscoverAsync(int dataSourceId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SourceEntityDescriptor>>([]);

        public Task<SourceElementsResult> DiscoverElementsAsync(int dataSourceId, string root, CancellationToken ct)
            => Task.FromResult(new SourceElementsResult([.. stand.Elements], stand.Complete));

        public Task<CollectionResult> ReadAsync(CollectionRequest request, CancellationToken ct)
            => throw new InvalidOperationException("Синк довідника не читає часових рядів.");

        public Task<CurrentValuesResult> ReadCurrentAsync(
            int dataSourceId, IReadOnlyCollection<string> paths, CancellationToken ct)
        {
            var found = paths.Where(stand.Values.ContainsKey).Select(p => stand.Values[p]).ToList();

            // Атрибута немає — джерело про нього нічого не сказало (не відмова: знімок лишається повним).
            return Task.FromResult(new CurrentValuesResult(found, []));
        }
    }

    /// <summary>
    /// Рушій правил: або кожен записаний запис порушує правило рівня Error, або падає посеред спроби.
    /// </summary>
    private sealed class FakeRules(bool fail) : IRegistryRuleEngine
    {
        public List<long> Seen { get; } = [];

        public Task<RegistryRuleCheck> EvaluateAsync(
            RegistryDef definition,
            IReadOnlyCollection<long> changed,
            IReadOnlyCollection<long> removed,
            DateOnly? businessDate,
            CancellationToken ct)
        {
            if (fail)
            {
                throw new InvalidOperationException("Збій посеред спроби синку (тест атомарності).");
            }

            Seen.AddRange(changed);
            return Task.FromResult(new RegistryRuleCheck([.. changed.Select(id => new RegistryRuleViolationDto(
                id,
                "?",
                "R_MAX",
                "Error",
                RegistryRuleEngine.RuleViolatedErrorKey,
                new Dictionary<string, string?> { ["message"] = "CAP > 5" }))]));
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
