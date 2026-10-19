// tests/Ecr.Infrastructure.Tests/Persistence/PermitRuntimeRowsTests.QueryCount.cs
using System.Runtime.CompilerServices;
using Ecr.Application.Permits.Bindings;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Число команд SQL не росте з числом рядків дозволу (Permits, PM-09/PM-10, аудит B7): створення рядків документа,
/// «Оновити з дозволу» і перегляд прив'язки читають і пишуть набором, а не рядок за рядком.
/// </summary>
/// <remarks>
/// ⚠ Точна рівність - для 5 і 40 рядків. Єдине, що росте далі, - число пакетів EF для вставки <c>doc.TableRow</c>
/// (по 42 команди в пакеті): на 50 рядках це два <c>MERGE</c> замість одного; решта запитів рівна (окремий тест).
///
/// Мутаційний доказ: у <c>PermitBindingService.InsertRowsAsync</c> замінити пакетне <c>cells.ApplyAsync</c> на виклик
/// у циклі по рядках плану - червоніють «Створення_рядків…» і «Оновлення…».
/// </remarks>
public sealed partial class PermitRuntimeRowsTests
{
    private const int FewRows = 5;
    private const int ManyRows = 40;

    // EF ріже вставку на пакети по 42 команди (MaxBatchSize): на 50 рядках "MERGE doc.TableRow" виконується двічі, а не раз.
    private const int PastEfBatchRows = 50;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Створення_рядків_з_дозволу_має_те_саме_число_запитів_для_5_і_40_рядків()
    {
        var few = await new PermitRuntimeRowsTests(sql).MeasureMaterializeAsync(FewRows);
        var many = await new PermitRuntimeRowsTests(sql).MeasureMaterializeAsync(ManyRows);

        Assert.Equal(FewRows, few.Rows);
        Assert.Equal(ManyRows, many.Rows);
        Assert.True(
            few.Seen.Total == many.Seen.Total,
            $"Запитів: {few.Seen.Total} для {FewRows} рядків і {many.Seen.Total} для {ManyRows}.\n--- {FewRows}:\n{few.Seen.Format()}\n--- {ManyRows}:\n{many.Seen.Format()}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Понад_пакет_EF_у_42_рядки_зростає_лише_число_пакетів_вставки_рядків_а_читання_й_запис_комірок_не_змінюються()
    {
        var few = await new PermitRuntimeRowsTests(sql).MeasureMaterializeAsync(FewRows);
        var past = await new PermitRuntimeRowsTests(sql).MeasureMaterializeAsync(PastEfBatchRows);

        const string rowInserts = "MERGE doc.TableRow";
        Assert.Equal(1, few.Seen[rowInserts]);
        Assert.Equal(2, past.Seen[rowInserts]);
        Assert.True(
            few.Seen.Total - few.Seen[rowInserts] == past.Seen.Total - past.Seen[rowInserts],
            $"Окрім вставки рядків запитів має бути порівну.\n--- {FewRows}:\n{few.Seen.Format()}\n--- {PastEfBatchRows}:\n{past.Seen.Format()}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Оновлення_з_дозволу_має_те_саме_число_запитів_для_5_і_40_рядків()
    {
        var few = await new PermitRuntimeRowsTests(sql).MeasureRefreshAsync(FewRows);
        var many = await new PermitRuntimeRowsTests(sql).MeasureRefreshAsync(ManyRows);

        Assert.Equal((FewRows, ManyRows), (few.Added, many.Added));
        Assert.True(
            few.Seen.Total == many.Seen.Total,
            $"Запитів: {few.Seen.Total} для {FewRows} рядків і {many.Seen.Total} для {ManyRows}.\n--- {FewRows}:\n{few.Seen.Format()}\n--- {ManyRows}:\n{many.Seen.Format()}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перегляд_прив_язки_має_те_саме_число_запитів_для_5_і_40_рядків()
    {
        var few = await new PermitRuntimeRowsTests(sql).MeasurePreviewAsync(FewRows);
        var many = await new PermitRuntimeRowsTests(sql).MeasurePreviewAsync(ManyRows);

        Assert.Equal((FewRows, ManyRows), (few.Rows, many.Rows));
        Assert.True(
            few.Seen.Total == many.Seen.Total,
            $"Запитів: {few.Seen.Total} для {FewRows} рядків і {many.Seen.Total} для {ManyRows}.\n--- {FewRows}:\n{few.Seen.Format()}\n--- {ManyRows}:\n{many.Seen.Format()}");
    }

    private async Task<(int Rows, CommandTallySnapshot Seen)> MeasureMaterializeAsync(int permitRows)
    {
        var scene = await ArrangeAsync(permitRows: permitRows);
        await using var _ = scene.H;
        await using (var db = scene.NewContext())
        {
            await NewStore(db, permits: false).EnsureTableInstancesAsync(scene.DocumentId, January, default);
        }

        await using var direct = scene.NewContext();
        var service = NewService(direct);
        var (result, seen) = await MeasureAsync(
            () => service.MaterializeAsync(scene.DocumentId, January, [scene.Sheet.Doc.TableDefId], default));

        return (result.Rows, seen);
    }

    private async Task<(int Added, CommandTallySnapshot Seen)> MeasureRefreshAsync(int permitRows)
    {
        var scene = await ArrangeAsync(permitRows: permitRows);
        await using var _ = scene.H;
        await using (var db = scene.NewContext())
        {
            await NewStore(db, permits: false).EnsureTableInstancesAsync(scene.DocumentId, January, default);
        }

        await using var read = scene.NewContext();
        var view = (await new PermitBindingStore(read).GetAsync(scene.Sheet.Doc.TemplateVersionId, $"B{_tag}", default))!;
        var instanceId = await scene.InstanceIdAsync();

        await using var db2 = scene.NewContext();
        var service = NewService(db2);
        var plan = await service.PlanAsync(view, scene.DocumentId, new DateOnly(2026, 1, 31), default);
        var (result, seen) = await MeasureAsync(
            () => service.RefreshAsync(scene.DocumentId, January, instanceId, view, plan, default));

        return (result.Added, seen);
    }

    private async Task<(int Rows, CommandTallySnapshot Seen)> MeasurePreviewAsync(int permitRows)
    {
        var scene = await ArrangeAsync(permitRows: permitRows);
        await using var _ = scene.H;
        await using var request = await scene.H.NextRequestAsync();

        var (preview, seen) = await MeasureAsync(
            () => request.Preview.HandleAsync(
                scene.Sheet.Doc.TemplateVersionId, $"B{_tag}", new PermitPreviewQuery(null, null, "Site A", new DateOnly(2026, 1, 31), null), default));

        return (preview.Rows.Count, seen);
    }

    /// <summary>Рахує команди SQL, випущені дією (лише її потоком виконання - фонові задачі не потрапляють).</summary>
    private static async Task<(T Result, CommandTallySnapshot Seen)> MeasureAsync<T>(Func<Task<T>> action)
    {
        using var counter = new SqlClientCommandCounter(new CommandTally(), _ => Measuring.Value is { Value: true });

        counter.Tally.Reset();
        var flag = new StrongBox<bool>(true);
        Measuring.Value = flag;
        T result;
        try
        {
            result = await action();
        }
        finally
        {
            flag.Value = false;
            Measuring.Value = null;
        }

        counter.AssertObserved();
        return (result, counter.Tally.Snapshot());
    }
}
