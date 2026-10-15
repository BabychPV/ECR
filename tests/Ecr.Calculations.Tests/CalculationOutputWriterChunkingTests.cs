// tests/Ecr.Calculations.Tests/CalculationOutputWriterChunkingTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// <c>CalculationOutputWriter</c> ріже запис на чанки <c>SaveChanges</c> (15.10: одна довга команда на десятки
/// тисяч рядків впиралась у CommandTimeout під зупинкою I/O), зберігаючи атомарність методології: кілька чанків —
/// ОДНА транзакція; повний результат ідентичний; вихід не ділиться; без перевищення стелі — рівно один
/// <c>SaveChanges</c> без транзакції, як і було.
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЯ: повернути один <c>WriteResultsAsync</c>/<c>SaveChanges</c> на весь набір - тести чанкінгу
/// червоні (1 збереження замість 4); викликати <c>WriteChunkAsync</c> поза <c>ExecuteInTransactionAsync</c> -
/// червоний тест транзакції.
/// </remarks>
public sealed class CalculationOutputWriterChunkingTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Набір_понад_стелю_ріжеться_на_чанки_в_одній_транзакції_а_результат_повний_і_впорядкований()
    {
        var outputs = Outputs(count: 10, valuesEach: 3);
        var (writer, store, uow) = Arrange(maxRows: 10);

        await writer.WriteAsync(1, outputs, TraceLevel.Off, CancellationToken.None);

        // 3 виходи × 3 значення = 9 ≤ 10; четвертий не вміщається → 3 + 3 + 3 + 1.
        Assert.Equal([3, 3, 3, 1], store.ResultChunks.Select(c => c.Count).ToArray());
        Assert.Equal(4, uow.SavedChanges);
        Assert.Equal(1, uow.TransactionsCommitted);
        Assert.Equal(0, uow.SavesOutsideTransaction);

        // Ті самі виходи в тому самому порядку, без втрат і дублів.
        Assert.Equal(outputs, store.ResultChunks.SelectMany(c => c).ToList());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Набір_у_межах_стелі_це_один_SaveChanges_без_транзакції_як_раніше()
    {
        var (writer, store, uow) = Arrange(maxRows: 5_000);

        await writer.WriteAsync(1, Outputs(count: 10, valuesEach: 3), TraceLevel.Off, CancellationToken.None);

        Assert.Single(store.ResultChunks);
        Assert.Equal(1, uow.SavedChanges);
        Assert.Equal(0, uow.TransactionsCommitted);
        Assert.Equal(1, uow.SavesOutsideTransaction);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Один_вихід_більший_за_стелю_не_ділиться()
    {
        var (writer, store, _) = Arrange(maxRows: 2);
        var outputs = Outputs(count: 2, valuesEach: 7);

        await writer.WriteAsync(1, outputs, TraceLevel.Off, CancellationToken.None);

        Assert.Equal([1, 1], store.ResultChunks.Select(c => c.Count).ToArray());
        Assert.All(store.ResultChunks, c => Assert.Equal(7, c[0].Values.Count));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Трейс_рахується_в_розмір_чанка_лише_коли_рівень_його_пише()
    {
        // 1 значення + 4 кроки на вихід: із трейсом 5 рядків (стеля 6 → по одному), без трейсу 1 (по 6).
        var outputs = Outputs(count: 6, valuesEach: 1, traceEach: 4);

        var (withTrace, storeA, _) = Arrange(maxRows: 6);
        await withTrace.WriteAsync(1, outputs, TraceLevel.Full, CancellationToken.None);

        var (withoutTrace, storeB, _) = Arrange(maxRows: 6);
        await withoutTrace.WriteAsync(1, outputs, TraceLevel.Off, CancellationToken.None);

        Assert.Equal(6, storeA.ResultChunks.Count);
        Assert.Single(storeB.ResultChunks);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Збій_у_третьому_чанку_відкочує_транзакцію_і_не_коммітить_нічого()
    {
        var (writer, store, uow) = Arrange(maxRows: 1);
        store.FailOnChunk = 3;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => writer.WriteAsync(1, Outputs(count: 6, valuesEach: 1), TraceLevel.Off, CancellationToken.None));

        Assert.Equal(1, uow.TransactionsRolledBack);
        Assert.Equal(0, uow.TransactionsCommitted);
    }

    private static (CalculationOutputWriter Writer, RecordingStore Store, RecordingUnitOfWork Uow) Arrange(int maxRows)
    {
        var store = new RecordingStore();
        var uow = new RecordingUnitOfWork();

        return (new CalculationOutputWriter(store.Substitute, uow) { MaxRowsPerSave = maxRows }, store, uow);
    }

    private static List<CalculationOutput> Outputs(int count, int valuesEach, int traceEach = 0)
        => [.. Enumerable.Range(1, count).Select(i => new CalculationOutput(
            DocumentId: 7,
            SourceRowKey: $"R{i:000}",
            Values: [.. Enumerable.Range(1, valuesEach)
                .Select(v => new CalculationOutputValue(1, null, $"OUT{v}", i * 10m + v, UnitId: 1))],
            Trace: [.. Enumerable.Range(1, traceEach)
                .Select(s => new CalculationTraceStep(s, $"S{s}", "1+1", 2m, null))]))];

    private sealed class RecordingStore
    {
        public RecordingStore()
        {
            Substitute = NSubstitute.Substitute.For<ICalculationResultStore>();
            Substitute
                .WriteResultsAsync(
                    Arg.Any<long>(), Arg.Any<IReadOnlyList<CalculationOutput>>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    ResultChunks.Add([.. call.ArgAt<IReadOnlyList<CalculationOutput>>(1)]);
                    return ResultChunks.Count == FailOnChunk
                        ? Task.FromException(new InvalidOperationException("збій запису"))
                        : Task.CompletedTask;
                });
        }

        public ICalculationResultStore Substitute { get; }

        public List<List<CalculationOutput>> ResultChunks { get; } = [];

        /// <summary>Номер чанка (з 1), на якому запис падає; 0 — не падає.</summary>
        public int FailOnChunk { get; set; }
    }

    /// <summary>Фейк одиниці роботи: транзакцію веде за правилами EF (коміт — лише без винятку).</summary>
    private sealed class RecordingUnitOfWork : IUnitOfWork
    {
        private bool _inTransaction;

        public int SavedChanges { get; private set; }

        public int SavesOutsideTransaction { get; private set; }

        public int TransactionsCommitted { get; private set; }

        public int TransactionsRolledBack { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken ct)
        {
            SavedChanges++;
            if (!_inTransaction)
            {
                SavesOutsideTransaction++;
            }

            return Task.FromResult(1);
        }

        public Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct)
            => throw new NotSupportedException();

        public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> operation, CancellationToken ct)
        {
            _inTransaction = true;
            try
            {
                await operation(ct).ConfigureAwait(false);
                TransactionsCommitted++;
            }
            catch
            {
                TransactionsRolledBack++;
                throw;
            }
            finally
            {
                _inTransaction = false;
            }
        }
    }
}
