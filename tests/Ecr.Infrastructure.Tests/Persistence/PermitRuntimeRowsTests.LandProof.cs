// tests/Ecr.Infrastructure.Tests/Persistence/PermitRuntimeRowsTests.LandProof.cs
using System.Runtime.CompilerServices;
using Ecr.Application.Common;
using Ecr.Application.Permits.Bindings;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// «Land не зламано» (Permits, PM-10): документ без прив'язок до дозволів поводиться так само, як до PM-10, і платить
/// не більше одного запиту-охоронця. Land-таблиці прив'язок не мають.
/// </summary>
/// <remarks>
/// Мутаційний доказ (прогнано): у <c>PermitBindingService.MaterializeAsync</c> умову охоронця замінено на
/// <c>|| false</c> (без <c>AnyActiveRowsAsync</c>) - червоніють «Повільний_шлях…», «Сервіс_без_прив_язок…» і
/// «Таблиця_без_прив_язки…»; мутацію відкочено. Швидкий шлях не мутувався: сервіс там не викликається взагалі.
/// </remarks>
public sealed partial class PermitRuntimeRowsTests
{
    private static readonly AsyncLocal<StrongBox<bool>?> Measuring = new();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Швидкий_шлях_EnsureTableInstances_без_прив_язок_не_змінився_ні_поведінкою_ні_числом_запитів()
    {
        // Екземпляри вже є - нічого створювати: до PM-10 і після PM-10 рівно те саме читання, сервіс не викликається.
        var before = await BuildLandLikeAsync(withInstances: true);
        var after = await BuildLandLikeAsync(withInstances: true);

        var (createdBefore, withoutService) = await MeasureEnsureAsync(before, permits: false);
        var (createdAfter, withService) = await MeasureEnsureAsync(after, permits: true);

        Assert.Equal(0, createdBefore);
        Assert.Equal(0, createdAfter);
        Assert.Equal(withoutService.Total, withService.Total);
        Assert.Equal(0, withService["SELECT cfg.PermitSheetBinding"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Повільний_шлях_EnsureTableInstances_без_прив_язок_коштує_рівно_один_запит_охоронця_понад_колишнє_число()
    {
        // Екземплярів немає: створення йде повільним шляхом. Базова лінія - той самий сценарій без сервісу дозволів
        // (RowStore до PM-10, permits = null); з сервісом дозволено РІВНО +1 запит - охоронець по cfg.PermitSheetBinding.
        var baselineScene = await BuildLandLikeAsync(withInstances: false);
        var scene = await BuildLandLikeAsync(withInstances: false);

        var (createdBaseline, baseline) = await MeasureEnsureAsync(baselineScene, permits: false);
        var (created, withGuard) = await MeasureEnsureAsync(scene, permits: true);

        Assert.Equal(1, createdBaseline);
        Assert.Equal(1, created);
        Assert.Equal(0, baseline["SELECT cfg.PermitSheetBinding"]);
        Assert.Equal(1, withGuard["SELECT cfg.PermitSheetBinding"]);
        Assert.True(
            withGuard.Total == baseline.Total + 1,
            $"Очікувалось {baseline.Total} + 1 запит-охоронець, є {withGuard.Total}.\n--- з сервісом:\n{withGuard.Format()}\n--- без:\n{baseline.Format()}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Сервіс_без_прив_язок_нічого_не_пише_не_журналює_і_не_вантажить_знімки()
    {
        var scene = await BuildLandLikeAsync(withInstances: true);
        var rowsBefore = await CountAsync(scene.Builder, "doc.TableRow", scene.Chain.DocumentId);
        var cellsBefore = await CountAsync(scene.Builder, "doc.CellValue", scene.Chain.DocumentId);

        var store = Substitute.For<IPermitBindingStore>();
        store.AnyActiveRowsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>()).Returns(false);
        var resolver = Substitute.For<IPermitResolver>();
        var types = Substitute.For<IPermitTypeStore>();
        var snapshots = Substitute.For<IRegistrySnapshotLoader>();
        var headers = Substitute.For<IDocumentHeaderStore>();
        var registries = Substitute.For<IRegistryStore>();
        var periods = Substitute.For<IPeriodStore>();
        var cells = Substitute.For<ICellStore>();
        var audit = Substitute.For<IAuditWriter>();

        await using var db = scene.Builder.CreateContext();
        var service = new PermitBindingService(
            db, new BulkCellLoader(sql.ConnectionString, 1000), store, types, resolver, snapshots, headers, registries,
            periods, cells, audit, new TestClock(Now));

        var result = await service.MaterializeAsync(scene.Chain.DocumentId, scene.Chain.PeriodKey, [scene.Chain.TableDefId], default);
        var empty = await service.MaterializeAsync(scene.Chain.DocumentId, scene.Chain.PeriodKey, [], default);

        Assert.Equal(PermitMaterialization.None, result);
        Assert.Equal(PermitMaterialization.None, empty);

        // Єдине звернення до залежностей - запит-охоронець (і лише для непорожнього набору таблиць).
        Assert.Single(store.ReceivedCalls());
        Assert.Equal(nameof(IPermitBindingStore.AnyActiveRowsAsync), store.ReceivedCalls().Single().GetMethodInfo().Name);
        Assert.Empty(resolver.ReceivedCalls());
        Assert.Empty(types.ReceivedCalls());
        Assert.Empty(snapshots.ReceivedCalls());
        Assert.Empty(headers.ReceivedCalls());
        Assert.Empty(registries.ReceivedCalls());
        Assert.Empty(periods.ReceivedCalls());
        Assert.Empty(cells.ReceivedCalls());
        Assert.Empty(audit.ReceivedCalls());
        Assert.False(db.ChangeTracker.HasChanges());

        Assert.Equal(rowsBefore, await CountAsync(scene.Builder, "doc.TableRow", scene.Chain.DocumentId));
        Assert.Equal(cellsBefore, await CountAsync(scene.Builder, "doc.CellValue", scene.Chain.DocumentId));
        Assert.Equal(0, await CountAsync(scene.Builder, "aud.CellChange", scene.Chain.DocumentId));
    }

    // ── Побудова й вимір ───────────────────────────────────────────────────

    private sealed record LandLike(TestDocumentBuilder Builder, TestDocument Chain);

    private async Task<LandLike> BuildLandLikeAsync(bool withInstances)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(rowCount: 3, rowMode: TableRowMode.Fixed, ct: CancellationToken.None);
        await using var prep = builder.CreateContext();
        prep.DocumentSheets.Add(new DocumentSheet(chain.DocumentId, chain.SheetDefId));
        await prep.SaveChangesAsync();
        if (!withInstances)
        {
            await prep.TableRows.Where(r => r.PeriodKeyValue == chain.PeriodKey.Value && r.TableInstanceId == chain.TableInstanceId).ExecuteDeleteAsync();
            await prep.TableInstances.Where(i => i.DocumentId == chain.DocumentId).ExecuteDeleteAsync();
        }

        return new LandLike(builder, chain);
    }

    private async Task<(int Created, CommandTallySnapshot Seen)> MeasureEnsureAsync(LandLike scene, bool permits)
    {
        await using var db = scene.Builder.CreateContext();
        var store = NewStore(db, permits);

        var texts = new List<string>();
        using var counter = new SqlClientCommandCounter(new CommandTally(), command =>
        {
            if (Measuring.Value is not { Value: true })
            {
                return false;
            }

            lock (texts)
            {
                texts.Add(command.CommandText ?? string.Empty);
            }

            return true;
        });

        counter.Tally.Reset();
        var flag = new StrongBox<bool>(true);
        Measuring.Value = flag;
        int created;
        try
        {
            created = await store.EnsureTableInstancesAsync(scene.Chain.DocumentId, scene.Chain.PeriodKey, default);
        }
        finally
        {
            flag.Value = false;
            Measuring.Value = null;
        }

        counter.AssertObserved();
        return (created, counter.Tally.Snapshot());
    }

    private static async Task<int> CountAsync(TestDocumentBuilder builder, string table, long documentId)
    {
        await using var db = builder.CreateContext();
        var text = table switch
        {
            "doc.TableRow" => "SELECT COUNT(*) AS Value FROM doc.TableRow r JOIN doc.TableInstance i ON i.Id = r.TableInstanceId AND i.PeriodKey = r.PeriodKey WHERE i.DocumentId = @d",
            "doc.CellValue" => "SELECT COUNT(*) AS Value FROM doc.CellValue c JOIN doc.TableRow r ON r.Id = c.TableRowId AND r.PeriodKey = c.PeriodKey JOIN doc.TableInstance i ON i.Id = r.TableInstanceId AND i.PeriodKey = r.PeriodKey WHERE i.DocumentId = @d",
            _ => "SELECT COUNT(*) AS Value FROM aud.CellChange WHERE DocumentId = @d",
        };

        return await db.Database.SqlQueryRaw<int>(text, new Microsoft.Data.SqlClient.SqlParameter("d", documentId)).SingleAsync();
    }
}
