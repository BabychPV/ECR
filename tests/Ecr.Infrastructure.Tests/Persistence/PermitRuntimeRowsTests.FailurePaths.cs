// tests/Ecr.Infrastructure.Tests/Persistence/PermitRuntimeRowsTests.FailurePaths.cs
using Ecr.Application.Permits.Bindings;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Шляхи збою рантайму прив'язок (Permits, PM-10, аудит B17): збій ПЛАНУВАННЯ (читання дозволу) не ламає відкриття
/// документа, а збій ЗАПИСУ - ламає й відкочує все разом із щойно створеними екземплярами.
/// </summary>
/// <remarks>
/// Мутаційні докази: прибрати <c>try/catch</c> навколо <c>PlanCoreAsync</c> у <c>MaterializeAsync</c> - червоніє
/// «Збій_планування…» (виняток дійшов би до відкриття); загорнути <c>audit.WriteCellChangesAsync</c> у порожній
/// <c>catch</c> - червоніє «Збій_запису…» (екземпляри лишилися б, а рядки без аудиту).
/// </remarks>
public sealed partial class PermitRuntimeRowsTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Збій_планування_дозволу_лишає_екземпляр_без_рядків_з_попередженням_а_відкриття_документа_проходить()
    {
        var scene = await ArrangeAsync();
        await using var _ = scene.H;

        var resolver = Substitute.For<IPermitResolver>();
        resolver.ResolveAsync(Arg.Any<PermitResolveRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("довідник дозволів недоступний"));

        // Відкриття документа: екземпляр створено й збережено, виняток назовні не вийшов.
        await using var db = scene.NewContext();
        var created = await new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), new TestClock(Now), permits: ServiceWith(db, resolver))
            .EnsureTableInstancesAsync(scene.DocumentId, January, default);

        Assert.Equal(1, created);
        Assert.NotEqual(0, await scene.InstanceIdAsync());
        Assert.Empty(await scene.RowsAsync());
        Assert.Equal(0, await scene.PermitAuditCountAsync());

        // Попередження є (його журналює сервіс): ім'я прив'язки й тип збою, а не текст винятку з даними.
        await using var direct = scene.NewContext();
        var result = await ServiceWith(direct, resolver)
            .MaterializeAsync(scene.DocumentId, January, [scene.Sheet.Doc.TableDefId], default);
        Assert.Equal(0, result.Rows);
        var warning = Assert.Single(result.Warnings, w => w.Case == "planFailed");
        Assert.Contains($"B{_tag}", warning.Text, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), warning.Text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Збій_запису_рядків_з_дозволу_виходить_назовні_і_відкочує_екземпляри_й_рядки()
    {
        var scene = await ArrangeAsync();
        await using var _ = scene.H;

        var audit = Substitute.For<IAuditWriter>();
        audit.WriteCellChangesAsync(Arg.Any<IReadOnlyList<CellChangeRecord>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("аудит недоступний"));

        await using var db = scene.NewContext();
        var service = new PermitBindingService(
            db, new BulkCellLoader(sql.ConnectionString, 1000), new PermitBindingStore(db), new PermitTypeStore(db),
            new PermitResolver(new RegistryStore(db)), new RegistrySnapshotLoader(db), new DocumentHeaderStore(db),
            new RegistryStore(db), new PeriodStore(db), new NormalizedCellStore(db), audit, new TestClock(Now));
        var store = new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), new TestClock(Now), permits: service);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.EnsureTableInstancesAsync(scene.DocumentId, January, default));
        Assert.Equal("аудит недоступний", failure.Message);

        // Транзакція відкочена цілком: ні екземпляра, ні рядків, ні комірок.
        await using var check = scene.NewContext();
        Assert.False(await check.TableInstances.AsNoTracking().AnyAsync(i => i.DocumentId == scene.DocumentId));
        Assert.Empty(await scene.RowsAsync());

        // Повторне відкриття (аудит уже працює) створює все наново.
        await using var again = scene.NewContext();
        Assert.Equal(1, await NewStore(again, permits: true).EnsureTableInstancesAsync(scene.DocumentId, January, default));
        Assert.Equal(scene.Permit.RowEntryIds.Select(id => $"P{id}"), (await scene.RowsAsync()).Select(r => r.RowKey));
    }

    private PermitBindingService ServiceWith(EcrDbContext db, IPermitResolver resolver)
    {
        var registries = new RegistryStore(db);
        return new PermitBindingService(
            db, new BulkCellLoader(sql.ConnectionString, 1000), new PermitBindingStore(db), new PermitTypeStore(db), resolver,
            new RegistrySnapshotLoader(db), new DocumentHeaderStore(db), registries, new PeriodStore(db),
            new NormalizedCellStore(db), new AuditWriter(db), new TestClock(Now));
    }
}
