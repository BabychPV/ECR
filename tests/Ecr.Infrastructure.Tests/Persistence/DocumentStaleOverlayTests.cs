using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// LP-45 (D-432, D-432, D-432): накладка «брудних документів» карти застарілості й карти всіх авторів одним
/// запитом. Тести рахують SQL-команди (інтерсептор + тег запиту), а не час.
/// </summary>
/// <remarks>
/// Мутації (detached-worktree): у <c>StaleResultsMapCache.GetEntryAsync</c> не лікувати запис
/// (<c>current.DirtySeq &lt; dirtySeq</c> → <c>false</c>) - червоні <c>Накладка_власна_правка_...</c>,
/// <c>Накладка_лікує_протермінований_...</c>; не прибирати документ у <c>HealAllAsync</c> (<c>map.Remove</c>) -
/// червоний <c>Накладка_знімає_документ_...</c>; прибрати фільтр <c>Origin != Recalculation</c> в
/// <c>AuditWriter</c> - червоний <c>Пакет_Recalculation_...</c>; <c>MaxIncremental</c> → <c>int.MaxValue</c> -
/// червоний <c>Понад_поріг_...</c>; у <c>AuthorDocuments</c> прибрати <c>Origin &lt;&gt; 'Recalculation'</c> або
/// змінити <c>&gt;</c> на <c>&gt;=</c> - червоний <c>Автори_еквівалентні_...</c>; у <c>AuditWriter</c> повернути
/// <c>Invalidate</c> замість <c>MarkDirty</c> - червоні тести, що перевіряють <c>ValueFor</c> без змін.
/// </remarks>
public sealed partial class DocumentStaleResultsStoreTests
{
    private const int Stranger = 7701;

    private int OverlayColumn(SwrRig s) => s.R.Chain.ColumnDefIds[1];

    /// <summary>Ключі карти, що стосуються документів цього сценарію (база спільна з іншими тестами періоду).</summary>
    private static long[] Mine(SwrRig s, IReadOnlyDictionary<long, DateTime?>? map)
        => [.. map!.Keys.Where(k => k == s.R.A || k == s.R.B || k == s.R.Fresh).Order()];

    /// <summary>Правка комірки, як її лишає коміт PATCH: рядок журналу + позначка документа «брудним» (без повної епохи).</summary>
    private async Task DirtyEditAsync(SwrRig s, long documentId, int user = Me, string origin = "UserEdit")
    {
        await EditAsync(documentId, s.R.Period, OverlayColumn(s), Now.AddHours(1), user, origin);
        s.R.Epoch.MarkDirty(s.R.Period, [documentId]);
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<long, DateTime?>?>> MapsAsync(SwrRig s, params int?[] users)
    {
        await using var db = SwrContext(s);
        return await new StaleResultsMapCache(db, s.R.Cache, s.Clock, s.R.Epoch)
            .GetAsync(s.R.Period, users, CancellationToken.None);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Накладка_власна_правка_видна_одразу_без_повної_карти()
    {
        var s = await SwrRigAsync();
        using var _ = s.R.Cache;
        await using var __ = s.Provider;

        Assert.Equal([s.R.A, s.R.B], (await SwrPageAsync(s)).Order());
        Assert.Equal(1, s.Probe.Maps);
        var epochBefore = s.R.Epoch.ValueFor(s.R.Period);

        await DirtyEditAsync(s, s.R.Fresh);

        // Повна епоха не рухалась: карту лікує ОДИН вузький запит, повної карти немає.
        Assert.Equal(epochBefore, s.R.Epoch.ValueFor(s.R.Period));
        Assert.Equal(new[] { s.R.A, s.R.B, s.R.Fresh }.Order(), (await SwrPageAsync(s)).Order());
        Assert.Equal(1, s.Probe.Maps);
        Assert.Equal(1, s.Probe.Deltas);
        Assert.Equal(0, InFlightCount(s.R.Cache));

        // Лікування записане: наступний читач без нових позначок не робить жодного запиту карти.
        Assert.Equal(new[] { s.R.A, s.R.B, s.R.Fresh }.Order(), (await SwrPageAsync(s)).Order());
        Assert.Equal(1, s.Probe.Maps);
        Assert.Equal(1, s.Probe.Deltas);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Накладка_N_правок_між_читаннями_дають_одну_дельту()
    {
        var s = await SwrRigAsync();
        using var _ = s.R.Cache;
        await using var __ = s.Provider;

        Assert.Equal([s.R.A, s.R.B], (await SwrPageAsync(s)).Order());

        await DirtyEditAsync(s, s.R.Fresh);
        await DirtyEditAsync(s, s.R.A, user: 62);
        await DirtyEditAsync(s, s.R.B);

        Assert.Equal(new[] { s.R.A, s.R.B, s.R.Fresh }.Order(), (await SwrPageAsync(s)).Order());
        Assert.Equal(1, s.Probe.Maps);
        Assert.Equal(1, s.Probe.Deltas);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Накладка_знімає_документ_коли_вузький_запит_не_повернув_рядка()
    {
        var s = await SwrRigAsync();
        using var _ = s.R.Cache;
        await using var __ = s.Provider;

        Assert.Equal([s.R.A, s.R.B], (await SwrPageAsync(s)).Order());

        // У B зникли результати прогону (вузький запит його вже не повертає) - запис мусить зникнути з карти.
        await using (var connection = new SqlConnection(sql.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM calc.CalculationResult WHERE DocumentId = @document";
            command.Parameters.AddWithValue("@document", s.R.B);
            await command.ExecuteNonQueryAsync();
        }

        s.R.Epoch.MarkDirty(s.R.Period, [s.R.B]);

        Assert.Equal([s.R.A], (await SwrPageAsync(s)).Order());
        Assert.Equal(1, s.Probe.Maps);
        Assert.Equal(1, s.Probe.Deltas);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Правка_комірки_через_AuditWriter_не_піднімає_повну_епоху_але_видна_одразу()
    {
        var s = await SwrRigAsync();
        using var _ = s.R.Cache;
        await using var __ = s.Provider;
        Assert.Equal([s.R.A, s.R.B], (await SwrPageAsync(s)).Order());

        await using var db = EpochContext(s.R.Epoch);
        var audit = new AuditWriter(db, s.R.Epoch);
        var epochBefore = s.R.Epoch.ValueFor(s.R.Period);
        var seqBefore = s.R.Epoch.DirtySeq(s.R.Period);

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await audit.WriteCellChangesAsync([Change(s, s.R.Fresh, "UserEdit")], CancellationToken.None);

            // До коміту нічого не рухається (відкат не лишає сліду).
            Assert.Equal(seqBefore, s.R.Epoch.DirtySeq(s.R.Period));
            await transaction.CommitAsync();
        }

        Assert.Equal(epochBefore, s.R.Epoch.ValueFor(s.R.Period));
        Assert.Equal(seqBefore + 1, s.R.Epoch.DirtySeq(s.R.Period));
        Assert.Equal(new[] { s.R.A, s.R.B, s.R.Fresh }.Order(), (await SwrPageAsync(s)).Order());
        Assert.Equal(1, s.Probe.Maps);
        Assert.Equal(1, s.Probe.Deltas);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Пакет_Recalculation_не_піднімає_епоху_не_позначає_і_не_робить_запитів()
    {
        var s = await SwrRigAsync();
        using var _ = s.R.Cache;
        await using var __ = s.Provider;
        Assert.Equal([s.R.A, s.R.B], (await SwrPageAsync(s)).Order());

        await using var db = EpochContext(s.R.Epoch);
        var audit = new AuditWriter(db, s.R.Epoch);
        var epochBefore = s.R.Epoch.ValueFor(s.R.Period);
        var seqBefore = s.R.Epoch.DirtySeq(s.R.Period);
        var valueBefore = s.R.Epoch.Value;

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await audit.WriteCellChangesAsync(
                [Change(s, s.R.Fresh, CellChangeOrigins.Recalculation), Change(s, s.R.A, CellChangeOrigins.Recalculation)],
                CancellationToken.None);
            await transaction.CommitAsync();
        }

        Assert.Equal(epochBefore, s.R.Epoch.ValueFor(s.R.Period));
        Assert.Equal(seqBefore, s.R.Epoch.DirtySeq(s.R.Period));
        Assert.Equal(valueBefore, s.R.Epoch.Value);

        Assert.Equal([s.R.A, s.R.B], (await SwrPageAsync(s)).Order());
        Assert.Equal(1, s.Probe.Maps);
        Assert.Equal(0, s.Probe.Deltas);

        // Змішаний пакет (правка людини + перерахунок) позначає лише документ правки людини.
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await audit.WriteCellChangesAsync(
                [Change(s, s.R.Fresh, "UserEdit"), Change(s, s.R.A, CellChangeOrigins.Recalculation)], CancellationToken.None);
            await transaction.CommitAsync();
        }

        Assert.Equal(seqBefore + 1, s.R.Epoch.DirtySeq(s.R.Period));
        Assert.True(s.R.Epoch.TryDirtySince(s.R.Period, seqBefore, 10, out var dirty, out var currentSeq));
        Assert.Equal(seqBefore + 1, currentSeq);
        Assert.Equal([s.R.Fresh], dirty);
    }

    private static CellChangeRecord Change(SwrRig s, long documentId, string origin)
        => new(
            Now.AddHours(1), new CellAddress(s.R.Chain.PeriodKey, 1, s.R.Chain.ColumnDefIds[1]), documentId, "R1",
            "1", "2", Me, origin, IsLateEdit: false, CorrelationId: null);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Повна_епоха_прогону_чи_довідника_скидає_накладку_синхронним_завантаженням()
    {
        var s = await SwrRigAsync();
        using var _ = s.R.Cache;
        await using var __ = s.Provider;
        Assert.Equal([s.R.A, s.R.B], (await SwrPageAsync(s)).Order());

        await DirtyEditAsync(s, s.R.Fresh);
        s.R.Epoch.Bump(s.R.Period);

        Assert.Equal(new[] { s.R.A, s.R.B, s.R.Fresh }.Order(), (await SwrPageAsync(s)).Order());
        Assert.Equal(2, s.Probe.Maps);
        Assert.Equal(0, s.Probe.Deltas);

        // Глобальна епоха (довідник) - так само.
        s.R.Epoch.Bump();
        Assert.Equal(new[] { s.R.A, s.R.B, s.R.Fresh }.Order(), (await SwrPageAsync(s)).Order());
        Assert.Equal(3, s.Probe.Maps);
        Assert.Equal(0, s.Probe.Deltas);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Понад_поріг_брудних_документів_і_усічений_журнал_дають_повне_завантаження()
    {
        var s = await SwrRigAsync();
        using var _ = s.R.Cache;
        await using var __ = s.Provider;
        Assert.Equal([s.R.A, s.R.B], (await SwrPageAsync(s)).Order());

        // MaxIncremental + 1 різних документів між двома читаннями.
        await EditAsync(s.R.Fresh, s.R.Period, OverlayColumn(s), Now.AddHours(1));
        s.R.Epoch.MarkDirty(s.R.Period, Enumerable.Range(0, StaleResultsMapCache.MaxIncremental).Select(i => 9_000_000L + i).Append(s.R.Fresh));

        Assert.Equal(new[] { s.R.A, s.R.B, s.R.Fresh }.Order(), (await SwrPageAsync(s)).Order());
        Assert.Equal(2, s.Probe.Maps);
        Assert.Equal(0, s.Probe.Deltas);

        // Рівно MaxIncremental - ще вузький запит.
        s.R.Epoch.MarkDirty(s.R.Period, Enumerable.Range(0, StaleResultsMapCache.MaxIncremental).Select(i => 9_100_000L + i));
        Assert.Equal(new[] { s.R.A, s.R.B, s.R.Fresh }.Order(), (await SwrPageAsync(s)).Order());
        Assert.Equal(2, s.Probe.Maps);
        Assert.Equal(1, s.Probe.Deltas);

        // Журнал позначок усічено (понад StaleCountsEpoch.MaxDirtyLog): пам'ятати нічого - повне завантаження.
        s.R.Epoch.MarkDirty(s.R.Period, Enumerable.Range(0, StaleCountsEpoch.MaxDirtyLog + 1).Select(i => 9_200_000L + i));
        Assert.Equal(new[] { s.R.A, s.R.B, s.R.Fresh }.Order(), (await SwrPageAsync(s)).Order());
        Assert.Equal(3, s.Probe.Maps);
        Assert.Equal(1, s.Probe.Deltas);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Накладка_лікує_протермінований_запис_і_віддає_вилікувану_карту_з_фоновим_оновленням()
    {
        var s = await SwrRigAsync();
        using var _ = s.R.Cache;
        await using var __ = s.Provider;
        Assert.Equal([s.R.A, s.R.B], (await SwrPageAsync(s)).Order());

        await DirtyEditAsync(s, s.R.Fresh);
        s.Clock.Advance(PastTtl);
        s.Probe.Hold();

        // SWR віддає стару карту, але ВЖЕ вилікувану: власна правка видна, а повний запит іде у фоні.
        var page = await SwrPageAsync(s).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(new[] { s.R.A, s.R.B, s.R.Fresh }.Order(), page.Order());
        Assert.Equal(1, s.Probe.Deltas);

        await s.Probe.WaitMapsAsync(2);
        s.Probe.Release();
        await WaitIdleAsync(s.R.Cache);

        Assert.Equal(2, s.Probe.Maps);
        Assert.Equal(new[] { s.R.A, s.R.B, s.R.Fresh }.Order(), (await SwrPageAsync(s)).Order());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Накладка_не_віддає_чужі_документи_і_кожна_наступна_позначка_лікується_окремо()
    {
        var s = await SwrRigAsync();
        using var _ = s.R.Cache;
        await using var __ = s.Provider;
        Assert.Equal([s.R.A, s.R.B], (await SwrPageAsync(s)).Order());

        await DirtyEditAsync(s, s.R.Fresh);
        Assert.Empty(await SwrPageAsync(s, [s.R.Chain.ProjectId + 1_000_000]));
        Assert.Equal(1, s.Probe.Deltas);

        // Нова позначка після лікування не губиться: ще одне вузьке лікування, а не повна карта.
        await DirtyEditAsync(s, s.R.A, user: 62);
        Assert.Equal(new[] { s.R.A, s.R.B, s.R.Fresh }.Order(), (await SwrPageAsync(s)).Order());
        Assert.Equal(2, s.Probe.Deltas);
        Assert.Equal(1, s.Probe.Maps);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Автори_один_згрупований_запит_на_період_заповнює_ключі_всіх_авторів()
    {
        var s = await SwrRigAsync();
        using var _ = s.R.Cache;
        await using var __ = s.Provider;

        var maps = await MapsAsync(s, null, Me, 62, 424242);

        Assert.Equal([s.R.A, s.R.B], Mine(s, maps[0]));
        Assert.Equal([s.R.A], Mine(s, maps[1]));
        Assert.Equal([s.R.B], Mine(s, maps[2]));
        Assert.NotNull(maps[3]);
        Assert.Empty(maps[3]!);
        Assert.Equal(1, s.Probe.Maps);
        Assert.Equal(1, s.Probe.AuthorMaps);

        // Інші автори - з того самого запису: нових запитів немає.
        var again = await MapsAsync(s, 62, Me, 99);
        Assert.Equal([s.R.B], Mine(s, again[0]));
        Assert.Equal([s.R.A], Mine(s, again[1]));
        Assert.Empty(again[2]!);
        Assert.Equal(1, s.Probe.Maps);
        Assert.Equal(1, s.Probe.AuthorMaps);

        // Накладка лікує запис авторів: правка стороннього автора у Fresh - один вузький запит, а не повний.
        await DirtyEditAsync(s, s.R.Fresh, user: Stranger);
        var healed = await MapsAsync(s, Stranger, Me);
        Assert.Equal([s.R.Fresh], Mine(s, healed[0]));
        Assert.Equal([s.R.A], Mine(s, healed[1]));
        Assert.Equal(1, s.Probe.AuthorMaps);
        Assert.True(s.Probe.Deltas >= 1);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Автори_еквівалентні_запитам_по_автору_поштучно()
    {
        var s = await SwrRigAsync();
        using var _ = s.R.Cache;
        await using var __ = s.Provider;
        var column = OverlayColumn(s);

        // Додаткові випадки: спільний документ (Me раніше, 62 пізніше), Recalculation, межа «рівно початок прогону»,
        // правка іншого періоду, автор без правок.
        await EditAsync(s.R.A, s.R.Period, column, Now.AddHours(3), user: 62);
        await EditAsync(s.R.Fresh, s.R.Period, column, Now.AddHours(1), user: Stranger, origin: "Recalculation");
        await EditAsync(s.R.Fresh, s.R.Period, column, Now, user: Me);
        await EditAsync(s.R.Fresh, s.R.Period + 1, column, Now.AddHours(1), user: Stranger);
        await using var db = SwrContext(s);
        var grouped = (await StaleResultsQuery.AuthorDocuments(db, s.R.Period)
                .OrderBy(r => r.UserId).ThenBy(r => r.DocumentId).Take(1000).ToListAsync())
            .ToLookup(r => r.UserId);

        var users = new[] { Me, 62, Stranger, 424242 };
        foreach (var user in users)
        {
            var single = (await StaleResultsQuery.Documents(db, s.R.Period, user)
                    .OrderBy(r => r.DocumentId).Take(1000).ToListAsync())
                .ToDictionary(r => r.DocumentId, r => r.Since);
            var group = grouped[user].ToDictionary(r => r.DocumentId, r => r.Since);

            Assert.Equal(single.Keys.Order(), group.Keys.Order());
            foreach (var (document, since) in single)
            {
                Assert.Equal(since, group[document]);
            }
        }

        // Recalculation і правка іншого періоду не потрапили в жодну карту автора.
        Assert.Empty(grouped[Stranger]);
        Assert.DoesNotContain(grouped.SelectMany(g => g), r => r.DocumentId == s.R.Fresh);

        // Те саме через кеш: карта кожного автора з запису «автори» дорівнює поштучному запиту.
        var viaCache = await MapsAsync(s, Me, 62, Stranger, 424242);
        for (var i = 0; i < users.Length; i++)
        {
            var expected = grouped[users[i]].Select(r => r.DocumentId).Order().ToArray();
            Assert.Equal(expected, viaCache[i]!.Keys.Order().ToArray());
        }

        // Спільний документ A: у Me - його правка, у 62 - власна (пізніша), а не MIN по авторах.
        Assert.True(grouped[62].Single(r => r.DocumentId == s.R.A).Since > grouped[Me].Single(r => r.DocumentId == s.R.A).Since);
    }
}
